using System;
using System.Collections.Generic;

namespace MultiTray;

/// <summary>
/// 各品牌电量协议的**纯解析层**：只做字节到电量/状态的换算，不碰任何 I/O。
///
/// 拆分出这一层，是为了让协议逻辑脱离硬件就能被穷尽测试：把真机采到的帧
/// 固化成断言，任何改动一旦解析错位都会立刻在 `--test-protocols` 里暴露，
/// 而不是等到界面上显示一个错的电量。
///
/// 协议来源（均为公开实现或官方驱动，非猜测）：
///   * 迈从 MCHOSE V9 Pro —— rafagfran/mchose-v9-pro-battery-tray
///       请求 64 字节 [0]=0x55 [1]=0x65 [2]=0x01
///       响应 [0]=0x55 [1]=0x65 [2]=电量% [3]=状态码（0x00 充电 / 0x02 放电，真机校准）
///   * ATK —— Fan4Metal/ATK_tray 的 models.py
///       协议1：17 字节特征报告，[0]=ReportID [1]=0x04 [16]=0x49，电量在 [6]
///       协议2：64 字节，[2]=0x72；无线 [1]=0x7D [5]=0x01，有线 [1]=0x7C [5]=0x00
///              响应需 [1]=0x72 且 [5]=0x07，无线电量在 [7]
///   * 罗技 HID++ 2.0 —— 由已有 native 读取器负责，这里只解析它的文本输出
/// </summary>
public static class Protocols
{
    // ───────────────────────── 迈从 MCHOSE ─────────────────────────

    /// <summary>迈从请求帧的固定头。</summary>
    public static readonly byte[] MchoseRequestHeader = { 0x55, 0x65, 0x01 };

    /// <summary>迈从状态字节：充电中（真机采样，插充电器时连续 6 次均为该值）。</summary>
    public const byte MchoseStatusCharging = 0x00;

    /// <summary>迈从状态字节：放电中（真机采样，明确放电使用时连续 15+ 次均为该值）。</summary>
    public const byte MchoseStatusDischarging = 0x02;

    /// <summary>构造迈从电量请求帧（长度由接口的报告长度决定）。</summary>
    public static byte[] BuildMchoseRequest(int reportLength)
    {
        if (reportLength < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(reportLength),
                "报告长度至少 3 字节");
        }

        var frame = new byte[reportLength];
        frame[0] = 0x55;
        frame[1] = 0x65;
        frame[2] = 0x01;
        return frame;
    }

    /// <summary>
    /// 解析迈从响应。成功时 <paramref name="percent"/> 为 0..100。
    /// 判定依据（照搬公开实现并加严）：
    ///   * 长度至少 4
    ///   * [0]==0x55 且 [1]==0x65（帧头）
    ///   * [2] &lt;= 100（电量必须落在合理范围内，否则视为不是电量帧）
    /// </summary>
    public static bool TryParseMchose(byte[]? frame, out int percent, out byte status)
    {
        percent = -1;
        status = 0;
        if (frame == null || frame.Length < 4) return false;
        if (frame[0] != 0x55 || frame[1] != 0x65) return false;
        if (frame[2] > 100) return false;

        percent = frame[2];
        status = frame[3];
        return true;
    }

    /// <summary>
    /// 迈从状态字节（响应 [3]）→ 文案与充电标志。
    ///
    /// 映射依据是**真机双向采样**（`--diag-mchose watch`），不是照抄猜测：
    ///
    ///   | [3]  | 实测场景                             | 结论   |
    ///   |------|--------------------------------------|--------|
    ///   | 0x00 | 插上充电器后连续 6 次采样，电量 30%  | 充电中 |
    ///   | 0x02 | 明确放电使用时连续 15+ 次采样        | 放电中 |
    ///   | 其它 | **未观测到**                         | 未知   |
    ///
    /// 这里曾经把 1/2 映射为「充电中」、3 映射为「已充满」——那是**凭字面猜的**，
    /// 从未校准，而且**两个方向都错了**：真实的 0x02 其实是放电，
    /// 界面因此在用户一直放电使用时永远显示「充电中」。
    ///
    /// 上游参考实现（rafagfran/mchose-v9-pro-battery-tray，协议即取自它）
    /// 至今仍只记录不解释这个字节（「registrado mas NÃO interpretado」），
    /// 它自己的抓包 `55 65 14 02`（20%，[3]=0x02）也与我们一致。
    ///
    /// 尚未观测到 0x01、0x03（旧猜测里「已充满」用的就是 3，**无任何依据**）。
    /// 对未观测到的取值一律返回空文案，由调用方按「状态未知」显示 ——
    /// 宁可说不知道，也不再给出一个确定错了的状态：虚假的「充电中」还会
    /// 连带压掉低电量提醒（见 <see cref="ShouldNotify"/>）。
    ///
    /// 要补上「已充满」这一档，请在满电时采样：`multi-tray.exe --diag-mchose watch`。
    /// </summary>
    public static (string text, bool charging) MchoseStatusText(byte status) => status switch
    {
        MchoseStatusCharging => ("充电中", true),
        MchoseStatusDischarging => ("放电中", false),
        // 未观测到的取值：不下结论（空文案即「状态未知」）
        _ => ("", false),
    };

    // ───────────────────────── ATK ─────────────────────────

    /// <summary>ATK 协议 1 的报告长度。</summary>
    public const int AtkProtocol1Length = 17;

    /// <summary>ATK 协议 2 的报告长度。</summary>
    public const int AtkProtocol2Length = 64;

    /// <summary>构造 ATK 协议 1 请求（17 字节特征报告）。</summary>
    public static byte[] BuildAtk1Request(byte reportId = 0x08)
    {
        var frame = new byte[AtkProtocol1Length];
        frame[0] = reportId;
        frame[1] = 0x04;
        frame[16] = 0x49;
        return frame;
    }

    /// <summary>
    /// 解析 ATK 协议 1 响应：电量在 [6]。
    /// 公开实现未记录响应头，因此这里只校验长度与取值范围；
    /// 读到 0 视为无效（未充上电的设备不会报 0）。
    /// </summary>
    public static bool TryParseAtk1(byte[]? frame, out int percent)
    {
        percent = -1;
        if (frame == null || frame.Length < 7) return false;
        int p = frame[6];
        if (p <= 0 || p > 100) return false;
        percent = p;
        return true;
    }

    /// <summary>构造 ATK 协议 2 请求。<paramref name="wired"/> 决定 [1]/[5]。</summary>
    public static byte[] BuildAtk2Request(bool wired, byte reportId = 0x08)
    {
        var frame = new byte[AtkProtocol2Length];
        frame[0] = reportId;
        frame[1] = (byte)(wired ? 0x7C : 0x7D);
        frame[2] = 0x72;
        frame[3] = 0x02;
        frame[4] = 0x00;
        frame[5] = (byte)(wired ? 0x00 : 0x01);
        frame[6] = 0x07;
        frame[7] = 0x01;
        return frame;
    }

    // ─────────── ATK Z87 系列键盘（WebHID 抓包实证）───────────

    /// <summary>Z87 键盘协议的报告号。</summary>
    public const byte AtkKbdReportId = 4;

    /// <summary>Z87 键盘的电量命令码（驱动里的 <c>power_info = 26</c>）。</summary>
    public const byte AtkKbdPowerCmd = 0x1A;

    /// <summary>
    /// Z87 键盘帧的发送长度。
    ///
    /// 载荷本身只有 31 字节，但该接口的输出报告是 64 字节，
    /// Win32 中断写要求缓冲区**正好等于**报告长度：发 32 字节会
    /// 直接失败（Win32 错误 87 ERROR_INVALID_PARAMETER），发 64 字节才通。
    /// </summary>
    public const int AtkKbdFrameLength = 64;

    /// <summary>
    /// 构造 Z87 键盘的完整请求序列：14 帧 LED 矩阵 + 2 帧键盘信息 + 1 帧电量。
    ///
    /// 抓包显示驱动「问电量」从来不是单发一帧 0x1A，而是先做一轮固定前导。
    /// 只发那一帧 0x1A 时设备会把命令字节回成 0xFF（明确的「拒绝」）；
    /// 复现完整前导后，[2] 才会被正确回填 0x1A 并带回电量。
    ///
    /// 帧布局（**载荷**；Win32 侧把 Report ID 并入缓冲区首字节，故整体后移一位）：
    ///   [0]=全局递增序号(1..17) [1]=0x00 [2]=命令码
    ///   LED 帧另有 [3]=0x18、[4..5]=16 位小端偏移，逐帧 +0x18
    /// 抓包实录：`01 00 1b 18 00`、`02 00 1b 18 18` … `0e 00 1b 18 38 01`、
    /// `0f 00 03 18`、`10 00 03 0e 18`、`11 00 1a`。
    /// </summary>
    public static List<byte[]> BuildAtkKbdSequence(int frameLength = AtkKbdFrameLength)
    {
        var frames = new List<byte[]>();
        byte seq = 0;

        byte[] New(byte cmd)
        {
            var f = new byte[frameLength];
            f[0] = AtkKbdReportId;
            f[1] = ++seq;
            f[3] = cmd;
            return f;
        }

        for (int i = 0; i < 14; i++)
        {
            var f = New(0x1B);
            int offset = i * 0x18;
            f[4] = 0x18;
            f[5] = (byte)(offset & 0xFF);
            f[6] = (byte)((offset >> 8) & 0xFF);
            frames.Add(f);
        }

        var info1 = New(0x03);
        info1[4] = 0x18;
        frames.Add(info1);

        var info2 = New(0x03);
        info2[4] = 0x0E;
        info2[5] = 0x18;
        frames.Add(info2);

        frames.Add(New(AtkKbdPowerCmd));
        return frames;
    }

    /// <summary>
    /// 解析 Z87 键盘电量应答。实测帧：`04 11 00 1A 00 00 00 00 64 02 …`
    ///   [3]=0x1A  [7]=电量(0x64=100)  [8]=状态(0x02=充电中)
    ///
    /// 驱动源码对应关系：`power = batteryCharge === 0 ? batteryLevel : void 0`，
    /// 而 `batteryLevel`/`batteryCharge` 分别取载荷偏移 +2/+3 —— 即本函数
    /// 的 [7]/[8]（含 Report ID 偏移一位）。
    ///
    /// 缓冲区首字节可能是 Report ID 也可能被驱动剥掉，故自动识别。
    /// 命令码对不上就返回 false：设备在拒绝命令时会把 [2] 回成 0xFF。
    /// </summary>
    public static bool TryParseAtkKbdPower(byte[]? resp, out int percent, out bool charging)
    {
        percent = -1;
        charging = false;
        if (resp == null) return false;

        int b = (resp.Length > 0 && resp[0] == AtkKbdReportId) ? 1 : 0;
        if (resp.Length < b + 9) return false;
        if (resp[b + 2] != AtkKbdPowerCmd) return false;

        int p = resp[b + 7];
        if (p > 100) p = 100;
        if (p <= 0) return false;

        percent = p;
        // 驱动源码的判据是 batteryCharge !== 0 即视为充电中
        // （`battery: batteryCharge === 0 ? Ns.discharging : Ns.charging`），
        // 因此这里只判非零，不硬编码 0x02。
        charging = resp[b + 8] != 0;
        return true;
    }

    /// <summary>
    /// 解析 ATK 协议 2 响应。
    /// 判据（照搬公开实现）：[1]==0x72 且 [5]==0x07；无线时电量在 [7]。
    /// 有线连接公开实现明确说明「不提供可靠电量」，因此这里对有线的
    /// [7] 也做范围校验，越界即判定为不可用。
    /// </summary>
    public static bool TryParseAtk2(byte[]? frame, out int percent, out bool reliable)
    {
        percent = -1;
        reliable = false;
        if (frame == null || frame.Length < 8) return false;
        if (frame[1] != 0x72 || frame[5] != 0x07) return false;

        int p = frame[7];
        if (p <= 0 || p > 100) return false;
        percent = p;
        reliable = true;
        return true;
    }

    // ───────────────────────── 通用 ─────────────────────────

    /// <summary>把电量折算成档位文案，用于没有明确状态码的设备。</summary>
    public static string LevelText(int percent) => percent switch
    {
        < 0 => "未知",
        <= 5 => "极低",
        <= 20 => "偏低",
        <= 50 => "一般",
        <= 80 => "良好",
        _ => "充足",
    };

    /// <summary>
    /// 判断是否应触发低电量通知。
    ///
    /// 规则：电量必须已读到（&gt;=0）、不在充电、且不超过阈值。
    /// 另外要求 <paramref name="lastNotifiedPercent"/> 与当前值不同，
    /// 以免同一个电量每轮都重复提醒 —— 这是低电量预警最容易出问题的地方。
    /// </summary>
    public static bool ShouldNotify(int percent, bool charging, int threshold,
                                    int lastNotifiedPercent)
    {
        if (percent < 0) return false;
        if (charging) return false;
        if (percent > threshold) return false;
        return percent != lastNotifiedPercent;
    }
}
