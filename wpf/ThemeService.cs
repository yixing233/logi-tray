using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MouseBatteryTray;

public enum AppTheme
{
    Light,
    Dark
}

public static class LucideIcons
{
    public const string Settings = "\ue154";
    public const string X = "\ue1b2";
    public const string Battery = "\ue053";
    public const string Circle = "\ue076";
    public const string Hash = "\ue0ef";
    public const string Monitor = "\ue11d";
    public const string Sun = "\ue178";
    public const string Moon = "\ue11e";
    public const string Zap = "\ue1b4";
    public const string Check = "\ue06c";

    /// <summary>圆圈感叹号，用于「关于」入口。</summary>
    public const string Info = "\ue077";

    /// <summary>左箭头，用于二级页面的返回按钮。</summary>
    public const string ArrowLeft = "\ue048";

    /// <summary>圆环刻度（旋转/刷新），用于「检查更新」。</summary>
    public const string Refresh = "\ue0ac";

    /// <summary>右上方箭头，用于打开外部链接。</summary>
    public const string ExternalLink = "\ue04d";
}

public static class ThemeService
{
    public static System.Windows.Media.FontFamily LucideFont { get; } = InitializeLucideFont();

    private static System.Windows.Media.FontFamily InitializeLucideFont()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string fontDir = System.IO.Path.Combine(baseDir, "Fonts");
            if (System.IO.File.Exists(System.IO.Path.Combine(fontDir, "lucide.ttf")))
            {
                var folderUri = new Uri(fontDir + System.IO.Path.DirectorySeparatorChar);
                return new System.Windows.Media.FontFamily(folderUri, "./#lucide");
            }
        }
        catch { }
        return new System.Windows.Media.FontFamily("pack://application:,,,/logi-tray;component/Fonts/#lucide");
    }

    public static string ConfiguredThemeMode { get; private set; } = "system"; // "system", "light", "dark"
    public static bool IsAcrylicEnabled { get; private set; } = true;

    public static event Action? ThemeChanged;

    public static void SetAcrylicEnabled(bool enabled)
    {
        if (IsAcrylicEnabled == enabled) return;
        IsAcrylicEnabled = enabled;
        ThemeChanged?.Invoke();
    }

    public static Brush SolidSurfaceBrush => CurrentTheme == AppTheme.Dark
        ? new SolidColorBrush(Color.FromRgb(24, 29, 38))
        : new SolidColorBrush(Color.FromRgb(252, 253, 255));

    static ThemeService()
    {
        try
        {
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (ConfiguredThemeMode == "system")
                {
                    ThemeChanged?.Invoke();
                }
            };
        }
        catch { }
    }

    public static AppTheme CurrentTheme
    {
        get
        {
            if (ConfiguredThemeMode == "dark") return AppTheme.Dark;
            if (ConfiguredThemeMode == "light") return AppTheme.Light;
            return IsSystemDark() ? AppTheme.Dark : AppTheme.Light;
        }
    }

    public static void SetThemeMode(string mode)
    {
        if (ConfiguredThemeMode == mode) return;
        ConfiguredThemeMode = mode;
        ThemeChanged?.Invoke();
    }

    public static bool IsSystemDark() => AppPalette.IsSystemDark();

    public static Brush BorderBrush => CurrentTheme == AppTheme.Dark
        ? new SolidColorBrush(Color.FromArgb(50, 255, 255, 255))
        : new SolidColorBrush(Color.FromArgb(160, 200, 208, 219));

    public static Brush SeparatorBrush => CurrentTheme == AppTheme.Dark
        ? new SolidColorBrush(Color.FromArgb(30, 255, 255, 255))
        : new SolidColorBrush(Color.FromArgb(20, 0, 0, 0));

    public static Brush TrackBackgroundBrush => CurrentTheme == AppTheme.Dark
        ? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
        : new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));

    // 高辨识度科技蓝：深色亮天青，浅色科技深蓝
    public static Brush TechBlueBrush => CurrentTheme == AppTheme.Dark
        ? new SolidColorBrush(Color.FromRgb(56, 189, 248))  // 亮天青 #38BDF8
        : new SolidColorBrush(Color.FromRgb(0, 120, 212));   // 经典科技深蓝 #0078D4

    public static Brush PrimaryTextBrush => CurrentTheme == AppTheme.Dark
        ? new SolidColorBrush(Color.FromRgb(248, 250, 252))
        : new SolidColorBrush(Color.FromRgb(15, 23, 42));    // 深岩板黑 #0F172A

    public static Brush SecondaryTextBrush => CurrentTheme == AppTheme.Dark
        ? new SolidColorBrush(Color.FromRgb(158, 172, 190))
        : new SolidColorBrush(Color.FromRgb(51, 65, 85));    // 墨青灰 #334155

    public static Brush FaintTextBrush => CurrentTheme == AppTheme.Dark
        ? new SolidColorBrush(Color.FromRgb(110, 126, 148))
        : new SolidColorBrush(Color.FromRgb(100, 116, 139)); // 钢灰蓝 #64748B

    /// <summary>
    /// 将当前深浅模式的全部画刷动态注入到指定的 ResourceDictionary，
    /// 驱动 XAML 中所有 {DynamicResource ...} 瞬间响应变色，永无黑上黑。
    /// </summary>
    public static void ApplyThemeResources(ResourceDictionary dict)
    {
        bool dark = CurrentTheme == AppTheme.Dark;

        dict["ThemeGroupHeader"] = dark
            ? new SolidColorBrush(Color.FromRgb(56, 189, 248))  // 亮天青 #38BDF8
            : new SolidColorBrush(Color.FromRgb(2, 132, 199));   // 醒目科技深蓝 #0284C7

        dict["ThemeTextPrimary"] = dark
            ? new SolidColorBrush(Color.FromRgb(248, 250, 252))
            : new SolidColorBrush(Color.FromRgb(15, 23, 42));

        dict["ThemeTextSecondary"] = dark
            ? new SolidColorBrush(Color.FromRgb(203, 213, 225))  // 浅银灰 #CBD5E1
            : new SolidColorBrush(Color.FromRgb(30, 41, 59));    // 扎实深板岩灰黑 #1E293B (12:1 极限对比度)

        dict["ThemeTextFaint"] = dark
            ? new SolidColorBrush(Color.FromRgb(148, 163, 184))  // 柔和银 #94A3B8
            : new SolidColorBrush(Color.FromRgb(51, 65, 85));    // 墨青灰 #334155 (9:1 高对比度，告别模糊淡灰)

        dict["ThemeCardBackground"] = dark
            ? new SolidColorBrush(Color.FromArgb(32, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(16, 0, 0, 0));

        dict["ThemeCardBorder"] = dark
            ? new SolidColorBrush(Color.FromArgb(38, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(24, 0, 0, 0));

        dict["ThemeButtonSecondaryBackground"] = dark
            ? new SolidColorBrush(Color.FromArgb(38, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(16, 0, 0, 0));

        dict["ThemeButtonSecondaryForeground"] = dark
            ? new SolidColorBrush(Color.FromRgb(248, 250, 252))
            : new SolidColorBrush(Color.FromRgb(15, 23, 42));

        dict["ThemeButtonSecondaryHoverBackground"] = dark
            ? new SolidColorBrush(Color.FromArgb(65, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(32, 0, 0, 0));

        dict["ThemeButtonSecondaryHoverForeground"] = dark
            ? new SolidColorBrush(Color.FromRgb(255, 255, 255))
            : new SolidColorBrush(Color.FromRgb(0, 0, 0));

        dict["ThemeSliderTrackBackground"] = dark
            ? new SolidColorBrush(Color.FromArgb(50, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(38, 0, 0, 0));

        dict["ThemeTogglePillBackground"] = dark
            ? new SolidColorBrush(Color.FromArgb(36, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(24, 0, 0, 0));

        dict["ThemeTogglePillBorder"] = dark
            ? new SolidColorBrush(Color.FromArgb(120, 255, 255, 255))
            : new SolidColorBrush(Color.FromRgb(112, 112, 112));

        dict["ThemeToggleKnobFill"] = dark
            ? new SolidColorBrush(Color.FromRgb(220, 225, 230))
            : new SolidColorBrush(Color.FromRgb(85, 85, 85));
    }

    public static Color GetBatteryColor(int percent, bool charging)
    {
        // 配色定义在共享层，保证 WPF 完整版与 WinForms 轻量版颜色一致
        var c = AppPalette.GetBatteryColor(percent, charging);
        return Color.FromRgb(c.R, c.G, c.B);
    }

    public static Brush GetBatteryBrush(int percent, bool charging)
    {
        var brush = new SolidColorBrush(GetBatteryColor(percent, charging));
        brush.Freeze();
        return brush;
    }

    public static void ApplyAcrylicBackdrop(Window window)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            bool dark = CurrentTheme == AppTheme.Dark;

            // 1. Windows 11 DWM rounded corners & dark mode preference
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int cornerPreference = (int)UnmanagedMethods.DwmWindowCornerPreference.DWMWCP_ROUND;
                UnmanagedMethods.DwmSetWindowAttribute(
                    handle,
                    UnmanagedMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                    ref cornerPreference,
                    sizeof(int));

                int backdropType = (int)UnmanagedMethods.DwmSystemBackdropType.DWMSBT_NONE;
                UnmanagedMethods.DwmSetWindowAttribute(
                    handle,
                    UnmanagedMethods.DWMWA_SYSTEMBACKDROP_TYPE,
                    ref backdropType,
                    sizeof(int));

                int darkMode = dark ? 1 : 0;
                UnmanagedMethods.DwmSetWindowAttribute(
                    handle,
                    UnmanagedMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                    ref darkMode,
                    sizeof(int));
            }

            // 2. SetWindowCompositionAttribute with ACCENT_ENABLE_ACRYLICBLURBEHIND (TinyBar's technique)
            var accent = new UnmanagedMethods.AccentPolicy();
            if (IsAcrylicEnabled)
            {
                int tintOpacity = dark ? 175 : 110;
                Color tintColor = dark
                    ? Color.FromArgb((byte)tintOpacity, 16, 20, 26)
                    : Color.FromArgb((byte)tintOpacity, 248, 250, 252);

                accent.AccentState = UnmanagedMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND;
                accent.AccentFlags = 0;
                accent.GradientColor = ToAccentColor(tintColor);
                accent.AnimationId = 0;
            }
            else
            {
                accent.AccentState = UnmanagedMethods.AccentState.ACCENT_DISABLED;
                accent.AccentFlags = 0;
                accent.GradientColor = 0;
                accent.AnimationId = 0;
            }

            int size = Marshal.SizeOf<UnmanagedMethods.AccentPolicy>();
            IntPtr data = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, data, false);
                var attribute = new UnmanagedMethods.WindowCompositionAttributeData
                {
                    Attribute = UnmanagedMethods.WindowCompositionAttribute.WCA_ACCENT_POLICY,
                    Data = data,
                    SizeOfData = size
                };
                UnmanagedMethods.SetWindowCompositionAttribute(handle, ref attribute);
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }
        catch
        {
            // Fall back
        }
    }

    private static int ToAccentColor(Color c)
    {
        return unchecked((int)((c.A << 24) | (c.B << 16) | (c.G << 8) | c.R));
    }
}
