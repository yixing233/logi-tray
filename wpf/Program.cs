using System;
using System.Threading;
using System.Windows;

namespace MouseBatteryTray;

public static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // 单实例检查
        _singleInstanceMutex = new Mutex(true, "logi_tray_SingleInstance", out bool isNewInstance);
        if (!isNewInstance)
        {
            // 已有实例在运行
            return;
        }

        var app = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        bool configExisted = SettingsConfig.ConfigExists();
        var config = SettingsConfig.Load();
        ThemeService.SetThemeMode(config.ThemeMode);
        ThemeService.SetAcrylicEnabled(config.AcrylicEnabled);

        // 对齐开机自启：首次运行沿用安装脚本写入的状态，之后以配置为准
        config.Autostart = AutoStartService.ResolveInitialState(config.Autostart, configExisted);
        if (!configExisted)
        {
            // 首次运行落盘一次，避免每次都重新探测注册表
            try { config.Save(); } catch { }
        }

        MouseBatteryDetailsWindow? detailsWin = null;
        SettingsWindow? settingsWin = null;
        TrayIconManager? trayManager = null;
        BatteryService? batteryService = null;

        void ShowDetailsAt(int anchorX, int anchorY)
        {
            MemoryOptimizer.NotifyActivity();

            if (detailsWin == null)
            {
                detailsWin = new MouseBatteryDetailsWindow(ShowSettings);
            }

            if (batteryService != null)
            {
                detailsWin.UpdateData(batteryService.CurrentSnapshot);
            }

            // 锚点优先使用托盘图标被按下时的光标位置：右键菜单弹出后菜单自身会位移，
            // 此时取 GetCursorPos 会把卡片定到菜单项那个错误的位置上。
            detailsWin.ToggleNear(anchorX, anchorY);
        }

        void ShowSettings()
        {
            MemoryOptimizer.NotifyActivity();

            if (settingsWin == null || !settingsWin.IsLoaded)
            {
                settingsWin = new SettingsWindow(config, savedCfg =>
                {
                    batteryService?.UpdateInterval(savedCfg.Interval);
                    trayManager?.SetStyle(savedCfg.TrayIconStyle);
                }, previewStyle =>
                {
                    trayManager?.SetStyle(previewStyle);
                });
                settingsWin.Closed += (_, _) => settingsWin = null;
                settingsWin.Show();
                settingsWin.Topmost = true;
                settingsWin.Activate();
                settingsWin.Focus();
                settingsWin.Topmost = false; // 仅弹出激活瞬间置前，绝不常驻置顶
            }
            else
            {
                settingsWin.Topmost = true;
                settingsWin.Activate();
                settingsWin.Focus();
                settingsWin.Topmost = false;
            }
        }

        void ExitApp()
        {
            batteryService?.Dispose();
            trayManager?.Dispose();
            detailsWin?.Close();
            settingsWin?.Close();
            app.Shutdown();
        }

        batteryService = new BatteryService(config);
        trayManager = new TrayIconManager(ShowDetailsAt, ShowSettings, ExitApp, config.TrayIconStyle, style =>
        {
            config.TrayIconStyle = style;
            config.Save();
        }, theme =>
        {
            config.ThemeMode = theme;
            config.Save();
        });

        batteryService.SnapshotUpdated += snapshot =>
        {
            app.Dispatcher.Invoke(() =>
            {
                trayManager.UpdateSnapshot(snapshot);
                if (detailsWin != null && detailsWin.IsVisible)
                {
                    detailsWin.UpdateData(snapshot);
                }
            });
        };

        batteryService.AlertTriggered += (title, msg) =>
        {
            app.Dispatcher.Invoke(() =>
            {
                trayManager.ShowNotification(title, msg);
            });
        };

        // 检查启动参数
        bool showDetailsOnStart = false;
        bool showSettingsOnStart = false;
        foreach (var arg in args)
        {
            if (arg == "--show-details") showDetailsOnStart = true;
            else if (arg == "--show-settings") showSettingsOnStart = true;
        }

        app.Startup += (_, _) =>
        {
            batteryService.RefreshNow();
            MemoryOptimizer.Start();

            if (showDetailsOnStart)
            {
                UnmanagedMethods.GetCursorPos(out var pt);
                if (pt.X <= 0 && pt.Y <= 0)
                {
                    pt.X = (int)SystemParameters.PrimaryScreenWidth - 40;
                    pt.Y = (int)SystemParameters.PrimaryScreenHeight - 40;
                }
                ShowDetailsAt(pt.X, pt.Y);
            }

            if (showSettingsOnStart)
            {
                ShowSettings();
            }
        };

        app.Run();
    }
}
