using System;
using System.Collections.Generic;
using MouseBatteryTray;

namespace MultiTray;

/// <summary>设备类别，仅用于界面分组与图标选择。</summary>
public enum DeviceKind
{
    Mouse,
    Keyboard,
    Headset,
    Other,
}

/// <summary>一次电量读取的结果。</summary>
public sealed class DeviceReading
{
    /// <summary>设备显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>电量百分比 0..100；-1 表示未读到。</summary>
    public int Percent { get; set; } = -1;

    public DeviceKind Kind { get; set; } = DeviceKind.Other;

    /// <summary>是否正在充电。</summary>
    public bool IsCharging { get; set; }

    /// <summary>
    /// 充电状态是否**已知**。默认 true（读到了可信的状态位）。
    ///
    /// 迈从耳机置为 false：它的状态字节（响应 [3]）尚未校准 ——
    /// 放电时实测恒为 0x02，既不能断定表示充电，也不能断定表示放电。
    /// 此时界面不应声称「正在充电」，也不应反过来声称「正常放电中」，
    /// 而要如实说「充电状态未知」。
    /// </summary>
    public bool ChargeStateKnown { get; set; } = true;

    /// <summary>设备是否在线（已连接且能应答）。</summary>
    public bool IsOnline { get; set; }

    /// <summary>状态文案，例如「放电中」「充电中」。</summary>
    public string StatusText { get; set; } = "";

    /// <summary>是否休眠/离线（与 IsOnline 互为补充，供界面直接显示）。</summary>
    public bool IsSleeping => !IsOnline;

    /// <summary>数据来源说明，例如「HID++」「迈从 55 65」「ATK 报告」。用于排查。</summary>
    public string Source { get; set; } = "";

    /// <summary>设备标识（接口路径或槽位），用于稳定排序与去重。</summary>
    public string Key { get; set; } = "";

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public static DeviceReading Offline(string name, DeviceKind kind, string source)
        => new()
        {
            Name = name,
            Percent = -1,
            Kind = kind,
            IsOnline = false,
            StatusText = "已休眠",
            Source = source,
        };
}

/// <summary>
/// 多设备版设置。刻意**不含**续航预测相关字段 —— 本工具只做电量显示与低电量预警。
/// </summary>
public sealed class MultiSettings
{
    [System.Text.Json.Serialization.JsonPropertyName("interval")]
    public int Interval { get; set; } = 30;

    [System.Text.Json.Serialization.JsonPropertyName("low_threshold")]
    public int LowThreshold { get; set; } = 20;

    [System.Text.Json.Serialization.JsonPropertyName("critical_threshold")]
    public int CriticalThreshold { get; set; } = 10;

    [System.Text.Json.Serialization.JsonPropertyName("notify_enabled")]
    public bool NotifyEnabled { get; set; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("autostart")]
    public bool Autostart { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("theme_mode")]
    public string ThemeMode { get; set; } = "system";

    [System.Text.Json.Serialization.JsonPropertyName("acrylic_enabled")]
    public bool AcrylicEnabled { get; set; } = true;

    /// <summary>
    /// 关闭某个来源，便于排查（例如某型号协议不兼容时不必反复重试）。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("enabled_sources")]
    public List<string> EnabledSources { get; set; } = new() { "logitech", "mchose", "atk" };

    public static string ConfigPath =>
        System.IO.Path.Combine(AppIdentity.DataDirectory, "settings.json");

    public static MultiSettings Load()
    {
        try
        {
            string path = ConfigPath;
            if (System.IO.File.Exists(path))
            {
                string json = System.IO.File.ReadAllText(path);
                var cfg = System.Text.Json.JsonSerializer
                    .Deserialize<MultiSettings>(json);
                if (cfg != null)
                {
                    if (cfg.EnabledSources == null || cfg.EnabledSources.Count == 0)
                    {
                        cfg.EnabledSources = new List<string> { "logitech", "mchose", "atk" };
                    }
                    return cfg;
                }
            }
        }
        catch { }
        return new MultiSettings();
    }

    public void Save()
    {
        try
        {
            string path = ConfigPath;
            System.IO.Directory.CreateDirectory(
                System.IO.Path.GetDirectoryName(path)!);
            var opt = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            string json = System.Text.Json.JsonSerializer.Serialize(this, opt);
            // 不写 BOM：带 BOM 会让 JSON 反序列化失败
            System.IO.File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
        }
        catch { }
    }
}
