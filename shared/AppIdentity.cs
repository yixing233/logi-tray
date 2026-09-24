using System;
using System.IO;

namespace MouseBatteryTray;

/// <summary>
/// 应用身份：决定数据目录、单实例互斥名与开机自启注册表项。
///
/// 两个版本（带亚克力效果的完整版、主打小占用的轻量版）共用同一套共享源码，
/// 因此这里由各自的 Main 在启动最早阶段赋值，从而：
///   - 各自拥有独立的 settings.json / history.json，互不覆盖；
///   - 单实例名为各自所有，两个版本可同时运行、便于对比；
///   - 开机自启项互不干扰。
///
/// 默认值保持 "logi-tray"，因此完整版的行为与引入本类之前完全一致。
/// </summary>
public static class AppIdentity
{
    /// <summary>应用名，同时用作数据目录名与自启项名。</summary>
    public static string Name { get; set; } = "logi-tray";

    /// <summary>
    /// 单实例互斥体名称。默认由 <see cref="Name"/> 推导，
    /// 完整版沿用历史上的 "logi_tray_SingleInstance" 以免升级瞬间出现双实例。
    /// </summary>
    public static string? MutexNameOverride { get; set; }

    /// <summary>数据目录：%LOCALAPPDATA%\{Name}</summary>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Name);

    /// <summary>单实例互斥体名称。</summary>
    public static string MutexName => MutexNameOverride ?? Name + "_SingleInstance";

    /// <summary>开机自启在 Run 项下的值名。</summary>
    public static string AutoStartValueName => Name;

    /// <summary>主程序文件名。</summary>
    public static string ExecutableName => Name + ".exe";

    /// <summary>
    /// 旧版本数据目录名。仅完整版用于从更早的 MouseBatteryTray 版本迁移；
    /// 轻量版不复用该目录，避免与完整版的历史记录相互污染。
    /// </summary>
    public static string? LegacyDirectoryName { get; set; } = "MouseBatteryTray";

    /// <summary>旧版本数据目录的完整路径（无旧目录时为 null）。</summary>
    public static string? LegacyDirectory => string.IsNullOrEmpty(LegacyDirectoryName)
        ? null
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyDirectoryName!);
}
