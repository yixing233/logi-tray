using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MultiTray;

/// <summary>
/// 一个品牌/一类设备的电量读取器。
///
/// 设计成可插拔：每个 Provider 自己决定「认哪些接口、怎么读、读不到算什么」。
/// 新增品牌只需实现本接口并注册到 <see cref="DeviceReader.All"/>，
/// 不必改动托盘与界面代码。
/// </summary>
public interface IBatteryProvider
{
    /// <summary>来源标识，与设置里的 enabled_sources 对应。</summary>
    string Id { get; }

    /// <summary>在界面上显示的名称。</summary>
    string DisplayName { get; }

    /// <summary>读取该 Provider 负责的全部设备。实现不应抛异常。</summary>
    List<DeviceReading> Read(IReadOnlyList<Hid.HidInterface> interfaces);
}

/// <summary>汇总所有 Provider 的读取入口。</summary>
public static class DeviceReader
{
    /// <summary>已注册的 Provider，按此顺序执行。</summary>
    public static readonly List<IBatteryProvider> All = new()
    {
        new LogitechProvider(),
        new MchoseProvider(),
        new AtkProvider(),
    };

    /// <summary>
    /// 读取所有启用的来源。
    /// Provider 内部异常会被吞掉并记录为离线设备，绝不因为一个来源出错
    /// 而让整个界面空掉。
    /// </summary>
    public static List<DeviceReading> ReadAll(MultiSettings settings)
    {
        var results = new List<DeviceReading>();
        IReadOnlyList<Hid.HidInterface> interfaces;

        try
        {
            interfaces = Hid.Enumerate();
        }
        catch
        {
            interfaces = Array.Empty<Hid.HidInterface>();
        }

        foreach (var provider in All)
        {
            if (!settings.EnabledSources.Contains(provider.Id)) continue;
            try
            {
                var r = provider.Read(interfaces);
                if (r != null) results.AddRange(r);
            }
            catch
            {
                // 单个来源失败不影响其它来源
            }
        }

        // 稳定排序：先按类别，再按名称，避免界面每次刷新顺序跳动
        return results
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.Name, StringComparer.CurrentCulture)
            .ToList();
    }
}

// ─────────────────────────────────────────────────────────────────
//  罗技：复用既有的 native HID++ 读取器
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// 罗技设备电量。
///
/// 读法复用项目已有的 native 读取器（mouse-tray.exe --once）：它的 HID++
/// 协议实现经过 53 项合成帧单测、并覆盖 5 条电量特性回退路径，
/// 重新用托管代码写一遍没有收益，还可能引入新的协议错误。
/// 这里只负责调用它并把文本输出解析成结构化结果。
///
/// 文本格式（native/src/main.cpp 的 RunOnce）：
///     &lt;设备名&gt;: &lt;电量&gt;% · &lt;状态&gt; [· &lt;档位&gt;][（推算）]
/// </summary>
public sealed class LogitechProvider : IBatteryProvider
{
    public string Id => "logitech";
    public string DisplayName => "罗技";

    /// <summary>原生读取器可执行文件名。</summary>
    private const string ReaderExe = "mouse-tray.exe";

    private static readonly Regex LinePattern = new(
        @"^(?<name>.+?):\s*(?<pct>\d{1,3})%\s*·\s*(?<status>[^·]+?)(?:\s*·\s*(?<extra>.+))?$",
        RegexOptions.Compiled);

    public List<DeviceReading> Read(IReadOnlyList<Hid.HidInterface> interfaces)
    {
        var list = new List<DeviceReading>();

        // 只要看到罗技厂商 ID 就尝试读一次。
        //
        // 早先的写法额外要求「存在 UsagePage 0xFF00 的接口」，这是错的：
        // 本机 LIGHTSPEED 接收器（046D:C547）暴露的接口用途页是
        // 0x0001 / 0xFF00 等混合形态，用用途页当门槛会让整块罗技设备
        // 直接从列表里消失，而不是显示为离线。
        bool hasLogitech = interfaces.Any(x => x.VendorId == 0x046D);
        if (!hasLogitech) return list;

        string? output = RunReader();
        if (output == null)
        {
            // 读取器缺失或执行失败时，仍要如实报告「有一台罗技设备但读不到」，
            // 而不是静默不显示
            var off = DeviceReading.Offline("罗技设备", DeviceKind.Mouse, "HID++");
            off.Key = "logitech:unknown";
            list.Add(off);
            return list;
        }

        int parsedCount = 0;
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            // 读不到电量时读取器会输出提示语，跳过
            if (line.Contains("未读到电量")) continue;

            var m = LinePattern.Match(line);
            if (!m.Success) continue;

            int pct;
            if (!int.TryParse(m.Groups["pct"].Value, out pct)) continue;
            if (pct < 0 || pct > 100) continue;

            bool inferred = line.Contains("推算");
            string status = m.Groups["status"].Value.Trim();
            bool charging = status.Contains("充电") || status.Contains("已充满");

            list.Add(new DeviceReading
            {
                Name = m.Groups["name"].Value.Trim(),
                Percent = pct,
                Kind = GuessKind(m.Groups["name"].Value),
                IsCharging = charging,
                IsOnline = true,
                StatusText = status,
                Source = inferred ? "HID++（推算）" : "HID++",
                Key = "logitech:" + m.Groups["name"].Value.Trim(),
            });
            parsedCount++;
        }

        // 有罗技设备但一台都没读到 → 显示为离线，而不是从列表里消失。
        // 用户能据此判断「接收器在、鼠标休眠」，而不是以为程序没支持罗技。
        if (parsedCount == 0)
        {
            var off = DeviceReading.Offline("罗技设备", DeviceKind.Mouse, "HID++");
            off.Key = "logitech:unknown";
            list.Add(off);
        }

        return list;
    }

    private static DeviceKind GuessKind(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("键") || n.Contains("keyboard") || n.Contains("kbd"))
            return DeviceKind.Keyboard;
        if (n.Contains("耳机") || n.Contains("headset") || n.Contains("耳机"))
            return DeviceKind.Headset;
        // 罗技读取器目前只读鼠标
        return DeviceKind.Mouse;
    }

    /// <summary>
    /// 调用原生读取器。找不到可执行文件、超时或非预期退出码都返回 null，
    /// 由调用方按「没有罗技设备」处理。
    /// </summary>
    private static string? RunReader()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string exe = Path.Combine(baseDir, ReaderExe);
            if (!File.Exists(exe)) return null;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--once",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };

            using var p = Process.Start(psi);
            if (p == null) return null;

            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return stdout;
        }
        catch
        {
            return null;
        }
    }
}

// ─────────────────────────────────────────────────────────────────
//  迈从 MCHOSE
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// 迈从（MCHOSE）无线耳机等设备。
///
/// 协议来自同型号的公开实现 rafagfran/mchose-v9-pro-battery-tray：
///     请求 64 字节 [0]=0x55 [1]=0x65 [2]=0x01
///     响应 [0]=0x55 [1]=0x65 [2]=电量% [3]=状态码
/// 设备标识：VID 0x291D / PID 0x385D，输入与输出报告均为 64 字节。
///
/// 注意：本机实测该设备对上述帧**没有任何应答**（详见 README 的实测记录）。
/// 因此这里除了发送标准帧，还会尝试把请求写在带 Report ID 的形态下，
/// 并把结果如实上报；读不到就显示为离线，绝不显示一个猜测的数字。
/// </summary>
public sealed class MchoseProvider : IBatteryProvider
{
    public string Id => "mchose";
    public string DisplayName => "迈从";

    private const ushort Vid = 0x291D;
    private const ushort Pid = 0x385D;

    public List<DeviceReading> Read(IReadOnlyList<Hid.HidInterface> interfaces)
    {
        var list = new List<DeviceReading>();

        var targets = interfaces
            .Where(x => x.VendorId == Vid && x.ProductId == Pid)
            .ToList();
        if (targets.Count == 0) return list;

        // 需要 64 字节输入输出才能承载该协议帧
        var usable = targets.Where(x => x.InputLength >= 64 && x.OutputLength >= 64)
                            .ToList();
        string displayName = targets
            .Select(t => t.ProductName)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "迈从设备";

        if (usable.Count == 0)
        {
            var off = DeviceReading.Offline(displayName, DeviceKind.Headset, "迈从");
            off.Key = "mchose:" + displayName;
            list.Add(off);
            return list;
        }

        // 同一物理设备的多个接口只读一次，避免重复条目
        var seen = new HashSet<string>();
        foreach (var d in usable)
        {
            string key = Hid.PhysicalKey(d);
            if (!seen.Add(key)) continue;
            list.Add(TryRead(d, displayName));
        }

        return list;
    }

    private DeviceReading TryRead(Hid.HidInterface d, string displayName)
    {
        string key = "mchose:" + d.Path;

        // 形态 1：照搬公开实现 —— 无 Report ID，整帧以 0x55 开头
        var plain = Protocols.BuildMchoseRequest(d.OutputLength);
        var resp = Hid.WriteRead(d.Path, plain, d.InputLength, 900);
        if (Protocols.TryParseMchose(resp, out int pct, out byte st))
            return MakeReading(displayName, key, pct, st, "迈从 55 65");

        // 形态 2：部分固件把首字节当 Report ID，数据从偏移 1 开始
        var withId = new byte[d.OutputLength];
        withId[0] = 0x55;
        withId[1] = 0x65;
        withId[2] = 0x01;
        var resp2 = Hid.WriteRead(d.Path, withId, d.InputLength, 900);
        if (Protocols.TryParseMchose(resp2, out int pct2, out byte st2))
            return MakeReading(displayName, key, pct2, st2, "迈从 55 65(带 ID)");

        var off = DeviceReading.Offline(displayName, DeviceKind.Headset, "迈从");
        off.Key = key;
        return off;
    }

    private static DeviceReading MakeReading(string name, string key, int pct,
                                             byte status, string source)
    {
        var (text, charging) = Protocols.MchoseStatusText(status);
        return new DeviceReading
        {
            Name = name,
            Percent = pct,
            Kind = DeviceKind.Headset,
            IsCharging = charging,
            // 状态字节未校准：既不声称充电，也不声称放电。
            ChargeStateKnown = !string.IsNullOrEmpty(text),
            IsOnline = true,
            // 状态字节尚未校准，MchoseStatusText 返回空串。
            // 此时退回电量档位文案（如「一般」），至少是有依据的信息，
            // 而不是一个确定错了的「充电中」。
            StatusText = string.IsNullOrEmpty(text) ? Protocols.LevelText(pct) : text,
            Source = source,
            Key = key,
        };
    }
}

// ─────────────────────────────────────────────────────────────────
//  ATK
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// ATK 键盘/鼠标。
///
/// 协议来自公开实现 Fan4Metal/ATK_tray 的 models.py：
///   协议 1：17 字节特征报告，[1]=0x04 [16]=0x49，电量在响应 [6]
///   协议 2：64 字节，[2]=0x72；无线 [1]=0x7D [5]=0x01，有线 [1]=0x7C [5]=0x00；
///           响应需 [1]=0x72 且 [5]=0x07，无线电量在 [7]
///
/// 关键区别（与照抄实现的差异）：公开实现把 Report ID 硬编码为 0x08，
/// 而本机实测该键盘的 17 字节特征接口实际可读的 Report ID 是 **0x5A**，
/// 65 字节特征接口是 **0xED**，另一个接口是 **0xCC** —— 各型号并不相同。
/// 因此这里不硬编码，而是先从接口能力推断，再依次尝试若干候选 ID，
/// 哪种形态真的解析出合理电量就采用哪种，并在 Source 里标注。
///
/// 此外还有一套 **Z87 系列键盘**专用协议（WebHID 抓包实证，本机实测可用）：
///   64 字节中断写，[0]=报告号 4、[1]=序号、[3]=命令码；
///   电量命令 0x1A 必须跟在固定 17 帧前导之后才被接受。
/// 详见 <see cref="Protocols.BuildAtkKbdSequence"/>。
/// 这套协议优先尝试：它是唯一在本机拿真机验证过的路径。
/// </summary>
public sealed class AtkProvider : IBatteryProvider
{
    public string Id => "atk";
    public string DisplayName => "ATK";

    /// <summary>公开实现列出的 ATK/VXE/VGN 厂商 ID。</summary>
    private static readonly ushort[] KnownVids = { 0x373B, 0x3554 };

    /// <summary>候选 Report ID。公开实现用 0x08，实测本机接口用的是别的值。</summary>
    private static readonly byte[] CandidateReportIds =
    {
        0x08, 0x5A, 0xED, 0xCC, 0x00, 0x01, 0x02, 0x03, 0x04,
    };

    public List<DeviceReading> Read(IReadOnlyList<Hid.HidInterface> interfaces)
    {
        var list = new List<DeviceReading>();

        var candidates = interfaces
            .Where(x => KnownVids.Contains(x.VendorId))
            .Where(x => x.FeatureLength >= Protocols.AtkProtocol1Length ||
                        (x.OutputLength >= Protocols.AtkProtocol2Length &&
                         x.InputLength >= Protocols.AtkProtocol2Length))
            .ToList();

        if (candidates.Count == 0) return list;

        string name = candidates
            .Select(c => c.ProductName)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "ATK 设备";

        // 同一把键盘会暴露十几个 HID 接口（键盘、多媒体键、厂商自定义…），
        // 只有一个能读电量。**必须先逐个接口试读、再按物理键去重** ——
        // 早先反过来（先去重、再试读）时，去重留下的恰好是第一个
        // `UP=0x0001 IN=9 OUT=2` 的键盘接口，于是永远试不到真正的
        // 承载接口 `UP=0xFF1C IN=64 OUT=64`，键盘恒显示 `--`。
        //
        // 排序只是加速：输出/输入报告越大越可能是厂商自定义接口。
        var groups = candidates
            .GroupBy(Hid.PhysicalKey)
            .ToList();

        foreach (var g in groups)
        {
            var ordered = g
                .OrderByDescending(x => x.OutputLength + x.InputLength)
                .ToList();

            DeviceReading? first = null;
            foreach (var d in ordered)
            {
                var reading = TryRead(d, name);
                first ??= reading;
                if (reading.IsOnline) { first = reading; break; }
            }

            if (first != null) list.Add(first);
        }

        return list;
    }

    private DeviceReading TryRead(Hid.HidInterface d, string name)
    {
        string key = "atk:" + Hid.PhysicalKey(d);

        // ── 优先试 Z87 系列键盘协议（本机唯一验证成功的路径）──
        if (d.OutputLength >= Protocols.AtkKbdFrameLength &&
            d.InputLength >= Protocols.AtkKbdFrameLength)
        {
            var kbd = TryReadZ87Keyboard(d, name, key);
            if (kbd != null) return kbd;
        }

        // 优先走 17 字节特征报告（协议 1）：最轻量，且公开实现首选它
        if (d.FeatureLength >= Protocols.AtkProtocol1Length)
        {
            foreach (byte rid in CandidateReportIds)
            {
                var set = Protocols.BuildAtk1Request(rid);
                // 按接口实际长度补齐（可能是 17，也可能更大）
                if (d.FeatureLength != set.Length)
                {
                    var sized = new byte[d.FeatureLength];
                    Array.Copy(set, sized, Math.Min(set.Length, sized.Length));
                    sized[0] = rid;
                    if (sized.Length > 1) sized[1] = 0x04;
                    if (sized.Length > 16) sized[16] = 0x49;
                    set = sized;
                }

                if (!Hid.SetFeature(d.Path, set)) continue;

                var resp = Hid.GetFeature(d.Path, rid, d.FeatureLength);
                if (resp == null) continue;
                if (LooksLikeEcho(set, resp)) continue;

                if (Protocols.TryParseAtk1(resp, out int pct))
                {
                    return new DeviceReading
                    {
                        Name = name,
                        Percent = pct,
                        Kind = GuessKind(name),
                        IsCharging = false,
                        IsOnline = true,
                        StatusText = Protocols.LevelText(pct),
                        Source = $"ATK 协议1(rid=0x{rid:X2})",
                        Key = key,
                    };
                }
            }
        }

        // 退回协议 2：64 字节输出报告，无线/有线各试一次
        if (d.OutputLength >= Protocols.AtkProtocol2Length &&
            d.InputLength >= Protocols.AtkProtocol2Length)
        {
            foreach (bool wired in new[] { false, true })
            {
                foreach (byte rid in CandidateReportIds)
                {
                    var frame = Protocols.BuildAtk2Request(wired, rid);
                    var resp = Hid.WriteRead(d.Path, frame, d.InputLength, 700);
                    if (resp == null) continue;

                    // 回显防护（实测本机 ATK Z87 会把请求原样回送）：
                    // 若响应与请求逐字节相同，那是设备回显而非应答。
                    // 不排除的话，请求里的电量字段会被当成读数 ——
                    // 例如回显的 [7]=0x01 会被误报成「1%」。
                    if (LooksLikeEcho(frame, resp)) continue;

                    if (Protocols.TryParseAtk2(resp, out int pct, out bool reliable)
                        && reliable)
                    {
                        return new DeviceReading
                        {
                            Name = name,
                            Percent = pct,
                            Kind = GuessKind(name),
                            IsCharging = false,
                            IsOnline = true,
                            StatusText = Protocols.LevelText(pct),
                            Source = $"ATK 协议2({(wired ? "有线" : "无线")},rid=0x{rid:X2})",
                            Key = key,
                        };
                    }
                }
            }
        }

        var off = DeviceReading.Offline(name, GuessKind(name), "ATK");
        off.Key = key;
        return off;
    }

    /// <summary>
    /// Z87 系列键盘：64 字节中断写 + 固定 17 帧前导，电量为前导之后那一帧
    /// 0x1A 的应答（实测 100% / 充电中）。
    ///
    /// 为什么必须连发整轮：单发那一帧 0x1A 时设备会把命令字节回成 0xFF
    /// （驱动源码里 0xFF 是明确的错误返回），只有走完前导才被受理。
    ///
    /// **重试是必需的**：真机连续 4 次读取里会有 1 次整轮无应答 ——
    /// 键盘若刚被唤醒或正忙于背光/按键上报，会整轮丢弃。单次失败就报离线
    /// 会让界面频繁闪 `--`，因此最多试 <see cref="KbdAttempts"/> 次，
    /// 每次收尾等待递增（越往后越可能等到迟到的应答）。
    /// </summary>
    private static DeviceReading? TryReadZ87Keyboard(Hid.HidInterface d, string name, string key)
    {
        const int KbdAttempts = 2;

        for (int attempt = 0; attempt < KbdAttempts; attempt++)
        {
            if (attempt > 0) Thread.Sleep(120);

            // 每轮重新构造：序号必须从 1 重新开始，
            // 设备用序号与请求配对，跨轮沿用会让它对不上。
            var frames = Protocols.BuildAtkKbdSequence();

            // 优先用「逐帧写 + 排空读」这条节拍。
            //
            // 真机实测：`--diag-atk` 同一条命令走 ProbeDrain 时 4/4 全部读到，
            // 走 ProbeBurst（后台线程连发）则 8 次里只成功 4~5 次。
            // 差异不在写，而在读：连发路径的后台读线程反复
            // ReadFile/CancelIo，与写入抢同一个句柄，应答会偶发丢失。
            // 排空读把读写严格串起来，因此稳定。
            var slow = Hid.ProbeDrain(d.Path, frames, d.InputLength,
                                      settleMs: 15, drainMs: 90);
            if (TryParseAny(slow, name, key, out var fromSlow)) return fromSlow;

            // 慢节拍没读到再试连发（与浏览器同速），只作为兜底
            var fast = Hid.ProbeBurst(d.Path, frames, d.InputLength,
                                      gapMs: 12, afterMs: 400 + attempt * 250);
            if (TryParseAny(fast, name, key, out var fromFast)) return fromFast;
        }

        return null;
    }

    /// <summary>
    /// 从一次交换的报告里挑出电量应答。
    ///
    /// 应答可能以任意顺序到达，且前导帧的应答也满足长度要求，
    /// 因此逐条用解析器筛：只有命令码对得上 0x1A 的才是电量帧。
    /// </summary>
    private static bool TryParseAny(Hid.ExchangeResult ex, string name, string key,
                                    out DeviceReading? reading)
    {
        reading = null;
        if (!ex.WriteOk) return false;

        foreach (var rep in ex.Reports)
        {
            if (!Protocols.TryParseAtkKbdPower(rep, out int pct, out bool charging)) continue;

            reading = new DeviceReading
            {
                Name = name,
                Percent = pct,
                Kind = GuessKind(name),
                IsCharging = charging,
                IsOnline = true,
                StatusText = charging ? "充电中" : Protocols.LevelText(pct),
                Source = "ATK Z87 键盘",
                Key = key,
            };
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断响应是否只是把请求回显了回来。
    ///
    /// 实测本机 ATK Z87 键盘（VID 373B:PID 1012）对协议 2 的帧会原样回送：
    /// 发 04 7D 72 02 00 01 07 01，收到 04 7D 72 02 00 01 07 01。
    /// 这种回显不含任何设备信息，必须排除，否则会把请求里的字节当成电量。
    /// 比较时忽略首字节（Report ID 可能被设备改写）。
    /// </summary>
    private static bool LooksLikeEcho(byte[] sent, byte[] received)
    {
        if (sent == null || received == null) return false;
        int n = Math.Min(sent.Length, received.Length);
        if (n <= 2) return false;

        // 从第 1 字节起比较（跳过 Report ID）
        for (int i = 1; i < n; i++)
        {
            if (sent[i] != received[i]) return false;
        }
        return true;
    }

    private static DeviceKind GuessKind(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("键") || n.Contains("keyboard") || n.Contains("kbd"))
            return DeviceKind.Keyboard;
        // ATK 的接收器名为「ATK Z87 Dongle」这类，型号里通常带布局数字（Z87/A75 等）。
        // 判不出来时归为键盘：ATK/VXE/VGN 的无线产品以键盘为主，
        // 且本项目对鼠标与键盘的显示差异仅是分类文案。
        return DeviceKind.Keyboard;
    }
}
