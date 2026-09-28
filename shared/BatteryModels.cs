using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseBatteryTray;

public record HourlyBucket(string HourText, int Percent, int Count, bool IsSleeping = false);

public class BatterySnapshot
{
    public string DeviceName { get; set; } = "罗技设备";
    public int Percent { get; set; } = -1;
    public bool IsCharging { get; set; }
    public bool IsConnected { get; set; } = true;
    public string LevelText { get; set; } = "良好";

    /// <summary>
    /// 原生读取器给出的充放电状态原文（「放电中」「充电中」「已充满」…）。
    ///
    /// 与 <see cref="LevelText"/> 是两件事：LevelText 是电量档位（「良好」「充足」），
    /// 这个是「在充电还是在放电」。托盘悬停提示要的是后者 —— 档位属于卡片第二行。
    ///
    /// 早先这个值在 <c>BatteryService</c> 里解析出来后就被丢掉了
    /// （传进 BuildSnapshotFromHistory 的 statusText 形参从未被使用），
    /// 于是提示里只能拿 RemainingTimeText 凑数，显示成「（预计剩余使用 8 小时）」
    /// 这种与状态无关的句子，与多品牌版的「放电中」对不上。
    /// </summary>
    public string StatusText { get; set; } = "";

    public string RemainingTimeText { get; set; } = "正在估算...";
    public double RatePerHour { get; set; }
    public string Confidence { get; set; } = "";
    public string LastUpdatedText { get; set; } = "刚刚";
    public int OnlineHoursCount { get; set; }
    public int MinPercent { get; set; } = -1;
    public int MaxPercent { get; set; } = -1;
    public List<HourlyBucket> HourlyBuckets { get; set; } = new();

    /// <summary>
    /// 休眠前最后一次读到的电量，仅用于卡片/托盘的小字。
    ///
    /// 刻意与 <see cref="Percent"/> 分开：<see cref="Percent"/> 是**当前**读数，
    /// 读不到时必须是 -1（界面显示「--%」）。早先的实现把历史值直接写进
    /// Percent，结果离线时大号数字显示的是几个小时前的旧电量，看上去像
    /// 「现在还有 77%」—— 用户要的是小字，不是把当前值篡改掉。
    /// </summary>
    public int LastKnownPercent { get; set; } = -1;

    /// <summary>上面那个读数的**本地**时间；无记忆时为 null。</summary>
    public DateTime? LastKnownAt { get; set; }

    /// <summary>本轮读不到数、但有旧电量可显示时，是否该出「休眠前最后一次电量」小字。</summary>
    public bool HasLastKnown =>
        BatteryHistoryText.HasLastKnown(Percent, LastKnownPercent, LastKnownAt);

    /// <summary>
    /// 托盘悬停提示的那一行，形如 <c>PRO X Wireless  73%  放电中</c>。
    ///
    /// 放在模型上而不是各版自己拼：完整版与轻量版的这一段代码原先逐字重复，
    /// 且第三段取的是续航预测（「预计剩余使用 8 小时」），与多品牌版的状态词
    /// 量纲不同 —— 用户正是拿这一点来抱怨「tooltip 需要统一」的。
    /// 现在三版都走 <see cref="BatteryStatusText"/>，包含行格式在内只有一份实现。
    ///
    /// 「有没有读到数」以 <see cref="Percent"/> 为准，不用
    /// <see cref="IsConnected"/>：StaleGrace 容忍窗口内设备刚睡着时
    /// Percent 已回到 -1 而 IsConnected 仍为 true，那 5 分钟里正该说
    /// 「已休眠 + 睡前电量」，否则提示会声称设备正常却在显示 `--%`。
    /// </summary>
    public string TooltipLine =>
        BatteryStatusText.DeviceLine(DeviceName, Percent, Percent >= 0,
                                     chargeStateKnown: true, IsCharging, StatusText);

    public static BatterySnapshot Offline(string deviceName = "罗技设备") => new()
    {
        DeviceName = deviceName,
        Percent = -1,
        IsConnected = false,
        RemainingTimeText = "设备离线或休眠",
        LastUpdatedText = DateTime.Now.ToString("HH:mm")
    };
}

public class SettingsConfig
{
    [JsonPropertyName("interval")]
    public int Interval { get; set; } = 30;

    [JsonPropertyName("low_threshold")]
    public int LowThreshold { get; set; } = 20;

    [JsonPropertyName("critical_threshold")]
    public int CriticalThreshold { get; set; } = 10;

    [JsonPropertyName("notify_enabled")]
    public bool NotifyEnabled { get; set; } = true;

    [JsonPropertyName("autostart")]
    public bool Autostart { get; set; } = false;

    [JsonPropertyName("stale_grace")]
    public int StaleGrace { get; set; } = 300;

    [JsonPropertyName("tray_icon_style")]
    public string TrayIconStyle { get; set; } = "battery"; // "battery", "ring", "number"

    [JsonPropertyName("theme_mode")]
    public string ThemeMode { get; set; } = "system"; // "system", "light", "dark"

    [JsonPropertyName("acrylic_enabled")]
    public bool AcrylicEnabled { get; set; } = true;

    public static string ConfigPath => Path.Combine(AppIdentity.DataDirectory, "settings.json");

    /// <summary>配置文件（含旧版迁移路径）是否已存在。</summary>
    public static bool ConfigExists()
    {
        if (File.Exists(ConfigPath)) return true;

        string? legacy = AppIdentity.LegacyDirectory;
        return legacy != null && File.Exists(Path.Combine(legacy, "settings.json"));
    }

    public static SettingsConfig Load()
    {
        try
        {
            string path = ConfigPath;
            if (!File.Exists(path))
            {
                // 兼容旧路径无缝迁移
                string? legacy = AppIdentity.LegacyDirectory;
                string? oldPath = legacy == null ? null : Path.Combine(legacy, "settings.json");
                if (oldPath != null && File.Exists(oldPath)) path = oldPath;
            }

            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<SettingsConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch { }
        return new SettingsConfig();
    }

    public void Save()
    {
        try
        {
            string path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var opt = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(this, opt);
            File.WriteAllText(path, json);
        }
        catch { }
    }
}
