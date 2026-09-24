using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MouseBatteryTray.Lite;

/// <summary>
/// 轻量版主逻辑：托盘图标 + 右键菜单 + 电池轮询。
///
/// 托盘图标固定为纯数字样式，不提供样式/主题切换与亚克力效果。
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly SettingsConfig _config;
    private readonly BatteryService _batteryService;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _detailsItem;
    private readonly ToolStripMenuItem _settingsItem;

    /// <summary>UI 线程上下文。轮询在后台线程触发事件，必须切回 UI 线程再碰控件。</summary>
    private readonly SynchronizationContext _ui;

    private DetailsForm? _detailsForm;
    private SettingsForm? _settingsForm;

    private Icon? _currentIcon;
    private IntPtr _currentHIcon = IntPtr.Zero;

    private int _lastPercent = -999;
    private bool _lastCharging;

    /// <summary>托盘图标被点击时的锚点（菜单弹出后光标会移动，须提前捕获）。</summary>
    private Point _anchor;
    private bool _hasAnchor;

    public TrayApplicationContext(SettingsConfig config, bool configExisted, string[] args)
    {
        _config = config;

        // Application.Run 之后 SynchronizationContext.Current 才可用；
        // 这里在 UI 线程上先捕获一个，保证后台线程的事件总能切回来。
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);

        // 对齐开机自启：首次运行沿用安装脚本写入的状态，之后以配置为准
        _config.Autostart = AutoStartService.ResolveInitialState(_config.Autostart, configExisted);
        if (!configExisted)
        {
            try { _config.Save(); } catch { }
        }

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "logi-tray-lite"
        };

        var icon = LoadAppIcon();
        if (icon != null)
        {
            _notifyIcon.Icon = icon;
        }

        _menu = new ContextMenuStrip();
        _detailsItem = new ToolStripMenuItem("显示详情");
        _detailsItem.Click += (_, _) => ShowDetails();

        _settingsItem = new ToolStripMenuItem("设置...");
        _settingsItem.Click += (_, _) => ShowSettings();

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApp();

        _menu.Items.Add(_detailsItem);
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        // 菜单弹出前捕获锚点：菜单自身会把光标位置带偏
        _menu.Opening += (_, _) => CaptureAnchor();

        _notifyIcon.ContextMenuStrip = _menu;
        _notifyIcon.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                CaptureAnchor();
            }
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleDetails();
            }
        };

        _batteryService = new BatteryService(_config);
        _batteryService.SnapshotUpdated += OnSnapshotUpdated;
        _batteryService.AlertTriggered += OnAlertTriggered;

        // 首帧图标：先给出占位，随后由第一次轮询刷新
        UpdateIcon(-1, false);
        _batteryService.RefreshNow();

        // --show-settings / --show-details 便于截图与自检
        foreach (string arg in args)
        {
            if (arg == "--show-settings")
            {
                ShowSettings();
            }
            else if (arg == "--show-details")
            {
                ShowDetails();
            }
        }
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(path))
            {
                return new Icon(path);
            }
        }
        catch { }

        return null;
    }

    private void CaptureAnchor()
    {
        if (UnmanagedMethods.GetCursorPos(out var pt))
        {
            _anchor = new Point(pt.X, pt.Y);
            _hasAnchor = true;
        }
    }

    private Point AnchorPoint
    {
        get
        {
            if (_hasAnchor)
            {
                return _anchor;
            }

            if (UnmanagedMethods.GetCursorPos(out var pt))
            {
                return new Point(pt.X, pt.Y);
            }

            return new Point(
                Screen.PrimaryScreen?.WorkingArea.Right ?? 0,
                Screen.PrimaryScreen?.WorkingArea.Bottom ?? 0);
        }
    }

    private void OnSnapshotUpdated(BatterySnapshot snapshot)
    {
        // BatteryService 在线程池线程上触发；NotifyIcon 与窗口必须在 UI 线程操作
        _ui.Post(_ =>
        {
            UpdateSnapshot(snapshot);
            if (_detailsForm is { Visible: true })
            {
                _detailsForm.UpdateData(snapshot);
            }
        }, null);
    }

    private void OnAlertTriggered(string title, string message)
    {
        _ui.Post(_ => _notifyIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Warning), null);
    }

    private void UpdateSnapshot(BatterySnapshot snapshot)
    {
        if (snapshot.Percent != _lastPercent || snapshot.IsCharging != _lastCharging)
        {
            _lastPercent = snapshot.Percent;
            _lastCharging = snapshot.IsCharging;
            UpdateIcon(snapshot.Percent, snapshot.IsCharging);
        }

        string tooltip = snapshot.Percent >= 0
            ? $"{snapshot.DeviceName}: {snapshot.Percent}% ({snapshot.RemainingTimeText})"
            : $"{snapshot.DeviceName}: 离线或休眠";

        // NotifyIcon.Text 上限 63 字符，超出会抛异常
        if (tooltip.Length > 63)
        {
            tooltip = tooltip[..63];
        }

        _notifyIcon.Text = tooltip;
    }

    private void UpdateIcon(int percent, bool charging)
    {
        try
        {
            Icon icon = TrayIconRenderer.Render(percent, charging, AppPalette.IsSystemDark());

            // Icon.FromHandle 不接管句柄所有权，必须自己记住并销毁，
            // 否则每次刷新都会泄漏一个 HICON（GDI 资源）。
            _notifyIcon.Icon = icon;

            var oldIcon = _currentIcon;
            IntPtr oldHandle = _currentHIcon;

            _currentIcon = icon;

            // 取回该 Icon 底层句柄，供退出时释放
            _currentHIcon = icon.Handle;

            oldIcon?.Dispose();
            if (oldHandle != IntPtr.Zero && oldHandle != _currentHIcon)
            {
                DestroyIcon(oldHandle);
            }
        }
        catch
        {
            // 图标绘制失败不应影响托盘主功能
        }
    }

    private void ToggleDetails()
    {
        if (_detailsForm is { Visible: true })
        {
            _detailsForm.Hide();
            return;
        }

        ShowDetails();
    }

    private void ShowDetails()
    {
        if (_detailsForm == null || _detailsForm.IsDisposed)
        {
            _detailsForm = new DetailsForm(ShowSettings);
        }

        _detailsForm.UpdateData(_batteryService.CurrentSnapshot);
        _detailsForm.ShowNear(AnchorPoint);
    }

    private void ShowSettings()
    {
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_config, saved =>
            {
                _batteryService.UpdateInterval(saved.Interval);
            });
        }

        _settingsForm.Show();
        _settingsForm.BringToFront();
        _settingsForm.Activate();
    }

    private void ExitApp()
    {
        _batteryService.Dispose();
        _notifyIcon.Visible = false;

        if (_currentHIcon != IntPtr.Zero)
        {
            DestroyIcon(_currentHIcon);
            _currentHIcon = IntPtr.Zero;
        }

        _currentIcon?.Dispose();
        _detailsForm?.Close();
        _settingsForm?.Close();
        _menu.Dispose();
        _notifyIcon.Dispose();

        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _batteryService.SnapshotUpdated -= OnSnapshotUpdated;
            _batteryService.AlertTriggered -= OnAlertTriggered;
        }

        base.Dispose(disposing);
    }
}
