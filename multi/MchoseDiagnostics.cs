using System;
using System.Collections.Generic;
using System.Linq;

namespace MultiTray;

/// <summary>
/// 迈从（MCHOSE）设备电量诊断（`--diag-mchose`）。
///
/// ── 为什么需要它 ──
///
/// 电量百分比一直是准的，状态位却错了：耳机一直在用（放电），界面却始终显示
/// 「充电中」。原因是 <see cref="Protocols.MchoseStatusText"/> 里的状态码映射
/// 是**照抄上游实现后凭字面猜测**的（1/2 → 充电、3 → 充满），从未拿真机校准过。
/// 电量字节对了，就默认状态字节也对了 —— 这是本次 bug 的根源。
///
/// 本工具把每次轮询的**原始字节**按时间打印出来，用于确定：
///   1. 放电时 [3] 到底是什么值；
///   2. 插上充电器后 [3] 变成什么；
///   3. 充满后又是什么。
/// 只有把三种真实状态都采到，映射才谈得上正确。
///
/// 另一个待确认点：`[2]` 是否真的是「电量」。如果接口对同一请求返回的 [2]
/// 会随充电变化，那么它可能并非纯电量，需要重新认定。
///
/// 本工具**只读不写**，不改动任何协议实现。
/// </summary>
internal static class MchoseDiagnostics
{
    private const ushort Vid = 0x291D;
    private const ushort Pid = 0x385D;

    /// <summary>默认轮询 1 次；`--diag-mchose watch` 时每秒一次持续采样。</summary>
    public static int Run(bool watch)
    {
        ConsoleSession.UseUtf8Output();
        Console.WriteLine();
        Console.WriteLine("迈从 MCHOSE 电量诊断");
        Console.WriteLine(new string('=', 74));

        var all = Hid.Enumerate();
        var targets = all.Where(x => x.VendorId == Vid && x.ProductId == Pid).ToList();

        Console.WriteLine($"匹配 {Vid:X4}:{Pid:X4} 的接口 {targets.Count} 个：");
        foreach (var d in targets) Console.WriteLine($"  {d}");
        if (targets.Count == 0)
        {
            Console.WriteLine("\n未找到设备。请确认耳机已开机并连接。");
            return 2;
        }

        var usable = targets.Where(x => x.InputLength >= 64 && x.OutputLength >= 64).ToList();
        if (usable.Count == 0)
        {
            Console.WriteLine("\n没有 64/64 的可用接口（该协议需要一个能收发 64 字节的接口）。");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine("字段位置： [0]=0x55 [1]=0x65 [2]=电量? [3]=状态? [4..]=未知");
        Console.WriteLine();

        int rounds = watch ? int.MaxValue : 1;
        for (int i = 0; i < rounds; i++)
        {
            if (i > 0) System.Threading.Thread.Sleep(1000);

            string stamp = DateTime.Now.ToString("HH:mm:ss");
            foreach (var d in usable)
            {
                var frame = Protocols.BuildMchoseRequest(d.OutputLength);
                var resp = Hid.WriteRead(d.Path, frame, d.InputLength, 900);

                if (resp == null)
                {
                    Console.WriteLine($"[{stamp}] 无应答（{d.UsagePage:X4}/{d.Usage:X4}）");
                    continue;
                }

                string hex = string.Join(' ', resp.Take(16).Select(b => b.ToString("X2")));
                Console.WriteLine($"[{stamp}] 收 {hex}  …(共 {resp.Length}B)");

                if (resp.Length >= 4)
                {
                    Console.WriteLine($"           [2]=0x{resp[2]:X2} ({resp[2]})   " +
                                      $"[3]=0x{resp[3]:X2} ({resp[3]})");

                    if (Protocols.TryParseMchose(resp, out int pct, out byte st))
                    {
                        var (text, charging) = Protocols.MchoseStatusText(st);
                        Console.WriteLine($"           解析：电量 {pct}%  状态字节 [3]=0x{st:X2}");
                        Console.WriteLine(string.IsNullOrEmpty(text)
                            ? "           [3] 当前未做解释（映射尚未校准）→ 不判为充电"
                            : $"           → 当前映射为「{text}」(charging={charging})");
                    }
                    else
                    {
                        Console.WriteLine("           解析：不满足迈从帧头/范围，未采纳");
                    }
                }
            }

            if (watch && i == 0)
            {
                Console.WriteLine();
                Console.WriteLine("持续采样中（每秒一次）。");
                Console.WriteLine("请保持耳机在**放电**状态观察 [3]；然后插上充电器再观察一次。");
                Console.WriteLine("按 Ctrl+C 结束。");
                Console.WriteLine();
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 74));
        Console.WriteLine("请把上面的原始输出反馈给作者，用于确定状态码的正确映射。");
        return 0;
    }
}
