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
            IsOnline = true,
            StatusText = text,
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
/// 本机实测：该键盘对所有已尝试的组合均无有效应答（详见 README），
/// 因此读不到时会如实显示为离线。
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

        // 同一物理设备可能有多个接口暴露同样的数据，按物理键去重，
        // 否则界面上会出现重复条目
        var seen = new HashSet<string>();

        foreach (var d in candidates)
        {
            string physical = Hid.PhysicalKey(d);
            if (!seen.Add(physical)) continue;

            var reading = TryRead(d, name);
            list.Add(reading);
        }

        return list;
    }

    private DeviceReading TryRead(Hid.HidInterface d, string name)
    {
        string key = "atk:" + Hid.PhysicalKey(d);

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
