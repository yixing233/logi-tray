using System;
using System.Collections.Generic;

namespace MultiTray;

/// <summary>
/// 各品牌电量协议的**纯解析层**：只做字节到电量/状态的换算，不碰任何 I/O。
///
/// 这样拆分的原因是本机无法用真机验证任何一条厂商协议（三台设备对私有帧
/// 都不应答，详见 README 的记录）。纯函数可以脱离硬件，用合成帧做穷尽的
/// 单元测试，至少保证「解析逻辑本身是对的」，而不是让整条链路都处于
/// 「没测过」的状态。
///
/// 协议来源（均为公开实现，非猜测）：
///   * 迈从 MCHOSE V9 Pro —— rafagfran/mchose-v9-pro-battery-tray
///       请求 64 字节 [0]=0x55 [1]=0x65 [2]=0x01
///       响应 [0]=0x55 [1]=0x65 [2]=电量% [3]=状态码
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

    /// <summary>把迈从的状态码转成文案。1/2 为充电，3 为充满，其余按放电处理。</summary>
    public static (string text, bool charging) MchoseStatusText(byte status) => status switch
    {
        1 => ("充电中", true),
        2 => ("充电中", true),
        3 => ("已充满", true),
        _ => ("放电中", false),
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
