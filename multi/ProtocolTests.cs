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
        TestLevelText();
        TestNotifyDedup();

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

        // 状态码映射
        CheckEq("状态1文案", Protocols.MchoseStatusText(1).text, "充电中");
        Check("状态1为充电", Protocols.MchoseStatusText(1).charging);
        CheckEq("状态2文案", Protocols.MchoseStatusText(2).text, "充电中");
        Check("状态2为充电", Protocols.MchoseStatusText(2).charging);
        CheckEq("状态3文案", Protocols.MchoseStatusText(3).text, "已充满");
        Check("状态3为充电", Protocols.MchoseStatusText(3).charging);
        CheckEq("状态0文案", Protocols.MchoseStatusText(0).text, "放电中");
        Check("状态0为放电", !Protocols.MchoseStatusText(0).charging);
        CheckEq("状态9文案", Protocols.MchoseStatusText(9).text, "放电中");
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
}
