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
/// ── 本机实测结论：Z87 键盘已破（2026-08）──
///
///   设备表（官方驱动）与本机接口完全吻合：
///     {vendorId:0x373B, productId:0x1012, usagePage:0xFF1C, usage:0x92,
///      connectType:0, productName:"ATK Z87 Pro 2.4G Dongle"}
///
///   承载接口只有一个：`373B:1012 UP=0xFF1C IN=64 OUT=64 FEAT=0`。
///   真机实录（`--diag-atk`）：
///     收 04 11 00 1A 00 00 00 00 64 02 00 …(64B) → 电量 100%，充电中
///
///   两个曾长期被忽略的根因：
///     1. 帧长必须是 **64**。协议本身只用到 31 字节载荷，但发 32 字节会被
///        `WriteFile` 拒绝（Win32 错误 87 = ERROR_INVALID_PARAMETER），
///        必须补齐到该接口的输出报告长度。
///     2. 命令 0x1A 必须跟在固定的 17 帧前导之后（14 帧 LED 矩阵 + 2 帧键盘
///        信息）。单发那一帧时设备把命令字节回成 0xFF —— 驱动源码里
///        0xFF 是明确的错误返回，不是「无响应」。
///
///   传输方式：**只有中断写（WriteFile）+ 中断读**有效。控制端点写
///   （HidD_SetOutputReport）能发但不通，恒返回 Win32 错误 0。
///   慢节拍（每帧 settle+drain）与快节拍（12ms 连发）都能读到。
///
///   UP=0xFFEF 接口完全不可用（中断写错误 1 / 控制写错误 0）；
///   048D:5702 UP=0xFF89 只回 `5A 04 00 … 00`（末字节被清零），无电量。
///
/// 本工具保留下来是为了在换型号或将来有新线索时能快速复查。
/// </summary>
internal static class AtkDiagnostics
{
    /// <summary>GetBatteryLevel（鼠标协议）。</summary>
    private const byte Cmd = 4;

    // ── Z87 系列键盘协议（WebHID 抓包实证，见类注释末尾）──

    /// <summary>键盘输出报告的 Report ID（抓包固定为 4）。</summary>
    private const byte KbdReportId = 4;

    /// <summary>键盘电量命令 power_info = 26 = 0x1A。</summary>
    private const byte KbdPowerCmd = 0x1A;

    /// <summary>驱动实际发出的输出报告**载荷**长度（不含 Report ID）。</summary>
    private const int KbdPayloadLength = 31;

    /// <summary>输入报告缓冲长度（含 Report ID）；对应 63 字节载荷。</summary>
    private const int KbdBufferLength = 64;

    /// <summary>Win32 最小整帧长度：报告号 1 + 载荷 31。</summary>
    private const int KbdFrameLength = KbdPayloadLength + 1;

    /// <summary>请求序号；驱动每次请求递增，设备会把同样的值回填到应答里。</summary>
    private static byte _kbdSeq;

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

        // ── 先试 Z87 系列键盘协议（WebHID 抓包实证）──
        Console.WriteLine();
        Console.WriteLine(new string('=', 74));
        Console.WriteLine("Z87 键盘协议（31 字节输出报告 rid=4，cmd=0x1A）");
        Console.WriteLine(new string('=', 74));

        int kbdHits = 0;
        foreach (var d in targets.Where(x => x.OutputLength >= KbdFrameLength &&
                                             x.InputLength >= KbdFrameLength))
        {
            Console.WriteLine();
            Console.WriteLine(d.ToString());

            // ── 报告描述符：唯一能确定「某个报告号实际多长」的信息源 ──
            var lens = Hid.GetReportLengths(d.Path);
            if (lens == null)
            {
                Console.WriteLine("  报告描述符: 读取失败");
            }
            else
            {
                Console.WriteLine($"  报告描述符: 带报告号={lens.HasReportIds}");
                Console.WriteLine($"    输入长度 {Join(lens.Input)}");
                Console.WriteLine($"    输出长度 {Join(lens.Output)}");
            }

            var rawDesc = Hid.GetReportDescriptor(d.Path);
            Console.WriteLine(rawDesc == null
                ? "    原始描述符: 读取失败"
                : $"    原始描述符({rawDesc.Length}B): {Hex(rawDesc, 64)}");

            // 帧长以描述符为准：报告号 4 的实际总长（含报告号字节）。
            // 拿不到描述符时退回「1 + 31」与「补齐到 GET_CAPS」两种猜测。
            var frameLens = new List<int>();
            if (lens != null && lens.Output.TryGetValue(KbdReportId, out int realOut))
                frameLens.Add(realOut);
            foreach (int guess in new[] { KbdFrameLength, KbdBufferLength })
                if (!frameLens.Contains(guess)) frameLens.Add(guess);

            foreach (int frameLen in frameLens)
            {
                // 实验证明：应答是异步到达的，开/关句柄会把它冲掉，
                // 因此统一改为**单次句柄 + 先发握手前导 + 全程排空读取**。
                //
                // 两条节拍都保留：抓包里 34 帧落在同一秒内（背靠背连发），
                // 但真机实测慢节拍（每帧 settle+drain）与快节拍（12ms 连发）
                // **都能读到电量**，说明 0xFF 与节拍无关，纯粹是前导缺失。
                // 保留两条是为了换型号时能立刻分辨「时序敏感」还是「协议不符」。
                foreach (bool viaControl in new[] { false, true })
                {
                    string how = viaControl ? "控制写" : "中断写";

                    var slow = Hid.ProbeDrain(d.Path, BuildKeyboardProbeSequence(frameLen),
                                              d.InputLength, 60, 260,
                                              writeViaControl: viaControl);
                    Report(how + "+排空读", frameLen, slow, ref kbdHits);

                    var fast = Hid.ProbeBurst(d.Path, BuildKeyboardProbeSequence(frameLen),
                                              d.InputLength, 12, 400,
                                              writeViaControl: viaControl);
                    Report(how + "+连发  ", frameLen, fast, ref kbdHits);
                }
            }
        }

        if (kbdHits == 0)
            Console.WriteLine("\n  Z87 协议未读到电量（继续试旧协议）。");

        Console.WriteLine();
        Console.WriteLine(new string('=', 74));
        Console.WriteLine("旧协议探测");
        Console.WriteLine(new string('=', 74));

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
            Console.WriteLine("注：上面这些结论只针对**旧协议**（鼠标 GetBatteryLevel=4）。");
            Console.WriteLine("    Z87 键盘走的是另一套 31 字节协议，见本文件顶部的抓包记录。");
        }
        Console.WriteLine(new string('=', 74));
        return hits > 0 ? 0 : 2;
    }

    /// <summary>
    /// 打印一次交换的全部报告，并对每个报告尝试解析电量。
    /// 抽出来是因为现在有「排空读」与「连发」两条路径共用同一套输出与计数。
    /// </summary>
    private static void Report(string how, int frameLen, Hid.ExchangeResult ex,
                               ref int kbdHits)
    {
        Console.WriteLine($"  [{how}] 帧长 {frameLen}: {ex.Describe()}");
        foreach (var rep in ex.Reports)
        {
            Console.WriteLine($"    收 {Hex(rep, 34)}");

            // 关键诊断：回复里 [2] 是不是命令码。
            // 驱动源码里 `if (e.raw.getUint8(2) == 255) return { result: false, msg: "error" }`，
            // 所以 0xFF 是设备明确的「命令被拒绝」，不是数据。
            int b = (rep.Length > 0 && rep[0] == KbdReportId) ? 1 : 0;
            if (rep.Length > b + 2)
                Console.WriteLine($"      [2]=0x{rep[b + 2]:X2}" +
                                  (rep[b + 2] == 0xFF ? "  ← 设备返回错误" : "") +
                                  $"  [7]=0x{rep[b + 7]:X2}  [8]=0x{rep[b + 8]:X2}");
        }

        foreach (var rep in ex.Reports)
        {
            if (!TryParseKeyboardPower(rep, out int pct, out bool charging))
                continue;
            Console.WriteLine($"    >>> 电量 {pct}%" +
                              (charging ? "（充电中）" : "（放电中）"));
            kbdHits++;
        }
    }

    /// <summary>
    /// 按抓包顺序复现 Z87 键盘的 17 帧前导序列：
    ///
    /// 抓包显示驱动在问电量之前，先做了一轮固定前导：
    ///   14 帧 `0x1B`(get_led_matrix)：载荷 [3]=0x18，[4..5]=16 位偏移，逐帧 +0x18
    ///       `01 00 1b 18 00`、`02 00 1b 18 18`、`03 00 1b 18 30` … `0e 00 1b 18 38 01`
    ///   2 帧 `0x03`(get_keyboard_info)：`0f 00 03 18`、`10 00 03 0e 18`
    ///   1 帧 `0x1A`(power_info)：`11 00 1a 00`
    /// 之后又重复了一整轮相同的 14+2+1。
    ///
    /// 序号是**全局递增**的（1..17 跨命令共享），设备会把序号原样回填到应答里，
    /// 因此不能只发那一帧 0x1A —— 单个裸请求在真机上被回以载荷 [2]=0xFF，
    /// 与「命令被拒绝」相符。
    /// </summary>
    private static List<byte[]> BuildKeyboardProbeSequence(int frameLength)
    {
        var frames = new List<byte[]>();
        _kbdSeq = 0;

        for (int i = 0; i < 14; i++)
        {
            int offset = i * 0x18;
            var f = NewKbdFrame(frameLength, 0x1B);
            f[4] = 0x18;
            f[5] = (byte)(offset & 0xFF);
            f[6] = (byte)((offset >> 8) & 0xFF);
            frames.Add(f);
        }

        var info1 = NewKbdFrame(frameLength, 0x03);
        info1[4] = 0x18;
        frames.Add(info1);

        var info2 = NewKbdFrame(frameLength, 0x03);
        info2[4] = 0x0e;
        info2[5] = 0x18;
        frames.Add(info2);

        frames.Add(NewKbdFrame(frameLength, KbdPowerCmd));
        return frames;
    }

    /// <summary>
    /// 造一帧键盘请求。
    ///
    /// 抓包里的 `<c>sendReport(reportId, data)</c>` 把报告号与载荷分开传，
    /// 载荷 31 字节：<c>[0]=序号 [1]=0x00 [2]=命令码</c>。
    /// Win32 管道要求把报告号并进缓冲区首字节，故此处 0 号位是报告号，
    /// 载荷整体后移一位 —— 与 <see cref="TryParseKeyboardPower"/> 的识别逻辑对应。
    /// </summary>
    private static byte[] NewKbdFrame(int frameLength, byte cmd)
    {
        var frame = new byte[frameLength];
        frame[0] = KbdReportId;
        frame[1] = ++_kbdSeq;
        frame[2] = 0x00;
        frame[3] = cmd;
        return frame;
    }

    /// <summary>
    /// 解析 Z87 键盘电量应答。
    ///
    /// 语义取自驱动源码，非猜测：
    ///   power    = payload[7] &gt; 100 ? 100 : payload[7]
    ///   charging = payload[8] != 0    // batteryCharge === 0 ? discharging : charging
    /// 抓包实录载荷：`11 00 1a 00 00 00 00 64 02 00 ...`
    ///   → payload[0]=0x11(序号) payload[2]=0x1A(命令) payload[7]=0x64(100) payload[8]=0x02(充电)
    ///
    /// Win32 读到的缓冲区首字节可能是 Report ID，也可能被驱动剥掉，
    /// 因此这里自动识别：首字节等于 Report ID 就整体后移一位。
    /// </summary>
    internal static bool TryParseKeyboardPower(byte[]? resp, out int percent,
                                               out bool charging)
    {
        percent = -1;
        charging = false;
        if (resp == null) return false;

        int b = (resp.Length > 0 && resp[0] == KbdReportId) ? 1 : 0;
        if (resp.Length < b + 9) return false;

        // 帧头校验：命令码必须对得上，否则不是这条命令的应答
        if (resp[b + 2] != KbdPowerCmd) return false;

        int p = resp[b + 7];
        if (p > 100) p = 100;
        if (p <= 0) return false;

        percent = p;
        // 驱动源码：battery: batteryCharge === 0 ? Ns.discharging : Ns.charging
        // 即「非零即充电」，不需要数值到名字的映射。
        charging = resp[b + 8] != 0;
        return true;
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

    /// <summary>把「报告号 → 长度」字典排成一行，供诊断输出。</summary>
    private static string Join(Dictionary<byte, int> map)
        => map.Count == 0
            ? "(无)"
            : string.Join("  ", map.OrderBy(kv => kv.Key)
                                   .Select(kv => $"0x{kv.Key:X2}={kv.Value}"));
}
