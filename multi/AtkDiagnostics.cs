using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace MultiTray;

/// <summary>
/// ATK 设备电量读取的诊断工具（`--diag-atk`）。
///
/// 合并了开发过程中写下的多个临时探测程序，保留其中真正有用的部分，
/// 便于以后针对新型号排查，而不是留下一堆半成品。
///
/// ── 协议（两个独立来源交叉验证一致）──
///
///   A. 第三方 Fan4Metal/ATK_tray（Python，最后更新 2026-07）
///        report = [0]*17
///        report[0] = 8      # Report ID
///        report[1] = 4      # 命令
///        report[16] = 73    # 0x49
///        write; sleep(0.1); read(17); battery = res[6]
///
///   B. 官方网页驱动 hub.atk.pro v3.2.27（Vite bundle 逆向）
///        16 字节帧：raw[0]=commandId，raw[15]=checkSum
///        GetBatteryLevel = 4；电量 = raw[baseOffset]，baseOffset = 5
///        checkSum = 85 - (sum([8, raw[0..14]]) &amp; 255)
///
///   对命令 4 计算：85 - (8+4) = 73 = 0x49，与 A 中硬编码的字节完全一致；
///   电量偏移也对齐（A 的 res[6] 含 Report ID，去掉后即数据索引 5）。
///   两个项目互不引用，却推出同一套帧 —— 帧格式可信。
///
/// ── 本机实测结论 ──
///
///   设备表（官方驱动）与本机接口完全吻合：
///     {vendorId:0x373B, productId:0x1012, usagePage:0xFF1C, usage:0x92,
///      connectType:0, productName:"ATK Z87 Pro 2.4G Dongle"}
///
///   但无论用哪种传输、哪个 Report ID，都读不到电量：
///     * 0xFF1C / 0xFFEF 对 WriteFile 返回 error 87，改用控制端点后写入成功但无应答
///     * 048D:5702 的 17 字节特征接口会返回
///         5A 04 00 ... 00（请求末字节 0x49 被设备清零）
///       说明命令被"接住"了，但这不是含电量的应答
///     * 未观察到任何主动上报
///
///   因此 ATK 键盘目前显示 `--`。本工具保留下来是为了在换型号或将来有
///   新线索时能快速复查，而不是假装它能工作。
/// </summary>
internal static class AtkDiagnostics
{
    /// <summary>GetBatteryLevel。</summary>
    private const byte Cmd = 4;

    /// <summary>ATK 家族厂商 ID；048D 是键盘上附带的 ITE 复合接口，同属一把键盘。</summary>
    private static readonly ushort[] Vids = { 0x373B, 0x3554, 0x048D };

    /// <summary>上游实现 write 与 read 之间 sleep(0.1)，这里留足余量。</summary>
    private const int SettleMs = 150;

    /// <summary>候选 Report ID：上游用 0x08；本机描述符里出现过 0x5A / 0xED / 0xCC。</summary>
    private static readonly byte[] ReportIds = { 0x08, 0x5A, 0xED, 0xCC, 0x00 };

    public static int Run()
    {
        ConsoleSession.UseUtf8Output();
        Console.WriteLine();
        Console.WriteLine("ATK 电量诊断");
        Console.WriteLine(new string('=', 74));

        var all = Hid.Enumerate();
        var targets = all.Where(x => Vids.Contains(x.VendorId)).ToList();

        Console.WriteLine($"候选接口 {targets.Count} 个：");
        foreach (var d in targets) Console.WriteLine($"  {d}");

        int hits = 0;
        var notes = new List<string>();

        foreach (var d in targets)
        {
            bool canFeature = d.FeatureLength >= 16;
            bool canWrite = d.OutputLength >= 16;
            bool hasInput = d.InputLength >= 16;
            if (!canFeature && !canWrite && !hasInput) continue;

            Console.WriteLine();
            Console.WriteLine(new string('-', 74));
            Console.WriteLine(d.ToString());
            Console.WriteLine(new string('-', 74));

            foreach (byte rid in ReportIds)
            {
                var frame = Build(rid);

                // ── 传输 1：输出报告 + 中断读（WriteFile）──
                if (canWrite && hasInput)
                {
                    var resp = Hid.WriteThenRead(d.Path, frame, d.InputLength,
                                                 SettleMs, 800);
                    if (resp != null)
                    {
                        Console.WriteLine($"  [中断写] rid=0x{rid:X2}");
                        Console.WriteLine($"    发 {Hex(frame)}");
                        Console.WriteLine($"    收 {Hex(resp)}");
                        if (Accept(resp, frame, out int p1))
                        {
                            Console.WriteLine($"    >>> 电量 {p1}%");
                            hits++;
                        }
                    }
                }

                // ── 传输 2：控制端点 Set_Output → Get_Input ──
                // 带编号报告的设备常必须走这条路；本机 0xFF1C 对 WriteFile
                // 返回 error 87，只有这条路径真正写入成功。
                if (canWrite || d.OutputLength == 0)
                {
                    var resp = Hid.ControlRoundTrip(
                        d.Path, frame, Math.Max(16, (int)d.InputLength), SettleMs);
                    if (resp != null)
                    {
                        Console.WriteLine($"  [控制端点] rid=0x{rid:X2}");
                        Console.WriteLine($"    发 {Hex(frame)}");
                        Console.WriteLine($"    收 {Hex(resp)}");
                        if (Accept(resp, frame, out int p2))
                        {
                            Console.WriteLine($"    >>> 电量 {p2}%");
                            hits++;
                        }
                        else
                        {
                            notes.Add($"{d.Id} rid=0x{rid:X2} 有响应但无电量: {Hex(resp, 12)}");
                        }
                    }
                }

                // ── 传输 3：特征报告 SET → 等待 → GET ──
                if (canFeature)
                {
                    if (Hid.SetFeature(d.Path, frame))
                    {
                        Thread.Sleep(SettleMs);
                        var back = Hid.GetFeature(d.Path, rid, d.FeatureLength);
                        if (back != null)
                        {
                            Console.WriteLine($"  [特征报告] rid=0x{rid:X2}");
                            Console.WriteLine($"    发 {Hex(frame)}");
                            Console.WriteLine($"    收 {Hex(back)}");
                            if (Accept(back, frame, out int p3))
                            {
                                Console.WriteLine($"    >>> 电量 {p3}%");
                                hits++;
                            }
                            else
                            {
                                notes.Add($"{d.Id} FEAT rid=0x{rid:X2} 有响应但无电量: {Hex(back, 12)}");
                            }
                        }
                    }
                }
            }

            // ── 被动监听：部分接收器会主动上报 ──
            if (hasInput)
            {
                var unsolicited = Hid.Listen(d.Path, d.InputLength, 800);
                if (unsolicited != null)
                {
                    Console.WriteLine($"  [被动监听] {Hex(unsolicited)}");
                    if (Accept(unsolicited, Array.Empty<byte>(), out int p4))
                    {
                        Console.WriteLine($"    >>> 电量 {p4}%");
                        hits++;
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 74));
        if (hits > 0)
        {
            Console.WriteLine($"读到 {hits} 次电量");
        }
        else
        {
            Console.WriteLine("未读到 ATK 电量。");
            Console.WriteLine();
            Console.WriteLine("已排除的原因：");
            Console.WriteLine("  * 接口不匹配 —— 官方驱动设备表与本机接口逐字段吻合");
            Console.WriteLine("  * 帧格式错误 —— 第三方实现与官方驱动交叉验证一致");
            Console.WriteLine("  * 传输方式 —— 中断写 / 控制端点 / 特征报告 三条路径都试过");
            Console.WriteLine();
            Console.WriteLine("仍有响应但不符合电量格式的接口：");
            foreach (var n in notes.Distinct().Take(6)) Console.WriteLine("  " + n);
            Console.WriteLine();
            Console.WriteLine("下一步可行的排查方式：");
            Console.WriteLine("  用 USB 抓包（Wireshark + USBPcap）在官方 ATK HUB 读取电量时录一段，");
            Console.WriteLine("  即可看到真实的请求与应答，无需再猜。");
        }
        Console.WriteLine(new string('=', 74));
        return hits > 0 ? 0 : 2;
    }

    /// <summary>构造报文：带 Report ID 时 17 字节，否则 16 字节。</summary>
    private static byte[] Build(byte rid)
    {
        if (rid == 0)
        {
            var f = new byte[16];
            f[0] = Cmd;
            f[15] = CheckSum(f);
            return f;
        }
        var frame = new byte[17];
        frame[0] = rid;
        frame[1] = Cmd;
        frame[16] = CheckSum(frame.Skip(1).Take(16).ToArray());
        return frame;
    }

    /// <summary>官方驱动的校验和：85 - (sum([8, 前15字节]) &amp; 255)。</summary>
    private static byte CheckSum(byte[] frame16)
    {
        int sum = 8;
        for (int i = 0; i < 15 && i < frame16.Length; i++) sum += frame16[i];
        return (byte)(85 - (sum & 255));
    }

    /// <summary>
    /// 判断响应是否含有效电量。
    /// 位置：无 Report ID 时数据第 5 字节，含 Report ID 时后移一位。
    /// 排除全零与逐字节回显 —— 两者都不含设备信息。
    /// </summary>
    private static bool Accept(byte[]? resp, byte[] req, out int percent)
    {
        percent = -1;
        if (resp == null || resp.Length < 7) return false;
        if (resp.All(b => b == 0)) return false;

        if (req.Length >= 3)
        {
            int n = Math.Min(resp.Length, req.Length);
            bool echo = true;
            for (int i = 0; i < n && echo; i++)
                if (resp[i] != req[i]) echo = false;
            if (echo) return false;
        }

        foreach (int off in new[] { 5, 6 })
        {
            if (off >= resp.Length) continue;
            int v = resp[off];
            if (v > 0 && v <= 100) { percent = v; return true; }
        }
        return false;
    }

    private static string Hex(byte[]? b, int max = 18)
    {
        if (b == null) return "(无响应)";
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(b.Length, max); i++) sb.Append($"{b[i]:X2} ");
        return sb.ToString().Trim() + (b.Length > max ? $" ...({b.Length}B)" : "");
    }
}
