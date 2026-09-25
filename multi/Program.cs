using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MouseBatteryTray;

namespace MultiTray;

/// <summary>
/// 多品牌键鼠耳机电量工具。
///
/// 与罗技专用版的关系：并存的第三个应用，复用同一套亚克力 Fluent 外观层
/// （shared-wpf/）。刻意不含续航预测与历史曲线 —— 只做电量显示与低电量预警。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppIdentity.Name = "multi-tray";
        AppIdentity.LegacyDirectoryName = null;
        AppIdentity.MutexNameOverride = "multi_tray_SingleInstance";

        if (args.Length > 0)
        {
            // 命令行模式下强制 UTF-8 输出。
            //
            // 只设 Console.OutputEncoding 不够：本项目是 WinExe（GUI 子系统），
            // 设置 OutputEncoding 会让 .NET 重建 stdout 写入器且拿不到有效句柄，
            // 结果是**输出被整个吞掉**（实测重定向时 0 字节）。
            // 因此用 SetOut 接管 stdout。详见 ConsoleSession。
            ConsoleSession.UseUtf8Output();

            switch (args[0])
            {
                case "--test-protocols":
                    return ProtocolTests.Run();
                case "--list":
                    return ListDevices();
                case "--diag-atk":
                case "--probe-atk":
                case "--probe-atk-web":
                case "--probe-z87":
                case "--probe-proto1":
                case "--probe-transport":
                    return AtkDiagnostics.Run();
                case "--diag-mchose":
                    return MchoseDiagnostics.Run(args.Length > 1 && args[1] == "watch");
                case "--show-details":
                    return ShowDetailsOnce();
                case "--show-settings":
                    return ShowSettingsOnce();
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
            }
        }

        // 单实例：多开会导致托盘出现两个图标、通知重复
        using var mutex = new Mutex(true, AppIdentity.MutexName, out bool created);
        if (!created) return 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var settings = MultiSettings.Load();
        ThemeService.SetThemeMode(settings.ThemeMode);
        ThemeService.SetAcrylicEnabled(settings.AcrylicEnabled);

        var context = new TrayContext(settings, app);

        app.Startup += (_, _) =>
        {
            context.Start();
        };

        app.Run();
        context.Dispose();
        return 0;
    }

    private static int ShowDetailsOnce()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var settings = MultiSettings.Load();
        ThemeService.SetThemeMode(settings.ThemeMode);
        ThemeService.SetAcrylicEnabled(settings.AcrylicEnabled);

        var readings = DeviceReader.ReadAll(settings);
        var win = new DeviceCardWindow(readings, settings, () => { });
        win.Closed += (_, _) => app.Shutdown();
        app.Startup += (_, _) =>
        {
            // 没有托盘点击锚点，用当前光标位置（离通知区域很近）
            UnmanagedMethods.GetCursorPos(out var pt);
            win.ToggleNear(pt.X, pt.Y);
        };
        app.Run();
        return 0;
    }

    private static int ShowSettingsOnce()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var settings = MultiSettings.Load();
        ThemeService.SetThemeMode(settings.ThemeMode);
        ThemeService.SetAcrylicEnabled(settings.AcrylicEnabled);

        var win = new MultiSettingsWindow(settings);
        win.Closed += (_, _) => app.Shutdown();
        app.Startup += (_, _) => win.Show();
        app.Run();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("multi-tray —— 多品牌键鼠耳机电量托盘工具");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  multi-tray.exe                启动托盘程序");
        Console.WriteLine("  multi-tray.exe --list         列出检测到的设备与当前电量后退出");
        Console.WriteLine("  multi-tray.exe --diag-atk     诊断 ATK 设备（打印原始收发字节）");
        Console.WriteLine("  multi-tray.exe --diag-mchose  诊断迈从设备（打印原始收发字节）");
        Console.WriteLine("  multi-tray.exe --diag-mchose watch  持续采样，用于确认充电状态字节");
        Console.WriteLine("  multi-tray.exe --test-protocols  运行协议解析层自检（无需硬件）");
        Console.WriteLine("  multi-tray.exe --show-details    直接打开设备电量窗口");
        Console.WriteLine("  multi-tray.exe --show-settings   直接打开设置窗口");
        Console.WriteLine("  multi-tray.exe --help         显示本帮助");
    }

    /// <summary>列出设备与电量，便于用户在无界面情况下确认哪台设备能读到。</summary>
    private static int ListDevices()
    {
        var settings = MultiSettings.Load();
        var readings = DeviceReader.ReadAll(settings);

        Console.WriteLine();
        Console.WriteLine("检测到的设备");
        Console.WriteLine(new string('=', 66));

        if (readings.Count == 0)
        {
            Console.WriteLine("未检测到任何受支持的设备。");
            Console.WriteLine();
            Console.WriteLine("支持范围:");
            Console.WriteLine("  * 罗技（走 HID++ 2.0，需要接收器或蓝牙连接）");
            Console.WriteLine("  * 迈从 MCHOSE（VID 291D:385D）");
            Console.WriteLine("  * ATK / VXE / VGN（VID 373B / 3554）");
            return 2;
        }

        foreach (var r in readings)
        {
            string pct = r.Percent >= 0 ? $"{r.Percent,3}%" : "  --";
            string state = r.IsOnline ? r.StatusText : "已休眠/离线";
            Console.WriteLine($"  {pct}  {r.Name}");
            Console.WriteLine($"        {KindText(r.Kind)} · {state} · 来源 {r.Source}");
        }

        Console.WriteLine();
        int readable = readings.Count(r => r.Percent >= 0);
        Console.WriteLine($"共 {readings.Count} 台设备，其中 {readable} 台读到电量。");
        if (readable == 0)
        {
            Console.WriteLine();
            Console.WriteLine("均未读到电量。常见原因:");
            Console.WriteLine("  * 无线设备处于休眠，动一下设备或按键唤醒后重试");
            Console.WriteLine("  * 耳机/键盘未通过其无线接收器连接");
        }
        return readable > 0 ? 0 : 2;
    }

    private static string KindText(DeviceKind k) => k switch
    {
        DeviceKind.Mouse => "鼠标",
        DeviceKind.Keyboard => "键盘",
        DeviceKind.Headset => "耳机",
        _ => "设备",
    };
}

/// <summary>
/// 托盘运行上下文：定时轮询、刷新图标、触发低电量通知、承载菜单。
///
/// 托盘图标本身仍用 WinForms 的 NotifyIcon（WPF 没有等价物），
/// 但所有窗口都是 WPF 亚克力窗口。
/// </summary>
internal sealed class TrayContext : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private readonly DispatcherTimer _timer;
    private readonly MultiSettings _settings;
    private readonly Application _app;

    private List<DeviceReading> _readings = new();
    private System.Drawing.Icon? _currentIcon;
    private bool _dark;
    private DeviceCardWindow? _card;
    private MultiSettingsWindow? _settingsWindow;

    /// <summary>各设备上次已通知的电量，用于避免同一电量反复提醒。</summary>
    private readonly Dictionary<string, int> _lastNotified = new();

    /// <summary>
    /// 托盘图标被按下那一刻的光标位置（物理像素），作为卡片锚点。
    ///
    /// 为什么在 MouseDown 时就记下来：右键菜单弹出时会为避开屏幕边缘而位移，
    /// 那时再取光标得到的是**菜单项**坐标，卡片会被锚到错误位置。
    /// 完整版同样在按下与菜单 Opening 两处记录。
    /// </summary>
    private int _anchorX;
    private int _anchorY;
    private bool _hasAnchor;

    public TrayContext(MultiSettings settings, Application app)
    {
        _settings = settings;
        _app = app;
        _dark = ResolveDark();

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.MouseDown += (_, _) => CaptureAnchor();
        _tray.MouseClick += OnTrayClick;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(5, _settings.Interval)),
        };
        _timer.Tick += (_, _) => Refresh();
    }

    /// <summary>记录托盘图标的锚点（此刻菜单尚未弹出，光标就在图标上）。</summary>
    private void CaptureAnchor()
    {
        if (UnmanagedMethods.GetCursorPos(out var pt))
        {
            _anchorX = pt.X;
            _anchorY = pt.Y;
            _hasAnchor = true;
        }
    }

    /// <summary>锚点 X；没有有效锚点时退回当前光标。</summary>
    private int AnchorX
    {
        get
        {
            if (_hasAnchor) return _anchorX;
            UnmanagedMethods.GetCursorPos(out var pt);
            return pt.X;
        }
    }

    /// <summary>锚点 Y；没有有效锚点时退回当前光标。</summary>
    private int AnchorY
    {
        get
        {
            if (_hasAnchor) return _anchorY;
            UnmanagedMethods.GetCursorPos(out var pt);
            return pt.Y;
        }
    }

    /// <summary>在 WPF 消息循环启动后开始轮询。</summary>
    public void Start()
    {
        Refresh();
        _timer.Start();
    }

    private bool ResolveDark() => _settings.ThemeMode switch
    {
        "dark" => true,
        "light" => false,
        _ => AppPalette.IsSystemDark(),
    };

    private System.Windows.Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip { ShowImageMargin = false };
        // 菜单弹出前再记一次锚点，避免按下与弹出之间有鼠标移动
        menu.Opening += (_, _) => CaptureAnchor();

        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
            "查看设备", null, (_, _) => ShowDetails()));
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
            "立即刷新", null, (_, _) => Refresh()));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
            "设置", null, (_, _) => ShowSettings()));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
            "退出", null, (_, _) => ExitApp()));

        return menu;
    }

    private void Refresh()
    {
        try
        {
            _readings = DeviceReader.ReadAll(_settings);
            UpdateIcon();
            UpdateTooltip();
            CheckNotifications();

            if (_card != null && _card.IsVisible)
            {
                _card.UpdateData(_readings);
            }
        }
        catch
        {
            // 单次刷新失败不应让托盘消失
        }
    }

    private void UpdateIcon()
    {
        var icon = TrayIconRenderer.Render(_readings, _dark);

        // 先赋值再销毁旧图标：顺序反了会出现瞬间无图标
        var previous = _currentIcon;
        _currentIcon = icon;
        _tray.Icon = icon;
        previous?.Dispose();
    }

    private void UpdateTooltip()
    {
        var online = _readings.Where(r => r.IsOnline && r.Percent >= 0)
                              .OrderBy(r => r.Percent)
                              .ToList();

        string text;
        if (online.Count == 0)
        {
            text = _readings.Count > 0
                ? "multi-tray · 设备均未读到电量"
                : "multi-tray · 未检测到设备";
        }
        else
        {
            var lines = new List<string> { "multi-tray" };
            foreach (var r in online)
            {
                lines.Add($"{r.Name}  {r.Percent}%  {r.StatusText}");
            }
            // 托盘提示上限约 127 字符，超出会被截断
            text = string.Join("\n", lines);
            if (text.Length > 120) text = text.Substring(0, 120);
        }

        _tray.Text = text;
    }

    /// <summary>
    /// 低电量预警。
    ///
    /// 只在电量与上次已通知的值不同、且未充电时才提醒，
    /// 避免同一个电量每轮都弹一次（低电量提醒最常见的骚扰来源）。
    /// </summary>
    private void CheckNotifications()
    {
        if (!_settings.NotifyEnabled) return;

        foreach (var r in _readings)
        {
            if (!r.IsOnline || r.Percent < 0 || r.IsCharging) continue;

            int threshold = r.Percent <= _settings.CriticalThreshold
                ? _settings.CriticalThreshold
                : r.Percent <= _settings.LowThreshold
                    ? _settings.LowThreshold
                    : -1;
            if (threshold < 0) continue;

            _lastNotified.TryGetValue(r.Key, out int last);
            if (!Protocols.ShouldNotify(r.Percent, r.IsCharging, threshold, last))
                continue;

            bool critical = threshold == _settings.CriticalThreshold;
            _tray.BalloonTipTitle = critical ? "电量严重不足" : "电量偏低";
            _tray.BalloonTipText = critical
                ? $"{r.Name} 仅剩 {r.Percent}%，请尽快充电。"
                : $"{r.Name} 剩余 {r.Percent}%，建议充电。";
            _tray.BalloonTipIcon = critical
                ? System.Windows.Forms.ToolTipIcon.Warning
                : System.Windows.Forms.ToolTipIcon.Info;
            _tray.ShowBalloonTip(6000);

            _lastNotified[r.Key] = r.Percent;
        }

        // 已离线或已换设备的记录清掉，下次低电量能重新提醒
        var keys = _readings.Where(x => x.IsOnline).Select(x => x.Key).ToHashSet();
        foreach (var stale in _lastNotified.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _lastNotified.Remove(stale);
        }
    }

    private void OnTrayClick(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button == System.Windows.Forms.MouseButtons.Left)
        {
            ShowDetails();
        }
    }

    /// <summary>
    /// 显示或收起设备卡片。
    ///
    /// 复用同一个窗口实例（收起走 Hide，不 Close）：完整版就是这么做的。
    /// 早先每次点击都 new 一个窗口，导致每次都重建整棵视觉树，
    /// 表现为「窗口很大、每次都在同一位置重新弹出」（用户反馈过）。
    /// </summary>
    private void ShowDetails()
    {
        int ax = AnchorX, ay = AnchorY;

        if (_card == null || !_card.IsLoaded)
        {
            _card = new DeviceCardWindow(_readings, _settings, ShowSettings);
            _card.Closed += (_, _) => _card = null;
        }

        // 先刷新数据，再按锚点切换显示位置
        _card.UpdateData(_readings);
        _card.ToggleNear(ax, ay);
    }

    private void ShowSettings()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new MultiSettingsWindow(_settings);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                // 配置已由设置窗口自行落盘，这里只同步运行期状态
                _dark = ResolveDark();
                ThemeService.SetThemeMode(_settings.ThemeMode);
                ThemeService.SetAcrylicEnabled(_settings.AcrylicEnabled);
                _timer.Interval = TimeSpan.FromSeconds(Math.Max(5, _settings.Interval));
                Refresh();
            };
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }
        else
        {
            _settingsWindow.Activate();
        }
    }

    private void ExitApp()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _currentIcon?.Dispose();
        _settingsWindow?.Close();
        _card?.Close();
        _app.Shutdown();
    }

    public void Dispose()
    {
        _timer.Stop();
        _tray.Dispose();
        _currentIcon?.Dispose();
    }
}
