using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MouseBatteryTray;

public sealed class BatteryService : IDisposable
{
    private readonly SettingsConfig _config;
    private readonly string _nativeExePath;
    private readonly System.Threading.Timer _timer;
    private bool _isDisposed;
    private bool _hasAlertedLow;
    private bool _hasAlertedCritical;

    /// <summary>轮询重入闸：1 表示已有原生读取进程在跑。见 PollBattery 的注释。</summary>
    private int _polling;

    public event Action<BatterySnapshot>? SnapshotUpdated;
    public event Action<string, string>? AlertTriggered;

    public BatterySnapshot CurrentSnapshot { get; private set; } = new();

    // ---- 历史样本缓存 ----
    // 原实现每次轮询都要 File.ReadAllText + JsonDocument.Parse 整个 history.json，
    // 且追加样本时再把整个文件重写一遍。文件只增不减，开销随之线性放大，
    // 长时间运行后每次轮询的临时分配和磁盘 IO 都会变得很重。
    // 现在改为：启动时解析一次进内存，之后只在内存里追加，并按需落盘。
    private readonly List<(double T, int P, bool C)> _samples = new();
    private bool _samplesLoaded;
    private string? _historyPath;
    private DateTime _lastPersistUtc = DateTime.MinValue;

    /// <summary>落盘节流间隔：内存里可以频繁追加，磁盘不必每次都跟着重写。</summary>
    private static readonly TimeSpan PersistInterval = TimeSpan.FromMinutes(5);

    /// <summary>保留窗口。只用于图表与续航预测，留 48 小时足够，且能防止文件无限增长。</summary>
    private const double RetainSeconds = 48 * 3600;

    public BatteryService(SettingsConfig config)
    {
        _config = config;

        // 定位原生读取程序（同目录或上一级 native\build）
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string p1 = Path.Combine(baseDir, "mouse-tray.exe");
        string p2 = Path.Combine(baseDir, "..", "..", "..", "..", "native", "build", "mouse-tray.exe");
        string p3 = Path.GetFullPath(p2);

        _nativeExePath = File.Exists(p1) ? p1 : (File.Exists(p3) ? p3 : "mouse-tray.exe");

        // Program 在注册事件后会主动 RefreshNow；延迟定时器首轮，避免启动时并发访问 HID++ 接收器。
        _timer = new System.Threading.Timer(
            OnTimerTick, null,
            TimeSpan.FromSeconds(_config.Interval),
            TimeSpan.FromSeconds(_config.Interval));
    }

    public void RefreshNow()
    {
        Task.Run(PollBattery);
    }

    private void OnTimerTick(object? state)
    {
        PollBattery();
    }

    private void PollBattery()
    {
        if (_isDisposed) return;

        // 同一时刻只允许一个原生进程在跑。
        //
        // 为什么必须串行：Windows 把一个 HID 集合的输入报告只投递给**其中一个**
        // 持有句柄的进程。若定时轮询与「打开卡片时的立即刷新」同时各起一个
        // mouse-tray.exe，两个进程会互相抢走对方的应答，结果双双读失败 ——
        // 表现就是用户刚唤醒鼠标、点开卡片，反而又把这一轮读砸了。
        // 这里的做法是让后来者直接放弃：上一次轮询刚跑完（现在只要几十毫秒），
        // 紧接着的下一次定时轮询自然会带上最新值。
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
        {
            return;
        }

        try
        {
            PollBatteryCore();
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void PollBatteryCore()
    {
        if (_isDisposed) return;

        // 一次读取失败时可能仍能从历史恢复电量，型号也应保留最近一次成功读取的值。
        string deviceName = string.IsNullOrWhiteSpace(CurrentSnapshot.DeviceName)
            ? "罗技设备"
            : CurrentSnapshot.DeviceName;
        int percent = -1;
        bool isCharging = false;
        string statusText = "放电中";
        string levelText = "良好";

        // 原生进程的退出码是「设备此刻是否可达」唯一可靠的信号：
        //   0 = 读到了电量；2 = 整轮都没读到（设备休眠/离线）；
        //   null = 进程超时未收尾或根本没启动 —— 未知，交给历史按样本年龄判断。
        // 不能再用「样本年龄」单独推断休眠：那会让一次读取失败在整整
        // StaleGrace(300s) 内都显示「已休眠」，即使设备早已被唤醒。
        bool? reachable = null;

        // 1. 调用原生 reader 读取最新电量 (必须显式指定 UTF-8 编码，防止 Windows 中文下 · 字符被 GBK 替换导致截断为 0%)
        try
        {
            if (File.Exists(_nativeExePath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _nativeExePath,
                    Arguments = "--once",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    string output = p.StandardOutput.ReadToEnd();

                    // 原生读取器的预算已从 1.5s 抬到 3s（要给唤醒中的设备留出
                    // 首帧 650~850ms + 若干次往返的时间），因此这里的兜底等待
                    // 必须跟着放宽，否则会在读取器正常收尾前就返回，白白丢掉
                    // 本轮已经读到的电量。
                    if (p.WaitForExit(5000))
                    {
                        switch (p.ExitCode)
                        {
                            case 0:
                                reachable = true;
                                break;
                            case 2:
                                reachable = false;
                                break;
                        }
                    }

                    // 原生读取器每行格式："设备名: 89% · 放电中 · 良好"
                    // 电量必须只在**冒号之后**的部分里找：设备名本身可能含
                    // 数字和百分号（例如 "G502 100% Edition"），若整行匹配就会
                    // 先命中设备名里的 100%，把电量读错。
                    foreach (string rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string line = rawLine.Trim();
                        int colon = line.IndexOf(':');
                        if (colon <= 0)
                        {
                            continue;
                        }

                        deviceName = line[..colon].Trim();
                        string tail = line[(colon + 1)..];

                        var pctMatch = Regex.Match(tail, @"(\d{1,3})%");
                        if (!pctMatch.Success)
                        {
                            continue;
                        }

                        if (int.TryParse(pctMatch.Groups[1].Value, out int pVal) && pVal > 0 && pVal <= 100)
                        {
                            percent = pVal;
                        }

                        // 状态与档位文字只看冒号之后的电量区，避免设备名里的
                        // 字（如 "PRO"、"满"）干扰判断。
                        //
                        // 注意顺序：「已充满」里并不含「充电」这个连续子串，
                        // 若先判「充电」会漏掉满电状态，把插着线的鼠标显示成
                        // 放电中，因此必须把充满放在最前面判定。
                        if (tail.Contains("充满"))
                        {
                            statusText = "已充满";
                            isCharging = false;
                            levelText = "满";
                        }
                        else if (tail.Contains("充电"))
                        {
                            statusText = "充电中";
                            isCharging = true;
                        }
                        else if (tail.Contains("满"))
                        {
                            statusText = "放电中";
                            levelText = "满";
                        }
                        else if (tail.Contains("良好"))
                        {
                            levelText = "良好";
                        }
                        break;
                    }
                }
            }
        }
        catch { }

        // 2. 解析 history.json，提取 24 小时时段分析与续航预测
        var snapshot = BuildSnapshotFromHistory(deviceName, percent, isCharging, statusText, levelText, reachable);
        CurrentSnapshot = snapshot;

        // 3. 低电量提醒判断
        CheckAlerts(snapshot);

        SnapshotUpdated?.Invoke(snapshot);
    }

    private BatterySnapshot BuildSnapshotFromHistory(
        string deviceName, int percent, bool isCharging, string statusText, string levelText,
        bool? reachable = null)
    {
        var snapshot = new BatterySnapshot
        {
            DeviceName = deviceName,
            Percent = percent > 0 ? percent : -1,
            IsCharging = isCharging,
            IsConnected = percent > 0,
            LevelText = levelText,
            LastUpdatedText = DateTime.Now.ToString("HH:mm")
        };

        try
        {
            EnsureSamplesLoaded();

            // 设备离线时 _samples 仍可能持有旧版遗留的超大历史，
            // 因此这里也要走一次裁剪与落盘，否则文件永远不会被压缩。
            double nowUtcEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (_samples.Count == 0)
            {
                // 没有任何历史样本：直接返回，下面的兜底文案稍后统一处理
            }
            else
            {
                double nowEpoch = nowUtcEpoch;

                if (percent > 0)
                {
                    // 距离上个样本超过 20 秒才记录，避免密集轮询把文件撑大
                    if (nowEpoch - _samples[^1].T >= 20)
                    {
                        _samples.Add((nowEpoch, percent, isCharging));
                    }
                }
                else
                {
                    // 本次休眠/未读到：平滑回退到最近的有效历史，绝不显示 0% 假报警
                    var latest = _samples[^1];
                    snapshot.Percent = latest.P;
                    snapshot.IsCharging = latest.C;
                    double elapsed = nowEpoch - latest.T;

                    // 这里仍按「样本年龄」判断，而不是一失败就立刻判休眠：
                    // 单次读取失败可能是短暂的（设备正在被唤醒、G HUB 抢走了这一轮
                    // 应答……），StaleGrace 就是留给这种抖动的容忍窗口，贸然改成
                    // 「一次失败即休眠」会让卡片在设备好好的时候闪成「已休眠」。
                    //
                    // 「已唤醒却仍显示休眠」的真正病根在原生读取器的预算分配，
                    // 已由 fail-fast + 3 秒预算修掉：空槽位现在几毫秒内被回绝，
                    // 唤醒中的设备拿得到它需要的 650~850ms。原生一旦读到电量，
                    // percent > 0 就不会走到这个分支，状态自然立刻恢复。
                    snapshot.IsConnected = elapsed < _config.StaleGrace;

                    // 唯一的例外：原生进程明确回了「读到了」（exit 0），说明设备此刻
                    // 确实可达。正常情况下 exit 0 必然带回了百分比，走不到这个分支；
                    // 但若原生读到了读数而本方法解析 stdout 失败（格式变动、编码异常），
                    // percent 会停在 -1，此时绝不能再叫「已休眠」。
                    //
                    // 刻意**不用** reachable == false 去强制休眠：那是单向的另一半，
                    // 会让一次失败立刻把卡片打成「已休眠」，正是本缺陷要修的表现。
                    if (reachable == true)
                    {
                        snapshot.IsConnected = true;
                    }

                    if (!snapshot.IsConnected)
                    {
                        snapshot.LevelText = "已休眠";
                    }
                }

                PruneOldSamples(nowEpoch);
                MaybePersist(nowEpoch);

                BuildHistoryAnalysis(snapshot, nowEpoch);
            }
        }
        catch { }

        if (string.IsNullOrEmpty(snapshot.RemainingTimeText) || snapshot.RemainingTimeText == "正在估算...")
        {
            snapshot.RemainingTimeText = snapshot.IsCharging ? "充电中" : (snapshot.Percent > 0 ? "放电数据积累中" : "设备离线");
        }

        return snapshot;
    }

    /// <summary>首次调用时把 history.json 读进内存，之后不再重复解析。</summary>
    private void EnsureSamplesLoaded()
    {
        if (_samplesLoaded)
        {
            return;
        }

        _samplesLoaded = true;

        try
        {
            string histDir = AppIdentity.DataDirectory;
            string histPath = Path.Combine(histDir, "history.json");

            // 兼容旧版本目录：新路径没有而旧路径有，则迁移一次。
            // 轻量版不设旧目录，因此不会与完整版的历史相互污染。
            if (!File.Exists(histPath))
            {
                string? legacy = AppIdentity.LegacyDirectory;
                string? oldPath = legacy == null ? null : Path.Combine(legacy, "history.json");
                if (oldPath != null && File.Exists(oldPath))
                {
                    try
                    {
                        Directory.CreateDirectory(histDir);
                        File.Copy(oldPath, histPath, true);
                    }
                    catch { histPath = oldPath; }
                }
            }

            _historyPath = histPath;

            if (!File.Exists(histPath))
            {
                return;
            }

            using var stream = File.OpenRead(histPath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("samples", out var samplesArr))
            {
                return;
            }

            foreach (var elem in samplesArr.EnumerateArray())
            {
                if (!elem.TryGetProperty("t", out var tProp) ||
                    !elem.TryGetProperty("percent", out var pProp))
                {
                    continue;
                }

                int p = pProp.GetInt32();
                if (p <= 0 || p > 100)
                {
                    continue;
                }

                bool c = elem.TryGetProperty("charging", out var cProp) && cProp.GetBoolean();
                _samples.Add((tProp.GetDouble(), p, c));
            }

            // 旧版本累积的历史可能很大：加载后立刻按保留窗口裁剪。
            // 分桶用的是单调前进的指针，因此这里也确保时间升序。
            if (_samples.Count > 1 && !IsAscending(_samples))
            {
                _samples.Sort(static (a, b) => a.T.CompareTo(b.T));
            }

            PruneOldSamples(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        catch { }
    }

    private static bool IsAscending(List<(double T, int P, bool C)> list)
    {
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i].T < list[i - 1].T)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 丢弃保留窗口之外的旧样本，防止内存与文件无限增长。
    ///
    /// 但**永远保留最新那一个样本**：它是「休眠前最后一次电量」的唯一来源
    /// （见 BuildSnapshotFromHistory 里 percent &lt;= 0 的回退分支）。
    /// 若把样本裁光，设备休眠超过 48 小时后卡片就退化成「--%」+「设备离线」，
    /// 连「睡前还剩多少」都看不到 —— 而这正是离线时唯一还有参考价值的信息。
    /// 那一个样本同时会被写回文件，因此跨重启也不会丢。
    /// </summary>
    private void PruneOldSamples(double nowEpoch)
    {
        double cutoff = nowEpoch - RetainSeconds;
        int drop = 0;
        while (drop < _samples.Count && _samples[drop].T < cutoff)
        {
            drop++;
        }

        // 全部过期时最多裁到只剩最后一个，绝不裁空。
        if (drop >= _samples.Count)
        {
            drop = _samples.Count - 1;
        }

        if (drop > 0)
        {
            _samples.RemoveRange(0, drop);
        }
    }

    /// <summary>
    /// 按节流间隔把内存样本整体写回文件。
    /// 原来每记录一个样本就 ReadAllText + 字符串拼接 + WriteAllText 整个文件，
    /// 现在改为低频整体覆盖写，磁盘 IO 从「每次轮询」降到「每 5 分钟」。
    /// </summary>
    private void MaybePersist(double nowEpoch)
    {
        if (_historyPath == null)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if (nowUtc - _lastPersistUtc < PersistInterval)
        {
            return;
        }

        _lastPersistUtc = nowUtc;

        try
        {
            var sb = new StringBuilder(_samples.Count * 60 + 64);
            sb.Append("{\n \"samples\": [\n");
            for (int i = 0; i < _samples.Count; i++)
            {
                var s = _samples[i];
                sb.Append("   {\"t\": ").Append(s.T.ToString("F3", CultureInfo.InvariantCulture))
                  .Append(", \"percent\": ").Append(s.P)
                  .Append(", \"charging\": ").Append(s.C ? "true" : "false")
                  .Append('}');
                if (i < _samples.Count - 1)
                {
                    sb.Append(',');
                }
                sb.Append('\n');
            }
            sb.Append(" ]\n}\n");

            string? dir = Path.GetDirectoryName(_historyPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 必须显式用「无 BOM」的 UTF8：Encoding.UTF8 会写入 BOM，
            // 使文件不再是标准 JSON（Python 等解析器会直接报错）。
            File.WriteAllText(_historyPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>
    /// 24 小时聚合与续航预测。原来的实现针对每个小时都做一次
    /// samples.Where(...).ToList()，即 24 次全量扫描；这里改为一次遍历分桶。
    /// </summary>
    private void BuildHistoryAnalysis(BatterySnapshot snapshot, double nowEpoch)
    {
        double dayAgo = nowEpoch - 24 * 3600;

        // 单遍扫描：同时求出 24h 区间的最值、放电段起止
        int minP = int.MaxValue;
        int maxP = int.MinValue;
        double firstDischargeT = 0, lastDischargeT = 0;
        int firstDischargeP = 0, lastDischargeP = 0;
        bool hasDischarge = false;

        foreach (var s in _samples)
        {
            if (s.T >= dayAgo)
            {
                if (s.P < minP) minP = s.P;
                if (s.P > maxP) maxP = s.P;

                if (!s.C)
                {
                    if (!hasDischarge)
                    {
                        firstDischargeT = s.T;
                        firstDischargeP = s.P;
                        hasDischarge = true;
                    }
                    lastDischargeT = s.T;
                    lastDischargeP = s.P;
                }
            }
        }

        if (maxP >= minP)
        {
            snapshot.MinPercent = minP;
            snapshot.MaxPercent = maxP;
        }

        // 按小时分桶 (24 桶)，休眠时段沿用上一个已知电量 (Forward Fill)
        var buckets = new List<HourlyBucket>(24);
        DateTime nowLocal = DateTime.Now;
        DateTime thisHour = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, 0, 0);

        // 24 小时窗口起点之前的最后一个样本，作为首个桶的延续基线
        double windowStart = new DateTimeOffset(thisHour.AddHours(-23)).ToUnixTimeSeconds();
        int lastKnownPct = -1;
        foreach (var s in _samples)
        {
            if (s.T >= windowStart)
            {
                break;
            }
            lastKnownPct = s.P;
        }

        int activeHours = 0;

        // 逐个桶扫描：指针随样本单调前进，整体仍是 O(样本数 + 24)
        int idx = 0;
        for (int i = 23; i >= 0; i--)
        {
            DateTime hStart = thisHour.AddHours(-i);
            double sStart = new DateTimeOffset(hStart).ToUnixTimeSeconds();
            double sEnd = new DateTimeOffset(hStart.AddHours(1)).ToUnixTimeSeconds();

            int count = 0;
            int bucketPct = lastKnownPct > 0 ? lastKnownPct : snapshot.Percent;

            while (idx < _samples.Count && _samples[idx].T < sStart)
            {
                idx++;
            }

            int j = idx;
            while (j < _samples.Count && _samples[j].T < sEnd)
            {
                count++;
                bucketPct = _samples[j].P;
                j++;
            }

            bool isSleeping = count == 0;
            if (count > 0)
            {
                activeHours++;
                lastKnownPct = bucketPct;
            }

            buckets.Add(new HourlyBucket(hStart.ToString("HH:00"), bucketPct, count, isSleeping));
        }

        snapshot.HourlyBuckets = buckets;
        snapshot.OnlineHoursCount = activeHours;

        // 续航预测（仅放电段且数据充足时）
        if (snapshot.Percent > 0 && hasDischarge && !snapshot.IsCharging)
        {
            double dtHours = (lastDischargeT - firstDischargeT) / 3600.0;
            int dp = firstDischargeP - lastDischargeP;
            if (dtHours >= 0.5 && dp > 0)
            {
                double rate = dp / dtHours;
                snapshot.RatePerHour = rate;
                double remHours = snapshot.Percent / rate;
                snapshot.Confidence = dtHours > 4 ? "高" : "中";

                snapshot.RemainingTimeText = remHours >= 24
                    ? $"{(int)(remHours / 24)} 天 {(int)(remHours % 24)} 小时"
                    : $"{(int)remHours} 小时";
            }
            else
            {
                snapshot.RemainingTimeText = "放电数据积累中";
                snapshot.Confidence = "积累中";
            }
        }
    }

    private void CheckAlerts(BatterySnapshot snapshot)
    {
        if (!_config.NotifyEnabled || snapshot.IsCharging || snapshot.Percent <= 0)
            return;

        if (snapshot.Percent <= _config.CriticalThreshold)
        {
            if (!_hasAlertedCritical)
            {
                _hasAlertedCritical = true;
                _hasAlertedLow = true;
                AlertTriggered?.Invoke("严重低电量警告", $"{snapshot.DeviceName} 电量仅剩 {snapshot.Percent}%，请立即连接充电器！");
            }
        }
        else if (snapshot.Percent <= _config.LowThreshold)
        {
            if (!_hasAlertedLow)
            {
                _hasAlertedLow = true;
                AlertTriggered?.Invoke("低电量提醒", $"{snapshot.DeviceName} 电量已降至 {snapshot.Percent}%。");
            }
        }
        else
        {
            // 电量正常，重置告警状态
            _hasAlertedLow = false;
            _hasAlertedCritical = false;
        }
    }

    public void UpdateInterval(int seconds)
    {
        _timer.Change(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds));
    }

    public void Dispose()
    {
        _isDisposed = true;
        _timer.Dispose();
    }
}
