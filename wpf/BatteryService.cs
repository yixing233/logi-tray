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

    public event Action<BatterySnapshot>? SnapshotUpdated;
    public event Action<string, string>? AlertTriggered;

    public BatterySnapshot CurrentSnapshot { get; private set; } = new();

    public BatteryService(SettingsConfig config)
    {
        _config = config;

        // 定位原生读取程序（同目录或上一级 native\build）
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string p1 = Path.Combine(baseDir, "mouse-tray.exe");
        string p2 = Path.Combine(baseDir, "..", "..", "..", "..", "native", "build", "mouse-tray.exe");
        string p3 = Path.GetFullPath(p2);

        _nativeExePath = File.Exists(p1) ? p1 : (File.Exists(p3) ? p3 : "mouse-tray.exe");

        _timer = new System.Threading.Timer(OnTimerTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(_config.Interval));
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

        string deviceName = "PRO X Wireless";
        int percent = -1;
        bool isCharging = false;
        string statusText = "放电中";
        string levelText = "良好";

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
                    p.WaitForExit(3000);

                    // 严谨正则解析：匹配 "PRO X Wireless: 89% · 放电中 · 满"
                    foreach (string rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string line = rawLine.Trim();
                        var pctMatch = Regex.Match(line, @"(\d{1,3})%");
                        if (pctMatch.Success)
                        {
                            int colon = line.IndexOf(':');
                            if (colon > 0)
                            {
                                deviceName = line[..colon].Trim();
                            }

                            if (int.TryParse(pctMatch.Groups[1].Value, out int pVal) && pVal > 0 && pVal <= 100)
                            {
                                percent = pVal;
                            }

                            if (line.Contains("充电"))
                            {
                                statusText = "充电中";
                                isCharging = true;
                            }
                            else if (line.Contains("满"))
                            {
                                statusText = "放电中";
                                levelText = "满";
                            }
                            else if (line.Contains("良好"))
                            {
                                levelText = "良好";
                            }
                            break;
                        }
                    }
                }
            }
        }
        catch { }

        // 2. 解析 history.json，提取 24 小时时段分析与续航预测
        var snapshot = BuildSnapshotFromHistory(deviceName, percent, isCharging, statusText, levelText);
        CurrentSnapshot = snapshot;

        // 3. 低电量提醒判断
        CheckAlerts(snapshot);

        SnapshotUpdated?.Invoke(snapshot);
    }

    private BatterySnapshot BuildSnapshotFromHistory(
        string deviceName, int percent, bool isCharging, string statusText, string levelText)
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
            string histDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "logi-tray");
            string histPath = Path.Combine(histDir, "history.json");

            if (!File.Exists(histPath))
            {
                string oldPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MouseBatteryTray",
                    "history.json");
                if (File.Exists(oldPath))
                {
                    try
                    {
                        Directory.CreateDirectory(histDir);
                        File.Copy(oldPath, histPath, true);
                    }
                    catch { histPath = oldPath; }
                }
            }

            if (File.Exists(histPath))
            {
                string json = File.ReadAllText(histPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("samples", out var samplesArr))
                {
                    var samples = new List<(double t, int p, bool c)>();
                    foreach (var elem in samplesArr.EnumerateArray())
                    {
                        if (elem.TryGetProperty("t", out var tProp) &&
                            elem.TryGetProperty("percent", out var pProp))
                        {
                            bool c = elem.TryGetProperty("charging", out var cProp) && cProp.GetBoolean();
                            int p = pProp.GetInt32();
                            if (p > 0 && p <= 100)
                            {
                                samples.Add((tProp.GetDouble(), p, c));
                            }
                        }
                    }

                    if (percent > 0)
                    {
                        // 若读到了新样本，且距离最后一个样本超过 20 秒，则持久化到 history.json
                        double nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        if (samples.Count == 0 || (nowEpoch - samples.Last().t) >= 20)
                        {
                            samples.Add((nowEpoch, percent, isCharging));
                            AppendSampleToFile(histPath, nowEpoch, percent, isCharging);
                        }
                    }
                    else if (samples.Count > 0)
                    {
                        // 若本次休眠/未读到，平滑回退到最近的有效历史记录，绝对不显示 0% 红色假报警
                        var latest = samples.Last();
                        snapshot.Percent = latest.p;
                        snapshot.IsCharging = latest.c;
                        double elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - latest.t;
                        snapshot.IsConnected = elapsed < _config.StaleGrace;
                        if (!snapshot.IsConnected)
                        {
                            snapshot.LevelText = "已休眠";
                        }
                    }

                    if (samples.Count > 0)
                    {
                        // 计算 24 小时聚合
                        double nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        double dayAgo = nowEpoch - 24 * 3600;
                        var daySamples = samples.Where(s => s.t >= dayAgo).ToList();

                        if (daySamples.Count > 0)
                        {
                            snapshot.MinPercent = daySamples.Min(s => s.p);
                            snapshot.MaxPercent = daySamples.Max(s => s.p);
                        }

                        // 按小时分桶 (24 桶)，并执行休眠期间前向沿用算法 (Forward Fill)，绝不直接归零
                        var buckets = new List<HourlyBucket>();
                        DateTime nowLocal = DateTime.Now;
                        DateTime thisHour = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, 0, 0);

                        int activeHours = 0;
                        int lastKnownPct = -1;

                        // 尝试从 24 小时之前的最近一次历史中获取初始延续电量
                        double dayAgoStart = new DateTimeOffset(thisHour.AddHours(-23)).ToUnixTimeSeconds();
                        var priorSamples = samples.Where(s => s.t < dayAgoStart).ToList();
                        if (priorSamples.Count > 0 && priorSamples.Last().p > 0)
                        {
                            lastKnownPct = priorSamples.Last().p;
                        }

                        for (int i = 23; i >= 0; i--)
                        {
                            DateTime hStart = thisHour.AddHours(-i);
                            DateTime hEnd = hStart.AddHours(1);
                            double sStart = new DateTimeOffset(hStart).ToUnixTimeSeconds();
                            double sEnd = new DateTimeOffset(hEnd).ToUnixTimeSeconds();

                            var inHour = samples.Where(s => s.t >= sStart && s.t < sEnd).ToList();
                            int count = inHour.Count;
                            int bucketPct;
                            bool isSleeping = false;

                            if (count > 0)
                            {
                                activeHours++;
                                bucketPct = inHour.Last().p;
                                lastKnownPct = bucketPct;
                            }
                            else
                            {
                                // 休眠期间沿用休眠前的电量，绝不直接归零！
                                isSleeping = true;
                                bucketPct = lastKnownPct > 0 ? lastKnownPct : snapshot.Percent;
                            }

                            buckets.Add(new HourlyBucket(hStart.ToString("HH:00"), bucketPct, count, isSleeping));
                        }

                        snapshot.HourlyBuckets = buckets;
                        snapshot.OnlineHoursCount = activeHours;

                        // 续航预测 (仅在放电段且电量 > 0 时有效计算)
                        if (snapshot.Percent > 0 && daySamples.Count >= 2 && !snapshot.IsCharging)
                        {
                            var discharge = daySamples.Where(s => !s.c).OrderBy(s => s.t).ToList();
                            if (discharge.Count >= 2)
                            {
                                double dtHours = (discharge.Last().t - discharge.First().t) / 3600.0;
                                int dp = discharge.First().p - discharge.Last().p;
                                if (dtHours >= 0.5 && dp > 0)
                                {
                                    double rate = dp / dtHours;
                                    snapshot.RatePerHour = rate;
                                    double remHours = snapshot.Percent / rate;
                                    snapshot.Confidence = dtHours > 4 ? "高" : "中";

                                    if (remHours >= 24)
                                    {
                                        int d = (int)(remHours / 24);
                                        int h = (int)(remHours % 24);
                                        snapshot.RemainingTimeText = $"{d} 天 {h} 小时";
                                    }
                                    else
                                    {
                                        snapshot.RemainingTimeText = $"{(int)remHours} 小时";
                                    }
                                }
                                else
                                {
                                    snapshot.RemainingTimeText = "放电数据积累中";
                                    snapshot.Confidence = "积累中";
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        if (string.IsNullOrEmpty(snapshot.RemainingTimeText) || snapshot.RemainingTimeText == "正在估算...")
        {
            snapshot.RemainingTimeText = snapshot.IsCharging ? "充电中" : (snapshot.Percent > 0 ? "放电数据积累中" : "设备离线");
        }

        return snapshot;
    }

    private static void AppendSampleToFile(string path, double t, int percent, bool charging)
    {
        try
        {
            string content = File.ReadAllText(path).Trim();
            int closeIdx = content.LastIndexOf(']');
            if (closeIdx > 0)
            {
                string entry = $"   {{\"t\": {t:F3}, \"percent\": {percent}, \"charging\": {(charging ? "true" : "false")}}}\n";
                // 检查前面是否有元素，需要补逗号
                int lastBrace = content.LastIndexOf('}', closeIdx);
                if (lastBrace > 0)
                {
                    string before = content[..(lastBrace + 1)];
                    string after = content[closeIdx..];
                    File.WriteAllText(path, before + ",\n" + entry + " " + after);
                }
            }
        }
        catch { }
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
