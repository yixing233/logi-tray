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
    public string RemainingTimeText { get; set; } = "正在估算...";
    public double RatePerHour { get; set; }
    public string Confidence { get; set; } = "";
    public string LastUpdatedText { get; set; } = "刚刚";
    public int OnlineHoursCount { get; set; }
    public int MinPercent { get; set; } = -1;
    public int MaxPercent { get; set; } = -1;
    public List<HourlyBucket> HourlyBuckets { get; set; } = new();

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

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "logi-tray",
        "settings.json");

    /// <summary>配置文件（含旧版迁移路径）是否已存在。</summary>
    public static bool ConfigExists()
    {
        if (File.Exists(ConfigPath)) return true;

        string oldPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MouseBatteryTray",
            "settings.json");
        return File.Exists(oldPath);
    }

    public static SettingsConfig Load()
    {
        try
        {
            string path = ConfigPath;
            if (!File.Exists(path))
            {
                // 兼容旧路径无缝迁移
                string oldPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MouseBatteryTray",
                    "settings.json");
                if (File.Exists(oldPath)) path = oldPath;
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
