using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MultiTray;

/// <summary>
/// 按 ATK 网页驱动（hub.atk.pro v3.2.27）的**真实协议**读取 Z87 键盘电量。
///
/// 协议来自驱动 JS，而非第三方项目：
///   设备表条目（与本机完全吻合）：
///     {vendorId:14139(0x373B), productId:4114(0x1012),
///      usage:146(0x92), usagePage:65308(0xFF1C),
///      custom:{connectType:0, productName:"ATK Z87 Pro 2.4G Dongle"}}
///
///   帧结构（类 Mo，SIZE=16，baseOffset=5）：
///     raw[0]       commandId
///     raw[1]       commandStatus
///     raw[2..3]    eepromAddress
///     raw[4]       dataValidLen
///     raw[15]      checkSum = 85 - (sum([8, raw[0..14]]) & 255)
///
///   命令 GetBatteryLevel = 4
///     电量   = raw[5]
///     充电态 = raw[6]（2 表示充电中）
///     电压   = uint16(raw[5..6])
///
/// 关键点：驱动的 transferForResult 传的 report id 是 **0**，
/// 且 promiseRun 的默认传输方式是 sendFeatureReport（feature 默认 true）。
/// 本机 0xFF1C 接口是 IN=64 OUT=64 FEAT=0，因此这里把两条路径都试：
///   A. 输出报告 + 等输入报告（含把 16 字节帧补零到 64 字节）
///   B. 特性报告（若接口有）
/// 另外会**被动监听**几秒，因为 2.4G 接收器可能主动上报设备状态
/// （驱动里另有 createBattery 直接读 dv[0]/dv[1] 的路径，符合被动上报的形态）。
/// 只有 raw[5]（或补零前 16 字节内的对应位）落在 1..100 才认定读到真值。
/// </summary>
internal static class AtkZ87
{
    private const byte CmdGetBatteryLevel = 4;
    private const int FrameSize = 16;
    private const ushort Vid = 0x373B;
    private const ushort Pid = 0x1012;
    private const ushort TargetPage = 0xFF1C;
    private const ushort TargetUsage = 0x92;

    public static int Run()
    {
        ConsoleSession.UseUtf8Output();
        Console.WriteLine();
        Console.WriteLine("ATK Z87 电量读取（网页驱动真实协议）");
        Console.WriteLine(new string('=', 74));

        var all = Hid.Enumerate();
        var z87 = all.Where(x => x.VendorId == Vid && x.ProductId == Pid).ToList();

        Console.WriteLine($"Z87 接口 {z87.Count} 个：");
        foreach (var d in z87) Console.WriteLine($"  {d}");

        // 驱动指定的接口
        var target = z87.FirstOrDefault(x => x.UsagePage == TargetPage
                                             && x.Usage == TargetUsage);
        Console.WriteLine();
        if (target == null)
        {
            Console.WriteLine($"未找到驱动指定接口 UP=0x{TargetPage:X4} U=0x{TargetUsage:X2}");
            return 2;
        }
        Console.WriteLine($"驱动指定接口已找到：{target}");

        int hits = 0;

        // ---------- A. 被动监听：接收器可能主动上报 ----------
        Console.WriteLine();
        Console.WriteLine(new string('-', 74));
        Console.WriteLine("A. 被动监听 3 秒（不发任何请求，看是否有主动上报）");
        Console.WriteLine(new string('-', 74));
        foreach (var d in z87.Where(x => x.InputLength >= 16))
        {
            var unsolicited = Hid.Listen(d.Path, d.InputLength, 1500);
            Console.WriteLine($"  {d.UsagePage:X4}/{d.Usage:X2} IN={d.InputLength} -> "
                              + Hex(unsolicited));
            if (TryRead(unsolicited, out int p, out byte ch))
            {
                Console.WriteLine($"    >>> 主动上报电量 {p}%（充电态 {ch}）");
                hits++;
            }
        }

        // ---------- B. 主动请求 ----------
        Console.WriteLine();
        Console.WriteLine(new string('-', 74));
        Console.WriteLine("B. 发送 GetBatteryLevel 请求");
        Console.WriteLine(new string('-', 74));

        // 期望响应里 raw[0] 可能回显 commandId=4，或带 status
        foreach (byte reportId in new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05,
                                               0x06, 0x07, 0x08 })
        {
            // 帧按接口输出长度补齐（驱动传的是 16 字节，HID 报告是 64 字节）
            var frame = Build(reportId, target.OutputLength);

            foreach (var d in z87.Where(x => x.OutputLength >= FrameSize))
            {
                if (d.InputLength < FrameSize) continue;

                var resp = Hid.WriteRead(d.Path, frame, d.InputLength, 800);
                Console.WriteLine($"  rid=0x{reportId:X2} 经 {d.UsagePage:X4}/{d.Usage:X2}");
                Console.WriteLine($"    发 {Hex(frame, 20)}");
                Console.WriteLine($"    收 {Hex(resp)}");

                if (resp == null) continue;
                if (TryRead(resp, out int p, out byte ch))
                {
                    Console.WriteLine($"    >>> 电量 {p}%（充电态 {ch}）");
                    hits++;
                }
                // 回显判定：与请求完全相同即为回显，不算应答
                if (IsEcho(frame, resp))
                {
                    Console.WriteLine("    （与请求一致 → 判定为回显，忽略）");
                }
            }
        }

        // ---------- C. 特性报告（若该接口有） ----------
        foreach (var d in z87.Where(x => x.FeatureLength >= FrameSize))
        {
            Console.WriteLine();
            Console.WriteLine(new string('-', 74));
            Console.WriteLine($"C. 特性报告接口 {d.UsagePage:X4}/{d.Usage:X2} "
                              + $"FEAT={d.FeatureLength}");
            Console.WriteLine(new string('-', 74));

            foreach (byte reportId in new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x08 })
            {
                var frame = Build(reportId, d.FeatureLength);
                if (!Hid.SetFeature(d.Path, frame)) continue;
                var resp = Hid.GetFeature(d.Path, reportId, d.FeatureLength);
                Console.WriteLine($"  rid=0x{reportId:X2} 发 {Hex(frame, 20)}");
                Console.WriteLine($"    收 {Hex(resp)}");
                if (TryRead(resp, out int p, out byte ch))
                {
                    Console.WriteLine($"    >>> 电量 {p}%（充电态 {ch}）");
                    hits++;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 74));
        Console.WriteLine(hits > 0 ? $"命中 {hits} 次" : "未命中：仍读不到 Z87 电量");
        Console.WriteLine(new string('=', 74));
        return hits > 0 ? 0 : 2;
    }

    /// <summary>构造 GetBatteryLevel 帧，并补零到接口的报告长度。</summary>
    private static byte[] Build(byte reportId, int totalLength)
    {
        int len = Math.Max(FrameSize, totalLength);
        var frame = new byte[len];

        int cmdOffset = reportId == 0 ? 0 : 1;
        if (reportId != 0) frame[0] = reportId;
        frame[cmdOffset] = CmdGetBatteryLevel;

        // 校验和按驱动的算法，作用于「去掉 Report ID 后的 16 字节帧」
        byte sum = CheckSum(frame, cmdOffset);
        // 校验和位于 16 字节帧的最后一字节
        int checksumIndex = cmdOffset + FrameSize - 1;
        if (checksumIndex < frame.Length) frame[checksumIndex] = sum;

        return frame;
    }

    /// <summary>checkSum = 85 - (sum([8, ...frame[0..14]]) &amp; 255)，作用于 16 字节帧。</summary>
    private static byte CheckSum(byte[] frame, int offset)
    {
        int sum = 8;
        for (int i = offset; i < offset + 15 && i < frame.Length; i++) sum += frame[i];
        return (byte)(85 - (sum & 255));
    }

    /// <summary>解析电量：兼容「命令在 raw[0]」与「带 Report ID 后移一位」两种布局。</summary>
    private static bool TryRead(byte[]? resp, out int percent, out byte charge)
    {
        percent = -1;
        charge = 0;
        if (resp == null || resp.Length < 7) return false;

        // 优先看 raw[5]（驱动定义的 baseOffset），再试 raw[6]
        foreach (int off in new[] { 5, 6 })
        {
            if (off >= resp.Length) continue;
            int v = resp[off];
            if (v > 0 && v <= 100)
            {
                percent = v;
                charge = off + 1 < resp.Length ? resp[off + 1] : (byte)0;
                return true;
            }
        }
        return false;
    }

    private static bool IsEcho(byte[] sent, byte[] recv)
    {
        if (sent.Length < 3 || recv.Length < 3) return false;
        int n = Math.Min(sent.Length, recv.Length);
        for (int i = 0; i < n; i++)
        {
            if (sent[i] != recv[i]) return false;
        }
        return true;
    }

    private static string Hex(byte[]? b, int max = 20)
    {
        if (b == null) return "(无响应)";
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(b.Length, max); i++) sb.Append($"{b[i]:X2} ");
        return sb.ToString().Trim() + (b.Length > max ? $" ...({b.Length}B)" : "");
    }
}
