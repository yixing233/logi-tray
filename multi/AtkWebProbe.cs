using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MultiTray;

/// <summary>
/// 从 ATK 网页驱动（hub.atk.pro v3.2.27）逆向出的真实协议排查。
///
/// 此前用的是第三方开源项目（Fan4Metal/ATK_tray）的协议，对本机 Z87 只得到
/// 「原样回显」。网页驱动的 JS 里含真正在用的指令，因此改为以它为准：
///
///   帧结构：16 字节，SIZE=16，baseOffset=5
///     raw[0]        commandId
///     raw[1]        commandStatus
///     raw[2..3]     eepromAddress (uint16, 小端)
///     raw[4]        dataValidLen
///     raw[15]       checkSum = 85 - (sum([8, raw[0..14]]) &amp; 255)
///
///   GetBatteryLevel 命令号 = 4（键盘类）
///     电量 batteryLevel   = raw[5]
///     充电状态 batteryCharge = raw[6]
///     电压 batteryVoltage = uint16(raw[5..6])
///
///   另一处 createBattery(dv) 的解读是 power=dv[0]、充电判断 dv[1]==2，
///   那是「跟随设备主动上报」的路径（偏移不同），不能与上面的响应混用。
///
/// 重要：网页驱动要求的接口是 usagePage=0xFF60 / usage=0x61
/// （filter: vendorId 0x373B, usagePage 65376, usage 97），
/// 而本机通过 2.4G 接收器连接时枚举到的厂商接口是 0xFF1C / 0xFFEF。
/// 因此下面会：
///   1. 打印全部 ATK 接口，明确是否存在 0xFF60；
///   2. 在所有 16 字节可用的接口上按真实协议发 GetBatteryLevel 并打印原始响应。
/// 只有 raw[5] 落在 1..100 才认为读到真值。
/// </summary>
internal static class AtkWebProbe
{
    /// <summary>ATK 键盘命令：GetBatteryLevel = 4。</summary>
    private const byte CmdGetBatteryLevel = 4;

    /// <summary>帧长 16 字节。</summary>
    private const int FrameSize = 16;

    /// <summary>网页驱动 filter 里要求的 usagePage / usage。</summary>
    private const ushort RequiredUsagePage = 0xFF60;
    private const ushort RequiredUsage = 0x61;

    public static int Run()
    {
        ConsoleSession.UseUtf8Output();
        Console.WriteLine();
        Console.WriteLine("ATK 网页驱动协议排查（hub.atk.pro v3.2.27）");
        Console.WriteLine(new string('=', 72));

        var all = Hid.Enumerate();
        var atk = all.Where(x => x.VendorId == 0x373B || x.VendorId == 0x048D).ToList();

        Console.WriteLine($"ATK 相关接口 {atk.Count} 个：");
        foreach (var d in atk)
        {
            bool isWebTarget = d.UsagePage == RequiredUsagePage && d.Usage == RequiredUsage;
            Console.WriteLine($"  {(isWebTarget ? "*" : " ")} {d}");
        }

        bool hasWebInterface = atk.Any(
            x => x.UsagePage == RequiredUsagePage && x.Usage == RequiredUsage);
        Console.WriteLine();
        if (hasWebInterface)
        {
            Console.WriteLine($"  找到网页驱动要求的接口（UP=0x{RequiredUsagePage:X4} "
                              + $"U=0x{RequiredUsage:X2}）");
        }
        else
        {
            Console.WriteLine($"  未找到网页驱动要求的接口（UP=0x{RequiredUsagePage:X4} "
                              + $"U=0x{RequiredUsage:X2}）");
            Console.WriteLine("  说明：该接口通常只在 USB 有线直连时出现；");
            Console.WriteLine("        网页驱动必须用有线连接，正是因为 WebHID 只能访问这类接口。");
            Console.WriteLine("        当前以 2.4G 接收器连接，接口布局不同。");
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine("按真实协议尝试 GetBatteryLevel（帧长 16 字节）");
        Console.WriteLine(new string('=', 72));

        int hits = 0;

        foreach (var d in atk)
        {
            // 帧长 16：优先用 16 字节以上的接口。输出报告或特征报告都可能。
            bool canFeature = d.FeatureLength >= FrameSize;
            bool canOutput = d.OutputLength >= FrameSize;
            bool canInput = d.InputLength >= FrameSize;

            if (!canFeature && !canOutput) continue;

            Console.WriteLine();
            Console.WriteLine($"接口 {d}");

            foreach (byte reportId in Candidates())
            {
                var frame = BuildGetBatteryLevel(reportId);

                // 路径 A：特征报告（先写后读）
                if (canFeature)
                {
                    if (Hid.SetFeature(d.Path, frame))
                    {
                        var resp = Hid.GetFeature(d.Path, reportId, d.FeatureLength);
                        Show("FEAT", reportId, frame, resp);
                        if (Accept(resp, out int pct)) hits += Report(pct, reportId, "特征报告");
                    }
                }

                // 路径 B：输出报告 + 中断读
                if (canOutput && canInput)
                {
                    var resp = Hid.WriteRead(d.Path, frame, d.InputLength, 700);
                    if (resp != null)
                    {
                        Show("OUT", reportId, frame, resp);
                        if (Accept(resp, out int pct)) hits += Report(pct, reportId, "输出报告");
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine(hits > 0
            ? $"命中 {hits} 次"
            : "未命中：按网页驱动协议也未读到电量");
        Console.WriteLine(new string('=', 72));
        return hits > 0 ? 0 : 2;
    }

    private static int Report(int pct, byte rid, string via)
    {
        Console.WriteLine($"    >>> 电量 {pct}%（{via}，rid=0x{rid:X2}）");
        return 1;
    }

    /// <summary>
    /// 构造 GetBatteryLevel 请求帧（16 字节）。
    /// commandId=raw[0]=4，其余按网页驱动类的默认值（0），
    /// checkSum = 85 - (sum([8, raw[0..14]]) &amp; 255)。
    /// </summary>
    private static byte[] BuildGetBatteryLevel(byte reportId)
    {
        var frame = new byte[FrameSize];
        if (reportId == 0)
        {
            // 无编号报告：命令直接放在 raw[0]
            frame[0] = CmdGetBatteryLevel;
        }
        else
        {
            // 带编号报告：raw[0] 是 Report ID，命令后移一位
            frame[0] = reportId;
            frame[1] = CmdGetBatteryLevel;
        }
        frame[FrameSize - 1] = CheckSum(frame, reportId);
        return frame;
    }

    /// <summary>网页驱动的校验和：85 - (sum([8, ...前15字节]) &amp; 255)。</summary>
    private static byte CheckSum(byte[] frame, byte reportId)
    {
        int start = reportId == 0 ? 0 : 1;
        int sum = 8;
        for (int i = start; i < 15; i++) sum += frame[i];
        return (byte)(85 - (sum & 255));
    }

    /// <summary>
    /// 判断响应是否为有效电量。
    /// 电量位置取决于是否带 Report ID：命令在 raw[0] 时电量在 raw[5]，
    /// 命令在 raw[1] 时整体后移一位 → raw[6]。
    /// </summary>
    private static bool Accept(byte[]? resp, out int percent)
    {
        percent = -1;
        if (resp == null) return false;

        foreach (int off in new[] { 5, 6 })
        {
            if (off >= resp.Length) continue;
            int v = resp[off];
            if (v > 0 && v <= 100)
            {
                percent = v;
                return true;
            }
        }
        return false;
    }

    private static void Show(string via, byte reportId, byte[] sent, byte[]? resp)
    {
        Console.WriteLine($"  {via} rid=0x{reportId:X2}");
        Console.WriteLine($"    发 {Hex(sent)}");
        Console.WriteLine($"    收 {Hex(resp)}");
    }

    private static IEnumerable<byte> Candidates()
    {
        yield return 0x00;
        foreach (byte b in new byte[] { 0x08, 0x5A, 0xED, 0xCC, 0x01, 0x02, 0x03, 0x04 })
            yield return b;
    }

    private static string Hex(byte[]? b, int max = 18)
    {
        if (b == null) return "(null)";
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(b.Length, max); i++) sb.Append($"{b[i]:X2} ");
        return sb.ToString().Trim() + (b.Length > max ? $" ...({b.Length}B)" : "");
    }
}
