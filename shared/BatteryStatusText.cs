using System;

namespace MouseBatteryTray;

/// <summary>
/// 托盘悬停提示里那个「充放电状态」词的唯一来源。
///
/// 为什么需要它：多品牌版的托盘提示里，同一行位置上曾经混着两种含义的东西 ——
/// 罗技那一栏是原生给的充放电状态（「放电中」），而 ATK 那几栏塞进去的却是
/// 电量档位（「良好」）。三行并排时，用户读到的是「良好 / 放电中 / 放电中」，
/// 看上去像同一列数据却量纲不同，完全没法横向比较。档位属于卡片第二行
/// （「电量等级 · 良好」），提示里要的只有「在充电还是在放电」这一件事。
///
/// 因此这里把状态词收敛成一套固定说法，三个版本共用：
/// 完整版/轻量版从 <c>BatterySnapshot</c> 取（充放电标志 + 原生文案），
/// 多品牌版从 <c>DeviceReading</c> 取。谁都不许再自己拼字符串。
///
/// 只放纯字符串判定，不碰任何文件与 UI 类型 —— wpf（WPF）、lite（WinForms）、
/// multi（两者都开）都能原样编译，也便于用合成数据离线断言。
/// </summary>
public static class BatteryStatusText
{
    /// <summary>插着线、电量在涨。</summary>
    public const string Charging = "充电中";

    /// <summary>没插线，电量在掉。</summary>
    public const string Discharging = "放电中";

    /// <summary>
    /// 设备在线，但它的状态字节还没校准，无法断定在充还是在放。
    /// 迈从耳机就是这种情况：放电时状态字节实测恒为 0x02，
    /// 既不能断言充电、也不能断言放电，如实说「未知」。
    /// </summary>
    public const string ChargeUnknown = "充电状态未知";

    /// <summary>本轮根本没读到设备。</summary>
    public const string Sleeping = "已休眠";

    /// <summary>
    /// 状态词的优先级：离线 → 未知 → 原生文案（若它确实是状态而不是档位）→ 充放电标志。
    /// </summary>
    /// <param name="online">本设备这一轮是否读到了。</param>
    /// <param name="chargeStateKnown">充电状态是否已知（迈从未校准时为 false）。</param>
    /// <param name="charging">解析出的充放电标志，仅在拿不到原生文案时兜底。</param>
    /// <param name="detail">
    /// 上游给的原始文案，可空。是状态词（「已充满」「充电中（慢充）」「充电异常」）就原样采用 ——
    /// 这些信息比一个笼统的「充电中」更值钱，丢掉反而更差；
    /// 是档位词（「良好」「充足」）则视为无效，退回充放电标志。
    /// </param>
    public static string For(bool online, bool chargeStateKnown, bool charging, string? detail = null)
    {
        if (!online)
        {
            return Sleeping;
        }

        if (!chargeStateKnown)
        {
            return ChargeUnknown;
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            string text = detail.Trim();

            // 原生在状态位无法识别时会给出「未知 (0x12)」这类带十六进制的调试串。
            // 那对用户是天书，收敛成「充电状态未知」。
            if (text.StartsWith("未知", StringComparison.Ordinal))
            {
                return ChargeUnknown;
            }

            if (IsChargeWord(text))
            {
                return text;
            }
        }

        return charging ? Charging : Discharging;
    }

    /// <summary>
    /// 一台设备在托盘提示里的那一行：<c>名称  电量%  状态</c>。
    ///
    /// 三个版本的提示行必须逐字一致，否则「统一」只统一了词汇，格式又各走各的。
    /// 完整版/轻量版是单设备，直接就是这一行；多品牌版是「标题 + 每台一行」，
    /// 每行同样走这里。
    ///
    /// 读不到读数时电量位置显示 <c>--%</c>，与卡片大号数字同一套写法 ——
    /// 不能省掉，否则「没读到」看起来像是这一栏本来就没有。
    /// </summary>
    public static string DeviceLine(string name, int percent, bool online,
                                    bool chargeStateKnown, bool charging,
                                    string? detail = null)
    {
        string pct = percent >= 0 ? $"{percent}%" : "--%";
        return $"{name}  {pct}  {For(online, chargeStateKnown, charging, detail)}";
    }

    /// <summary>
    /// 把原生读取器某一行「冒号之后」的文字，解析成状态词、充放电标志与电量档位。
    ///
    /// 为什么放在这里而不是留在 <c>BatteryService</c> 的循环里：这段判定有严格的
    /// 先后顺序，而顺序错了不会报错、只会安静地说错话 ——
    /// 「已充满」里并不含「充电」这个连续子串，先判「充电」就会把插着线的设备
    /// 说成「放电中」；原生状态位无法识别时打印的是「未知 (0x12)」，
    /// 漏掉这个分支，状态词就停在初值「放电中」，设备明说「我不知道」，
    /// 界面却替它宣称在放电。抽成纯函数后，这些顺序都能用合成字符串离线断言
    /// （见 multishot 的 CheckStatusText），不必依赖真机。
    /// </summary>
    /// <param name="tail">原生行里冒号之后的部分，通常形如「73% · 放电中 · 良好」。</param>
    /// <param name="levelFallback">没识别到档位词时用的档位。</param>
    public static (string StatusText, bool IsCharging, string LevelText) FromNativeTail(
        string tail, string levelFallback = "良好")
    {
        if (tail.Contains("充满"))
        {
            return ("已充满", false, "满");
        }

        if (tail.Contains("充电"))
        {
            return ("充电中", true, levelFallback);
        }

        // 原生状态位无法识别时给的是「未知 (0x12)」这类调试串。
        // 原文照收，显示层会由 For 收敛成「充电状态未知」。
        if (tail.Contains("未知"))
        {
            return ("未知", false, levelFallback);
        }

        if (tail.Contains("满"))
        {
            return ("放电中", false, "满");
        }

        if (tail.Contains("良好"))
        {
            return ("放电中", false, "良好");
        }

        return ("放电中", false, levelFallback);
    }

    /// <summary>
    /// 这段文案是在说「充放电」，而不是在说「电量档位」吗。
    ///
    /// 只能按前缀判断，不能用 <c>Contains("充")</c>：档位词里的「充足」
    /// （<c>Protocols.LevelText</c> 对 81~100 的取值）也含「充」，
    /// 那样它会被当成状态词原样显示出来 —— 正是本类要消灭的那种混用。
    /// </summary>
    public static bool IsChargeWord(string text)
        => text.StartsWith("充电", StringComparison.Ordinal)
        || text.StartsWith("放电", StringComparison.Ordinal)
        || text.StartsWith("已充满", StringComparison.Ordinal);
}
