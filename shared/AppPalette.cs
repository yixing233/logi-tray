using System;
using Microsoft.Win32;

namespace MouseBatteryTray;

/// <summary>与界面框架无关的 RGB 颜色。</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor FromRgb(byte r, byte g, byte b) => new(r, g, b);
}

/// <summary>
/// 两版共用的配色与主题判定。
///
/// 这里刻意不依赖任何界面框架类型（只返回普通 RGB 与 bool），
/// 因此 WPF 完整版与 WinForms 轻量版能共用同一套电量配色，
/// 不会出现两个版本颜色不一致的问题。
/// </summary>
public static class AppPalette
{
    /// <summary>Windows 充电蓝。</summary>
    public static readonly RgbColor Charging = new(0, 120, 212);

    /// <summary>离线中性灰。</summary>
    public static readonly RgbColor Offline = new(140, 140, 140);

    /// <summary>系统当前是否使用深色应用主题。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            return Registry.GetValue(key, "AppsUseLightTheme", 1) is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 按电量百分比取状态色：低电量红 → 中段琥珀 → 高电量绿，充电时用充电蓝。
    /// 分段线性插值，保证 0..100 之间颜色连续变化。
    /// </summary>
    public static RgbColor GetBatteryColor(int percent, bool charging)
    {
        if (charging) return Charging;
        if (percent <= 0) return Offline;

        double t = Math.Clamp(percent / 100.0, 0.0, 1.0);

        byte r, g, b;
        if (t >= 0.65)
        {
            double f = (t - 0.65) / 0.35;
            r = (byte)Math.Round(120 + f * (16 - 120));
            g = (byte)Math.Round(190 + f * (148 - 190));
            b = (byte)Math.Round(16 + f * (76 - 16));
        }
        else if (t >= 0.35)
        {
            double f = (t - 0.35) / 0.30;
            r = (byte)Math.Round(234 + f * (120 - 234));
            g = (byte)Math.Round(179 + f * (190 - 179));
            b = (byte)Math.Round(8 + f * (16 - 8));
        }
        else if (t >= 0.15)
        {
            double f = (t - 0.15) / 0.20;
            r = (byte)Math.Round(234 + f * (234 - 234));
            g = (byte)Math.Round(88 + f * (179 - 88));
            b = (byte)Math.Round(12 + f * (8 - 12));
        }
        else
        {
            double f = t / 0.15;
            r = (byte)Math.Round(220 + f * (234 - 220));
            g = (byte)Math.Round(38 + f * (88 - 38));
            b = (byte)Math.Round(38 + f * (12 - 38));
        }

        return new RgbColor(r, g, b);
    }
}
