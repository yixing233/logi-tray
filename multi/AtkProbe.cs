using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MultiTray;

/// <summary>
/// 针对 ATK Z87 键盘的重点排查。
///
/// 背景（重要）：迈从耳机在同一轮里已经能读到电量（40%，来源「迈从 55 65」），
/// 说明厂商私有帧的收发路径是通的 —— 之前读不到只是设备休眠。
/// 因此 ATK 仍读不到，更可能是「帧格式/报告 ID 与这个型号不匹配」，
/// 而不是设备不应答。用户正在使用键盘，所以它此刻必然是唤醒状态。
///
/// 已知接口（实测枚举）：
///   373B:1012  UP=0x0001  IN=9   OUT=2   FEAT=65
///   373B:1012  UP=0xFF1C  IN=64  OUT=64  FEAT=0
///   373B:1012  UP=0xFFEF  IN=64  OUT=64  FEAT=0
///   048D:5702  UP=0xFF89  FEAT=17
///   048D:5702  UP=0xFF89  FEAT=64
///
/// 公开实现（Fan4Metal/ATK_tray）针对的型号 usage_page 是 0xFF02，
/// 与本机的 0xFF1C/0xFFEF 不同，说明该型号走的是另一组接口。
/// 这里把以下几种可能都试一遍，并把原始响应打出来供判断：
///   1. 特征报告 17 字节：逐 Report ID 试「写 [1]=0x04,[16]=0x49 再读回」
///   2. 输出报告 64 字节 + 中断读：试 ATK 协议 2 的无线/有线帧、多个 Report ID
///   3. 特征报告 64/65 字节：把协议 2 的帧塞进特征报告再读回
/// 判定：只有解析出 1..100 的合理电量才算命中；否则如实报告读不到。
/// </summary>
internal static class AtkProbe
{
    public static int Run()
    {
        ConsoleSession.UseUtf8Output();
        Console.WriteLine();
        Console.WriteLine("ATK Z87 重点排查");
        Console.WriteLine(new string('=', 70));

        var all = Hid.Enumerate();
        var targets = all.Where(x => x.VendorId == 0x373B || x.VendorId == 0x048D)
                         .ToList();

        Console.WriteLine($"ATK 相关接口 {targets.Count} 个:");
        foreach (var d in targets) Console.WriteLine($"  {d}");

        int hits = 0;

        foreach (var d in targets)
        {
            Console.WriteLine();
            Console.WriteLine(new string('-', 70));
            Console.WriteLine(d.ToString());
            Console.WriteLine(new string('-', 70));

            // ── 1. 特征报告：协议 1（17 字节）逐 Report ID ──
            if (d.FeatureLength >= 17)
            {
                foreach (byte rid in CandidateIds())
                {
                    // 先看该 ID 是否可读
                    var pre = Hid.GetFeature(d.Path, rid, d.FeatureLength);
                    if (pre == null) continue;

                    var frame = new byte[d.FeatureLength];
                    frame[0] = rid;
                    if (frame.Length > 1) frame[1] = 0x04;
                    if (frame.Length > 16) frame[16] = 0x49;

                    bool wrote = Hid.SetFeature(d.Path, frame);
                    var back = wrote ? Hid.GetFeature(d.Path, rid, d.FeatureLength) : null;

                    Console.WriteLine($"  FEAT rid=0x{rid:X2} 写={wrote}");
                    Console.WriteLine($"    读回 {Hex(back)}");

                    if (Protocols.TryParseAtk1(back, out int p1))
                    {
                        Console.WriteLine($"    >>> 协议1 命中：电量 {p1}%");
                        hits++;
                    }
                }
            }

            // ── 2. 输出报告 + 中断读：协议 2 的两种帧 ──
            if (d.OutputLength >= 64 && d.InputLength >= 64)
            {
                foreach (bool wired in new[] { false, true })
                {
                    foreach (byte rid in CandidateIds())
                    {
                        var frame = Protocols.BuildAtk2Request(wired, rid);
                        if (frame.Length != d.OutputLength)
                        {
                            var sized = new byte[d.OutputLength];
                            Array.Copy(frame, sized, Math.Min(frame.Length, sized.Length));
                            if (sized.Length > 0) sized[0] = rid;
                            frame = sized;
                        }

                        var resp = Hid.WriteRead(d.Path, frame, d.InputLength, 700);
                        if (resp == null) continue;

                        Console.WriteLine($"  OUT rid=0x{rid:X2} {(wired ? "有线" : "无线")} " +
                                          $"-> {Hex(resp)}");
                        if (Protocols.TryParseAtk2(resp, out int p2, out bool rel) && rel)
                        {
                            Console.WriteLine($"    >>> 协议2 命中：电量 {p2}%");
                            hits++;
                        }
                    }
                }
            }

            // ── 3. 大特征报告（64/65 字节）装协议 2 的帧 ──
            if (d.FeatureLength >= 64)
            {
                foreach (byte rid in CandidateIds())
                {
                    var probe = Hid.GetFeature(d.Path, rid, d.FeatureLength);
                    if (probe == null) continue;

                    foreach (bool wired in new[] { false, true })
                    {
                        var frame = new byte[d.FeatureLength];
                        int off = rid == 0 ? 0 : 1;
                        if (off + 7 >= frame.Length) continue;
                        frame[0] = rid;
                        frame[off] = (byte)(wired ? 0x7C : 0x7D);
                        frame[off + 1] = 0x72;
                        frame[off + 2] = 0x02;
                        frame[off + 4] = (byte)(wired ? 0x00 : 0x01);
                        frame[off + 5] = 0x07;
                        frame[off + 6] = 0x01;

                        if (!Hid.SetFeature(d.Path, frame)) continue;
                        var back = Hid.GetFeature(d.Path, rid, d.FeatureLength);
                        Console.WriteLine($"  FEAT64 rid=0x{rid:X2} {(wired ? "有线" : "无线")} " +
                                          $"-> {Hex(back)}");
                        for (int k = 0; k + 7 < (back?.Length ?? 0); k++)
                        {
                            if (back![k] == 0x72 && back[k + 6] > 0 && back[k + 6] <= 100)
                            {
                                Console.WriteLine($"    >>> 命中：电量 {back[k + 6]}%");
                                hits++;
                            }
                        }
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 70));
        Console.WriteLine(hits > 0
            ? $"命中 {hits} 次 —— ATK 可读"
            : "未命中：ATK Z87 对本程序尝试的所有组合均无有效响应");
        Console.WriteLine(new string('=', 70));
        return hits > 0 ? 0 : 2;
    }

    /// <summary>候选 Report ID：公开实现用 0x08，实测本机接口出现过 0x5A/0xED/0xCC。</summary>
    private static IEnumerable<byte> CandidateIds()
    {
        foreach (byte b in new byte[]
                 { 0x08, 0x5A, 0xED, 0xCC, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05,
                   0x06, 0x07, 0x09, 0x0A })
        {
            yield return b;
        }
    }

    private static string Hex(byte[]? b, int max = 20)
    {
        if (b == null) return "(null)";
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(b.Length, max); i++) sb.Append($"{b[i]:X2} ");
        return sb.ToString().Trim() + (b.Length > max ? $" ...({b.Length}B)" : "");
    }
}
