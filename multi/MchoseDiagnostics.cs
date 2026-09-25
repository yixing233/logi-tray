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
/// **已定案**（本工具采到双向样本后）：`[3]=0x00` 是充电、`[3]=0x02` 是放电，
/// 旧映射两个方向都错了。本工具保留下来用于补采尚未观测到的取值（如满电）。
///
/// 本工具把每次轮询的**原始字节**按时间打印出来。三个状态里已采到两个：
///   1. 放电时 [3] = `0x02`（连续 15+ 次）；
///   2. 充电时 [3] = `0x00`（连续 6 次，电量 30%）；
///   3. 充满后是什么 —— **仍未观测到**。
/// 顺带确认了 `[2]` 确实是纯电量：充电期间它稳定在同一数值，不因充电改义。
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
        Console.WriteLine("字段位置： [0]=0x55 [1]=0x65 [2]=电量% [3]=状态(0x00 充电 / 0x02 放电)");
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
                            ? "           [3] 是未观测到的取值 → 显示「未知」，不下结论"
                            : $"           → 映射为「{text}」(charging={charging})");
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
                Console.WriteLine("已校准：放电 = 0x02，充电 = 0x00。");
                Console.WriteLine("尚未观测到「已充满」档 —— 满电后请再采一次，看 [3] 是否变成新取值。");
                Console.WriteLine("按 Ctrl+C 结束。");
                Console.WriteLine();
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 74));
        Console.WriteLine("已校准：放电 0x02 / 充电 0x00。仍未观测「已充满」档。");
        return 0;
    }
}
