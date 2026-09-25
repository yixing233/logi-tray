using System;
using System.Collections.Generic;

namespace MultiTray;

/// <summary>
/// 协议解析层自检。
///
/// 为什么需要它：本机三台设备对厂商私有帧都不应答（实测记录见 README），
/// 因此整条读取链路无法用真机验证。这个自检把**不需要硬件的那一半**
/// ——字节到电量的换算——用合成帧做穷尽验证，包括：
///   * 正常帧
///   * 边界值（0、100、101、255）
///   * 非法帧（错帧头、长度不足、null）
///   * 已知会误判的形态（例如 ATK 协议 2 的响应头校验）
///   * 通知去重逻辑
/// 这样「读不到」只可能来自设备无应答，而不会来自解析写错。
/// </summary>
public static class ProtocolTests
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> _failures = new();

    public static int Run()
    {
        _passed = 0;
        _failed = 0;
        _failures.Clear();

        Console.WriteLine();
        Console.WriteLine("多品牌电量协议解析层自检");
        Console.WriteLine(new string('=', 66));

        TestMchose();
        TestAtk1();
        TestAtk2();
        TestEchoRejection();
        TestAtkKeyboard();
        TestAtkKeyboardSequence();
        TestLevelText();
        TestNotifyDedup();
        TestBatteryHistory();

        Console.WriteLine(new string('=', 66));
        Console.WriteLine($"通过 {_passed} 项，失败 {_failed} 项");
        foreach (string f in _failures) Console.WriteLine("  失败: " + f);

        if (_failed == 0)
        {
            // 用纯文本标记，避免在非 UTF-8 的控制台里显示成问号
            Console.WriteLine("协议解析层自检通过（PASS）");
            return 0;
        }
        Console.WriteLine("协议解析层自检未通过（FAIL）");
        return 1;
    }

    // ───────────── 断言辅助 ─────────────

    private static void Check(string label, bool condition)
    {
        if (condition) { _passed++; }
        else { _failed++; _failures.Add(label); }
    }

    private static void CheckEq(string label, object? actual, object? expected)
    {
        bool ok = Equals(actual, expected);
        if (ok) { _passed++; }
        else
        {
            _failed++;
            _failures.Add($"{label} (实际={actual}, 期望={expected})");
        }
    }

    // ───────────── 迈从 ─────────────

    private static void TestMchose()
    {
        Console.WriteLine("\n[迈从 MCHOSE]");

        // 请求帧构造：头固定为 55 65 01，其余为 0
        var req = Protocols.BuildMchoseRequest(64);
        CheckEq("请求长度", req.Length, 64);
        CheckEq("请求[0]", req[0], (byte)0x55);
        CheckEq("请求[1]", req[1], (byte)0x65);
        CheckEq("请求[2]", req[2], (byte)0x01);
        CheckEq("请求[3..] 全为 0", req[3], (byte)0x00);
        CheckEq("请求末字节为 0", req[63], (byte)0x00);

        // 长度不足时应拒绝而不是产生越界
        bool threw = false;
        try { Protocols.BuildMchoseRequest(2); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Check("长度 2 应抛异常", threw);

        // 正常响应：电量 82，状态 1
        var good = new byte[] { 0x55, 0x65, 82, 0x01 };
        Check("正常帧解析成功", Protocols.TryParseMchose(good, out int p1, out byte s1));
        CheckEq("电量", p1, 82);
        CheckEq("状态码", s1, (byte)0x01);

        // 边界：0 与 100 都应接受（0 可能真的意味着耗尽）
        Check("电量 0 可解析", Protocols.TryParseMchose(
            new byte[] { 0x55, 0x65, 0, 0x00 }, out int p0, out _));
        CheckEq("电量 0 值", p0, 0);
        Check("电量 100 可解析", Protocols.TryParseMchose(
            new byte[] { 0x55, 0x65, 100, 0x03 }, out int p100, out _));
        CheckEq("电量 100 值", p100, 100);

        // 101 与 255 超出合理范围，必须拒绝（否则会把噪声当电量）
        Check("电量 101 应拒绝", !Protocols.TryParseMchose(
            new byte[] { 0x55, 0x65, 101, 0x00 }, out _, out _));
        Check("电量 255 应拒绝", !Protocols.TryParseMchose(
            new byte[] { 0x55, 0x65, 255, 0x00 }, out _, out _));

        // 帧头不符
        Check("错帧头应拒绝", !Protocols.TryParseMchose(
            new byte[] { 0xAA, 0x65, 50, 0x00 }, out _, out _));
        Check("错第二字节应拒绝", !Protocols.TryParseMchose(
            new byte[] { 0x55, 0x66, 50, 0x00 }, out _, out _));

        // 长度不足与 null
        Check("长度 3 应拒绝", !Protocols.TryParseMchose(
            new byte[] { 0x55, 0x65, 50 }, out _, out _));
        Check("null 应拒绝", !Protocols.TryParseMchose(null, out _, out _));

        // 状态码映射：**基于真机双向采样**，不是猜测。
        //
        // 采样证据（--diag-mchose watch）：
        //   插上充电器、电量 30%，连续 6 次  → [3]=0x00
        //   明确放电使用，连续 15+ 次        → [3]=0x02
        //
        // 历史教训：旧映射把 1/2 当归零为「充电中」、3 当「已充满」，
        // 是照抄上游后凭字面猜的，**两个方向都错了** —— 真实的 0x02 是放电，
        // 界面因此在用户一直放电时永远显示「充电中」。
        CheckEq("充电态文案", Protocols.MchoseStatusText(0x00).text, "充电中");
        Check("充电态判为充电", Protocols.MchoseStatusText(0x00).charging);
        CheckEq("放电态文案", Protocols.MchoseStatusText(0x02).text, "放电中");
        Check("放电态不判为充电", !Protocols.MchoseStatusText(0x02).charging);

        // 常量与映射必须一致（防止将来只改一处）
        CheckEq("充电常量", Protocols.MchoseStatusCharging, (byte)0x00);
        CheckEq("放电常量", Protocols.MchoseStatusDischarging, (byte)0x02);

        // 未观测到的取值一律不下结论，由界面显示「状态未知」。
        // 尤其 0x01/0x03：旧猜测把 3 当「已充满」，无任何依据。
        foreach (byte st in new byte[] { 1, 3, 4, 9, 0xFF })
        {
            var (text, charging) = Protocols.MchoseStatusText(st);
            CheckEq($"未观测状态{st} 不给出文案", text, "");
            Check($"未观测状态{st} 不判为充电", !charging);
        }
    }

    // ───────────── ATK 协议 1 ─────────────

    private static void TestAtk1()
    {
        Console.WriteLine("\n[ATK 协议 1]");

        var req = Protocols.BuildAtk1Request();
        CheckEq("请求长度", req.Length, 17);
        CheckEq("请求[0]=ReportID", req[0], (byte)0x08);
        CheckEq("请求[1]", req[1], (byte)0x04);
        CheckEq("请求[16]", req[16], (byte)0x49);

        var req2 = Protocols.BuildAtk1Request(0x5A);
        CheckEq("自定义 ReportID", req2[0], (byte)0x5A);
        CheckEq("自定义时长不变", req2.Length, 17);

        // 正常响应：电量在 [6]
        var good = new byte[17];
        good[6] = 77;
        Check("正常帧解析成功", Protocols.TryParseAtk1(good, out int p));
        CheckEq("电量", p, 77);

        // 电量 0 视为无效（未充上电的设备不会报 0；报 0 多为空帧）
        var zero = new byte[17];
        Check("电量 0 应判定无效", !Protocols.TryParseAtk1(zero, out _));

        // 超范围
        var over = new byte[17];
        over[6] = 101;
        Check("电量 101 应判定无效", !Protocols.TryParseAtk1(over, out _));
        over[6] = 255;
        Check("电量 255 应判定无效", !Protocols.TryParseAtk1(over, out _));

        // 边界 1 与 100 有效
        var low = new byte[17];
        low[6] = 1;
        Check("电量 1 有效", Protocols.TryParseAtk1(low, out int p1));
        CheckEq("电量 1 值", p1, 1);
        var high = new byte[17];
        high[6] = 100;
        Check("电量 100 有效", Protocols.TryParseAtk1(high, out int p100));
        CheckEq("电量 100 值", p100, 100);

        // 长度不足与 null
        Check("长度 6 应判定无效", !Protocols.TryParseAtk1(new byte[6], out _));
        Check("null 应判定无效", !Protocols.TryParseAtk1(null, out _));
    }

    // ───────────── ATK 协议 2 ─────────────

    private static void TestAtk2()
    {
        Console.WriteLine("\n[ATK 协议 2]");

        var wireless = Protocols.BuildAtk2Request(wired: false);
        CheckEq("无线请求长度", wireless.Length, 64);
        CheckEq("无线请求[0]=ReportID", wireless[0], (byte)0x08);
        CheckEq("无线请求[1]=0x7D", wireless[1], (byte)0x7D);
        CheckEq("无线请求[2]=0x72", wireless[2], (byte)0x72);
        CheckEq("无线请求[3]=0x02", wireless[3], (byte)0x02);
        CheckEq("无线请求[5]=0x01", wireless[5], (byte)0x01);
        CheckEq("无线请求[6]=0x07", wireless[6], (byte)0x07);
        CheckEq("无线请求[7]=0x01", wireless[7], (byte)0x01);

        var wired = Protocols.BuildAtk2Request(wired: true);
        CheckEq("有线请求[1]=0x7C", wired[1], (byte)0x7C);
        CheckEq("有线请求[5]=0x00", wired[5], (byte)0x00);

        // 正常响应：需 [1]=0x72 且 [5]=0x07，电量在 [7]
        var good = new byte[64];
        good[1] = 0x72;
        good[5] = 0x07;
        good[7] = 55;
        Check("正常帧解析成功", Protocols.TryParseAtk2(good, out int p, out bool rel));
        CheckEq("电量", p, 55);
        Check("标记为可靠", rel);

        // 缺少响应头校验时必须拒绝 —— 这是最容易误判的地方：
        // 只看 [7] 会把任意噪声当成电量
        var noHeader = new byte[64];
        noHeader[7] = 55;
        Check("缺 [1]=0x72 应拒绝", !Protocols.TryParseAtk2(noHeader, out _, out _));

        var noMarker = new byte[64];
        noMarker[1] = 0x72;
        noMarker[7] = 55;
        Check("缺 [5]=0x07 应拒绝", !Protocols.TryParseAtk2(noMarker, out _, out _));

        // 响应头正确但电量不合理
        var badVal = new byte[64];
        badVal[1] = 0x72;
        badVal[5] = 0x07;
        badVal[7] = 0;
        Check("电量 0 应拒绝", !Protocols.TryParseAtk2(badVal, out _, out _));
        badVal[7] = 101;
        Check("电量 101 应拒绝", !Protocols.TryParseAtk2(badVal, out _, out _));

        // 边界
        badVal[7] = 1;
        Check("电量 1 有效", Protocols.TryParseAtk2(badVal, out int p1, out _));
        CheckEq("电量 1 值", p1, 1);
        badVal[7] = 100;
        Check("电量 100 有效", Protocols.TryParseAtk2(badVal, out int p100, out _));
        CheckEq("电量 100 值", p100, 100);

        // 长度不足与 null
        Check("长度 7 应拒绝", !Protocols.TryParseAtk2(new byte[7], out _, out _));
        Check("null 应拒绝", !Protocols.TryParseAtk2(null, out _, out _));
    }

    // ───────────── 回显防护 ─────────────

    /// <summary>
    /// 实测本机 ATK Z87 键盘（373B:1012）会把请求帧**原样回送**：
    ///     发送 04 7D 72 02 00 01 07 01 … → 收到 04 7D 72 02 00 01 07 01 …
    /// 若不识别这种回显，请求里的字节会被当成电量
    /// （回显的 [7]=0x01 会被误报成「1%」）。
    /// 这里验证：回显被拒绝，而正常应答不被误伤。
    /// </summary>
    private static void TestEchoRejection()
    {
        Console.WriteLine("\n[回显防护]");

        // 本机 ATK 的真实回显样本（无线帧）
        var sent = Protocols.BuildAtk2Request(wired: false, reportId: 0x04);
        var echo = (byte[])sent.Clone();
        Check("回显应被识别", IsEcho(sent, echo));

        // 关键：若不做回显判定，这个回显会被当成什么？
        // 回显 [1]=0x7D 而协议2要求 [1]=0x72，因此响应头校验已经能挡住它 ——
        // 这是第二道防线，值得单独确认。
        var resp = Protocols.TryParseAtk2(echo, out int pct, out bool rel);
        Check("回显不应被协议2接受", !resp);
        Check("回显不应给出电量", pct == -1);
        Check("回显不应标记为可靠", !rel);

        // 首字节（Report ID）被设备改写时，仍应认出是回显
        var echo2 = (byte[])sent.Clone();
        echo2[0] = 0x00;
        Check("Report ID 被改写仍应识别为回显", IsEcho(sent, echo2));

        // 正常应答不应被误判为回显
        var good = new byte[64];
        good[1] = 0x72;
        good[5] = 0x07;
        good[7] = 55;
        Check("正常应答不应被当成回显", !IsEcho(sent, good));

        // 只有首字节不同、其余相同的短帧不应触发（长度不足 3 无法判定）
        Check("过短帧不判定为回显", !IsEcho(new byte[] { 0x01, 0x02 },
                                            new byte[] { 0x01, 0x02 }));

        // 协议1 的回显：写 [1]=0x04 后读回同样的 0x04
        var atk1 = Protocols.BuildAtk1Request(0x5A);
        var atk1Echo = (byte[])atk1.Clone();
        Check("协议1 回显应被识别", IsEcho(atk1, atk1Echo));
        Check("协议1 全零回显不含电量",
            !Protocols.TryParseAtk1(new byte[17], out _));
    }

    /// <summary>与 AtkProvider.LooksLikeEcho 相同的判定（忽略首字节）。</summary>
    private static bool IsEcho(byte[] sent, byte[] received)
    {
        int n = Math.Min(sent.Length, received.Length);
        if (n <= 2) return false;
        for (int i = 1; i < n; i++)
        {
            if (sent[i] != received[i]) return false;
        }
        return true;
    }

    // ───────────── ATK Z87 键盘 ─────────────

    /// <summary>
    /// 用抓包实录的字节验证 Z87 键盘协议。
    ///
    /// 真机实测的唯一成功样本（`--diag-atk` 实录，64 字节中的前 10 字节）：
    ///     04 11 00 1A 00 00 00 00 64 02 …
    /// 索引       0  1  2  3  4  5  6  7  8  9
    /// 首字节 0x04 等于 Report ID，故载荷整体后移一位（b=1）：
    ///   [b+2]=[3]=0x1A 命令码 ✓   [b+7]=[8]=0x64(100) 电量   [b+8]=[9]=0x02 状态
    /// </summary>
    private static void TestAtkKeyboard()
    {
        Console.WriteLine("\n[ATK Z87 键盘]");

        // 真机实录样本：按抓包逐字节构造，不做任何「看起来合理」的假设
        var real = new byte[64];
        real[0] = 0x04; real[1] = 0x11; real[2] = 0x00; real[3] = 0x1A;
        real[8] = 0x64; real[9] = 0x02;

        Check("真机样本应解析成功", Protocols.TryParseAtkKbdPower(real, out int p, out bool chg));
        CheckEq("真机样本电量", p, 100);
        CheckEq("真机样本状态", chg, true);

        // 缓冲区首字节被驱动剥掉（无 Report ID）时也应能解析：b=0
        var noRid = new byte[63];
        Array.Copy(real, 1, noRid, 0, 63);
        Check("无 Report ID 前缀也应解析",
            Protocols.TryParseAtkKbdPower(noRid, out int p2, out _));
        CheckEq("无 Report ID 前缀电量", p2, 100);

        // 放电态：[b+8]==0 不得标成充电中
        var discharging = (byte[])real.Clone();
        discharging[9] = 0x00;
        Check("放电态应解析成功",
            Protocols.TryParseAtkKbdPower(discharging, out int p3, out bool chg3));
        CheckEq("放电态电量", p3, 100);
        CheckEq("放电态不应标充电", chg3, false);

        // 命令码不符：设备拒绝命令时会把 [2] 回成 0xFF（驱动源码里 0xFF = error）
        var rejected = (byte[])real.Clone();
        rejected[3] = 0xFF;
        Check("命令码 0xFF 应拒绝", !Protocols.TryParseAtkKbdPower(rejected, out _, out _));

        // 前导帧的应答（命令码 0x1B）不应被当成电量帧。
        // 实测 LED 帧应答：04 01 00 1B 18 00 00 00 00 01 … → [8]=0x00 [9]=0x01
        var ledFrame = new byte[64];
        ledFrame[0] = 0x04; ledFrame[1] = 0x01; ledFrame[3] = 0x1B;
        ledFrame[4] = 0x18; ledFrame[9] = 0x01;
        Check("LED 前导帧不应被当成电量",
            !Protocols.TryParseAtkKbdPower(ledFrame, out _, out _));

        // 边界：0% 视为无效（未充上电的设备不会报 0）
        var zero = (byte[])real.Clone();
        zero[8] = 0x00;
        Check("电量 0 应视为无效", !Protocols.TryParseAtkKbdPower(zero, out _, out _));

        // 越界截断
        var over = (byte[])real.Clone();
        over[8] = 0xFF;
        Check("越界电量应解析但截断为 100",
            Protocols.TryParseAtkKbdPower(over, out int p4, out _));
        CheckEq("越界电量截断值", p4, 100);

        // 长度不足与 null
        Check("长度 8 应拒绝", !Protocols.TryParseAtkKbdPower(new byte[8], out _, out _));
        Check("null 应拒绝", !Protocols.TryParseAtkKbdPower(null, out _, out _));
    }

    /// <summary>
    /// 验证 17 帧前导序列复现抓包。
    ///
    /// 抓包实录（每条载荷首 3 字节）：
    ///   01 00 1b / 02 00 1b / … 0e 00 1b / 0f 00 03 / 10 00 03 / 11 00 1a
    /// LED 帧 [4..5] 为 16 位小端偏移，逐帧 +0x18。
    /// </summary>
    private static void TestAtkKeyboardSequence()
    {
        Console.WriteLine("\n[ATK Z87 前导序列]");

        var frames = Protocols.BuildAtkKbdSequence();

        CheckEq("帧数应为 17", frames.Count, 17);
        CheckEq("帧长应为 64", frames[0].Length, Protocols.AtkKbdFrameLength);

        // 序号：1..17 全局递增
        for (int i = 0; i < frames.Count; i++)
            CheckEq($"第 {i + 1} 帧序号", frames[i][1], (byte)(i + 1));

        // 报告号恒为 4，[2] 恒为 0
        Check("报告号恒为 4", frames.All(f => f[0] == 4));
        Check("保留字节恒为 0", frames.All(f => f[2] == 0x00));

        // 前 14 帧是 LED 矩阵，命令码 0x1B，偏移逐帧 +0x18
        for (int i = 0; i < 14; i++)
        {
            var f = frames[i];
            CheckEq($"LED 帧 {i + 1} 命令码", f[3], (byte)0x1B);
            CheckEq($"LED 帧 {i + 1} [4]", f[4], (byte)0x18);
            int offset = i * 0x18;
            int got = f[5] | (f[6] << 8);
            CheckEq($"LED 帧 {i + 1} 偏移", got, offset);
        }

        // 抓包逐字核对关键帧
        CheckEq("第 1 帧载荷 [2..4]", $"00 {frames[0][3]:X2} {frames[0][4]:X2}", "00 1B 18");
        CheckEq("第 2 帧偏移", frames[1][5], (byte)0x18);
        CheckEq("第 14 帧偏移低字节", frames[13][5], (byte)0x38);
        CheckEq("第 14 帧偏移高字节", frames[13][6], (byte)0x01);

        // 第 15/16 帧是键盘信息（cmd 0x03）
        CheckEq("第 15 帧命令码", frames[14][3], (byte)0x03);
        CheckEq("第 15 帧 [4]", frames[14][4], (byte)0x18);
        CheckEq("第 16 帧命令码", frames[15][3], (byte)0x03);
        CheckEq("第 16 帧 [4]", frames[15][4], (byte)0x0E);
        CheckEq("第 16 帧 [5]", frames[15][5], (byte)0x18);

        // 最后一帧才是电量命令
        CheckEq("末帧命令码", frames[16][3], Protocols.AtkKbdPowerCmd);

        // 自定义帧长（诊断用 32）也应成立
        var thin = Protocols.BuildAtkKbdSequence(32);
        CheckEq("自定义帧长帧数", thin.Count, 17);
        CheckEq("自定义帧长", thin[0].Length, 32);
        CheckEq("自定义帧长末帧命令码", thin[16][3], Protocols.AtkKbdPowerCmd);
    }

    // ───────────── 档位文案 ─────────────

    private static void TestLevelText()
    {
        Console.WriteLine("\n[档位文案]");
        CheckEq("-1 未知", Protocols.LevelText(-1), "未知");
        CheckEq("0 极低", Protocols.LevelText(0), "极低");
        CheckEq("5 极低", Protocols.LevelText(5), "极低");
        CheckEq("6 偏低", Protocols.LevelText(6), "偏低");
        CheckEq("20 偏低", Protocols.LevelText(20), "偏低");
        CheckEq("21 一般", Protocols.LevelText(21), "一般");
        CheckEq("50 一般", Protocols.LevelText(50), "一般");
        CheckEq("51 良好", Protocols.LevelText(51), "良好");
        CheckEq("80 良好", Protocols.LevelText(80), "良好");
        CheckEq("81 充足", Protocols.LevelText(81), "充足");
        CheckEq("100 充足", Protocols.LevelText(100), "充足");
    }

    // ───────────── 低电量通知去重 ─────────────

    private static void TestNotifyDedup()
    {
        Console.WriteLine("\n[低电量通知]");

        // 正常触发：低于阈值、未充电、与上次不同
        Check("15% 阈值20 应通知",
            Protocols.ShouldNotify(15, false, 20, -1));
        // 已通知过同一电量，不再重复
        Check("15% 已通知过则不再通知",
            !Protocols.ShouldNotify(15, false, 20, 15));
        // 电量继续下降，应再次通知
        Check("降到 12% 应再次通知",
            Protocols.ShouldNotify(12, false, 20, 15));
        // 充电中不通知
        Check("充电中不通知",
            !Protocols.ShouldNotify(15, true, 20, -1));
        // 高于阈值不通知
        Check("25% 超阈值不通知",
            !Protocols.ShouldNotify(25, false, 20, -1));
        // 恰好等于阈值应通知
        Check("恰好 20% 应通知",
            Protocols.ShouldNotify(20, false, 20, -1));
        // 未读到电量不通知
        Check("-1 不通知",
            !Protocols.ShouldNotify(-1, false, 20, -1));
        // 严重阈值也走同一逻辑
        Check("5% 严重阈值应通知",
            Protocols.ShouldNotify(5, false, 10, -1));
    }

    // ───────────── 休眠前电量记忆 ─────────────

    /// <summary>
    /// 纯逻辑自检：不碰文件系统，直接喂合成的读数列表。
    ///
    /// 需求（用户逐字）：「设备休眠的时候增加小字显示休眠之前的最后一次电量」。
    /// 这里要守住三件事：
    ///   1) 在线读数会被记住，离线时能取回；
    ///   2) 回填只写 LastKnownPercent，**绝不动 Percent**（否则大号数字
    ///      会用旧值冒充当前值，比不显示更糟）；
    ///   3) 键对不上时只有唯一候选才回退，多个候选宁可放弃。
    /// </summary>
    private static void TestBatteryHistory()
    {
        Console.WriteLine("\n[休眠前电量]");

        var now = new DateTime(2026, 1, 10, 12, 0, 0);

        // ── 1. 在线读到 → 记住 → 离线回填 ──
        var map = new Dictionary<string, BatteryHistory.Entry>();
        var online = new List<DeviceReading>
        {
            new() { Name = "PRO X Wireless", Key = "logitech:PRO X Wireless",
                    Percent = 77, IsOnline = true }
        };
        Check("在线读数应触发落盘", BatteryHistory.ApplyTo(map, online, now));
        CheckEq("在线读数被记住", map["logitech:PRO X Wireless"].Percent, 77);

        var offline = new List<DeviceReading>
        {
            new() { Name = "罗技设备", Key = "logitech:unknown",
                    Percent = -1, IsOnline = false }
        };
        BatteryHistory.ApplyTo(map, offline, now.AddMinutes(30));
        CheckEq("唯一候选可回退（键从设备名退化成 unknown）",
                offline[0].LastKnownPercent, 77);
        CheckEq("回填时不动当前读数", offline[0].Percent, -1);
        Check("HasLastKnown 成立", offline[0].HasLastKnown);
        CheckEq("小字文案",
                BatteryHistory.Describe(offline[0].LastKnownPercent,
                                        offline[0].LastKnownAt, now.AddHours(2)),
                "上次 77% · 2 小时前");

        // ── 2. 有多个候选时不猜 ──
        var map2 = new Dictionary<string, BatteryHistory.Entry>
        {
            ["logitech:A"] = new() { Percent = 50, At = now },
            ["logitech:B"] = new() { Percent = 60, At = now },
        };
        var amb = new List<DeviceReading>
        {
            new() { Name = "罗技设备", Key = "logitech:unknown",
                    Percent = -1, IsOnline = false }
        };
        BatteryHistory.ApplyTo(map2, amb, now);
        CheckEq("多候选不张冠李戴", amb[0].LastKnownPercent, -1);
        Check("多候选时 HasLastKnown 为假", !amb[0].HasLastKnown);

        // ── 3. 精确命中优先于回退 ──
        var map3 = new Dictionary<string, BatteryHistory.Entry>
        {
            ["logitech:A"] = new() { Percent = 50, At = now },
            ["logitech:B"] = new() { Percent = 60, At = now },
        };
        var exact = new List<DeviceReading>
        {
            new() { Name = "A", Key = "logitech:A", Percent = -1, IsOnline = false }
        };
        BatteryHistory.ApplyTo(map3, exact, now);
        CheckEq("精确命中取自己的记录", exact[0].LastKnownPercent, 50);

        // ── 4. 过期记录不再使用 ──
        var stale = new Dictionary<string, BatteryHistory.Entry>
        {
            ["logitech:A"] = new() { Percent = 50, At = now },
        };
        var later = new List<DeviceReading>
        {
            new() { Name = "A", Key = "logitech:A", Percent = -1, IsOnline = false }
        };
        BatteryHistory.ApplyTo(stale, later,
            now.AddDays(BatteryHistory.MaxAgeDays + 1));
        CheckEq("超过 MaxAgeDays 不再回填", later[0].LastKnownPercent, -1);
        Check("过期记录被清出表", !stale.ContainsKey("logitech:A"));

        // ── 5. 在线时绝不使用历史值（哪怕表里有） ──
        var map5 = new Dictionary<string, BatteryHistory.Entry>
        {
            ["logitech:A"] = new() { Percent = 50, At = now },
        };
        var live = new List<DeviceReading>
        {
            new() { Name = "A", Key = "logitech:A", Percent = 90, IsOnline = true }
        };
        BatteryHistory.ApplyTo(map5, live, now.AddMinutes(1));
        Check("在线设备不回填历史", live[0].LastKnownPercent == -1);
        Check("在线设备 HasLastKnown 为假", !live[0].HasLastKnown);
        CheckEq("在线读数覆盖旧记录", map5["logitech:A"].Percent, 90);

        // ── 6. 没有记忆的离线设备仍显示 --% ──
        var empty = new List<DeviceReading>
        {
            new() { Name = "X", Key = "mchose:xxx", Percent = -1, IsOnline = false }
        };
        BatteryHistory.ApplyTo(new Dictionary<string, BatteryHistory.Entry>(),
                               empty, now);
        CheckEq("无记忆时不回填", empty[0].LastKnownPercent, -1);
        Check("无记忆时 HasLastKnown 为假", !empty[0].HasLastKnown);

        // ── 7. 时间文案 ──
        CheckEq("刚刚", BatteryHistory.FormatAge(now.AddSeconds(-20), now), "刚刚");
        CheckEq("分钟", BatteryHistory.FormatAge(now.AddMinutes(-5), now), "5 分钟前");
        CheckEq("小时", BatteryHistory.FormatAge(now.AddHours(-3), now), "3 小时前");
        CheckEq("天", BatteryHistory.FormatAge(now.AddDays(-2), now), "2 天前");
        CheckEq("未来时间按刚刚处理",
                BatteryHistory.FormatAge(now.AddMinutes(5), now), "刚刚");
        CheckEq("无时间戳时只显示电量",
                BatteryHistory.Describe(42, null, now), "上次 42%");
    }
}
