using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using MouseBatteryTray;

namespace MultiTray;

/// <summary>
/// 多品牌键鼠耳机电量工具。
///
/// 与罗技专用版的关系：并存的第三个应用，界面与共享代码风格一致，
/// 但面向多设备多品牌。刻意**不含续航预测**与历史曲线 ——
/// 只做电量显示与低电量预警。
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
            // 只设 Console.OutputEncoding 是不够的：当 stdout 被重定向到管道
            // 时，.NET 仍按控制台代码页写出，实测包内程序输出的是 GBK，
            // 调用方按 UTF-8 解析就全是乱码。因此这里在设置编码之外，
            // 还显式打开一个 UTF-8 的 stdout 写入器。
            ConsoleSession.UseUtf8Output();

            switch (args[0])
            {
                case "--test-protocols":
                    return ProtocolTests.Run();
                case "--list":
                    return ListDevices();
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
            }
        }

        ApplicationConfiguration.Initialize();

        // 单实例：多开会导致托盘出现两个图标、通知重复
        using var mutex = new Mutex(true, AppIdentity.MutexName, out bool created);
        if (!created) return 0;

        Application.Run(new TrayContext());
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("multi-tray —— 多品牌键鼠耳机电量托盘工具");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  multi-tray.exe                启动托盘程序");
        Console.WriteLine("  multi-tray.exe --list         列出检测到的设备与当前电量后退出");
        Console.WriteLine("  multi-tray.exe --test-protocols  运行协议解析层自检（无需硬件）");
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
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly MultiSettings _settings;

    private List<DeviceReading> _readings = new();
    private Icon? _currentIcon;
    private bool _dark;

    /// <summary>各设备上次已通知的电量，用于避免同一电量反复提醒。</summary>
    private readonly Dictionary<string, int> _lastNotified = new();

    public TrayContext()
    {
        _settings = MultiSettings.Load();
        _dark = ResolveDark();

        _tray = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.MouseClick += OnTrayClick;
        _tray.DoubleClick += (_, _) => ShowDetails();

        _timer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(5, _settings.Interval) * 1000,
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    private bool ResolveDark() => _settings.ThemeMode switch
    {
        "dark" => true,
        "light" => false,
        _ => AppPalette.IsSystemDark(),
    };

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        menu.Items.Add(new ToolStripMenuItem("查看设备", null, (_, _) => ShowDetails()));
        menu.Items.Add(new ToolStripMenuItem("立即刷新", null, (_, _) => Refresh()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("设置", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));

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
    /// 只在电量确实下降、且与上次已通知的值不同时才提醒，
    /// 避免同一个电量每轮都弹一次（这是低电量提醒最常见的骚扰来源）。
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
                ? ToolTipIcon.Warning
                : ToolTipIcon.Info;
            _tray.ShowBalloonTip(6000);

            _lastNotified[r.Key] = r.Percent;
        }

        // 已充电或已换设备的记录清掉，下次低电量能重新提醒
        var keys = _readings.Where(x => x.IsOnline).Select(x => x.Key).ToHashSet();
        foreach (var stale in _lastNotified.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _lastNotified.Remove(stale);
        }
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) ShowDetails();
    }

    private void ShowDetails()
    {
        using var form = new DetailsForm(_readings, _dark, _settings);
        form.ShowDialog();
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog() == DialogResult.OK)
        {
            _settings.Save();
            _dark = ResolveDark();
            _timer.Interval = Math.Max(5, _settings.Interval) * 1000;
            Refresh();
        }
    }

    private void ExitApp()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _currentIcon?.Dispose();
        ExitThread();
    }
}
