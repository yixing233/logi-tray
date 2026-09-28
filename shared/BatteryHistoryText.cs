using System;

namespace MouseBatteryTray;

/// <summary>
/// 「休眠前最后一次电量」小字的文案与判定。
///
/// 为什么单独抽成一个文件：完整版、轻量版、多品牌版三个版本都要说同一句话。
/// 文案一旦各写一份，改一处忘一处，三个版本对同一件事的说法就会漂移
/// （时间档位、标点、间隔符全会慢慢分叉）。这里只放纯字符串换算与纯布尔判定，
/// 不碰任何文件、不碰任何 UI 类型 —— 因此 wpf（WPF）、lite（WinForms）、
/// multi（两者都开）都能原样编译，也便于用合成数据离线断言。
///
/// 与 multi 的关系：multi 的 <c>BatteryHistory</c> 仍然保留同名方法，
/// 但那两个方法现在只是转发到这里，保证三版输出必然一致。
/// </summary>
public static class BatteryHistoryText
{
    /// <summary>
    /// 记忆的有效期。超过这个天数的「上次电量」不再显示：
    /// 一只半个月没插过电的鼠标，报出它的睡前电量对用户没有意义，
    /// 反而像在说「现在还有这么多电」。
    /// </summary>
    public const int MaxAgeDays = 30;

    /// <summary>把「上次电量」渲染成卡片上的小字，例如「上次 77% · 2 小时前」。</summary>
    public static string Describe(int percent, DateTime? at, DateTime now)
        => at.HasValue
            ? $"上次 {percent}% · {FormatAge(at.Value, now)}"
            : $"上次 {percent}%";

    /// <summary>相对时长，例如「刚刚」「5 分钟前」「3 小时前」「2 天前」。</summary>
    public static string FormatAge(DateTime at, DateTime now)
    {
        // 时钟回拨（夏令时、手动校时、NTP 校正）会让差值变成负数，
        // 直接取整会得到「-1 分钟前」这种荒谬文案，这里夹到 0 就是「刚刚」。
        TimeSpan d = now - at;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalMinutes < 1) return "刚刚";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} 分钟前";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} 小时前";
        return $"{(int)d.TotalDays} 天前";
    }

    /// <summary>该时间点是否还够新，值得当作「休眠前最后一次电量」报出来。</summary>
    public static bool IsFresh(DateTime at, DateTime now)
        => now - at <= TimeSpan.FromDays(MaxAgeDays);

    /// <summary>
    /// 是否该显示「休眠前最后一次电量」小字。
    ///
    /// 三个条件缺一不可：
    ///   * <paramref name="percent"/> 小于 0 —— 本轮确实没读到数。
    ///     读到了就说明有实时值，历史小字没有存在的理由；
    ///   * <paramref name="lastKnownPercent"/> 有值 —— 从没记过就无话可说，
    ///     绝不能编一个数字出来；
    ///   * 时间还在 <see cref="MaxAgeDays"/> 有效期之内 —— 半个月前的读数
    ///     报出来只会让人以为「现在还有这么多电」。
    ///
    /// 判定只看「本轮有没有读数」，**刻意不要求「当前被判定为离线」**：
    /// 完整版/轻量版的 StaleGrace 抖动容忍窗口内，设备刚睡着的那 5 分钟里
    /// <c>IsConnected</c> 仍是 true 而 <c>Percent</c> 已经是 -1。
    /// 若拿 IsConnected 当条件，这段窗口里卡片会显示「--%」却不给任何解释 ——
    /// 而「刚睡着」正是用户最想看睡前电量的时刻。
    ///
    /// 收成纯原始类型是为了能被 <c>multishot</c> 直接断言：
    /// 测试宿主只引用多品牌版，拿不到 <see cref="BatterySnapshot"/>。
    /// </summary>
    public static bool HasLastKnown(int percent, int lastKnownPercent, DateTime? lastKnownAt)
        => percent < 0
            && lastKnownPercent >= 0
            && lastKnownAt.HasValue
            && IsFresh(lastKnownAt.Value, DateTime.Now);

    /// <summary>
    /// 读不到读数时第二行该显示什么。无话可说时返回<b>空串</b> —— 绝不能编。
    ///
    /// 单独抽出来是为了让「显示哪一行」这个判断本身也能被离线断言，
    /// 而不只是文案；完整版与轻量版都直接调它，省得两边各写一次三目运算。
    /// 这里返回空串而不是「电量等级 · 已休眠」这类兜底：设备都没应答了，
    /// 再报一个「电量等级」只会让人以为那是刚读到的状态。
    /// </summary>
    public static string OfflineSubtitle(int percent, int lastKnownPercent,
                                         DateTime? lastKnownAt, DateTime now)
        => HasLastKnown(percent, lastKnownPercent, lastKnownAt)
            ? Describe(lastKnownPercent, lastKnownAt, now)
            : "";
}
