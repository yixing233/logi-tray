using System;
using System.Threading;
using System.Windows.Forms;

namespace MouseBatteryTray.Lite;

/// <summary>
/// 轻量版入口：纯 WinForms 实现，不引入 WPF。
///
/// 与完整版的差异：
///   - 托盘图标固定为「纯数字」样式（不提供样式切换）；
///   - 界面为普通 WinForms 窗口，不做亚克力/云母效果；
///   - 数据目录为 %LOCALAPPDATA%\logi-tray-lite，与完整版互不干扰。
/// </summary>
public static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // 独立的身份与数据目录：与完整版可同时运行、互不覆盖
        AppIdentity.Name = "logi-tray-lite";
        AppIdentity.LegacyDirectoryName = null;   // 不复用完整版的历史记录
        AppIdentity.MutexNameOverride = null;     // 由 Name 推导：logi-tray-lite_SingleInstance

        _singleInstanceMutex = new Mutex(true, AppIdentity.MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        bool configExisted = SettingsConfig.ConfigExists();
        var config = SettingsConfig.Load();

        using var context = new TrayApplicationContext(config, configExisted, args);
        Application.Run(context);
    }
}
