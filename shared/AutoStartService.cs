using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MouseBatteryTray;

/// <summary>
/// 开机自启动管理：通过 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 写入/移除
/// 当前用户的启动项（无需管理员权限，仅影响当前用户）。
/// </summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>自启项名称随应用身份变化，两版互不覆盖。</summary>
    private static string ValueName => AppIdentity.AutoStartValueName;

    /// <summary>当前可执行文件的完整路径（优先使用真实进程路径，回退到基目录拼接）。</summary>
    public static string ExecutablePath
    {
        get
        {
            try
            {
                string? path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            }
            catch { }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppIdentity.ExecutableName);
        }
    }

    /// <summary>读取注册表中当前登记的启动项命令（不存在返回 null）。</summary>
    public static string? RegisteredCommand
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(ValueName) as string;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>启动项是否已启用且指向当前程序。</summary>
    public static bool IsEnabled()
    {
        string? cmd = RegisteredCommand;
        if (string.IsNullOrWhiteSpace(cmd)) return false;

        string current = ExecutablePath;
        string normalized = cmd.Trim().Trim('"');
        return string.Equals(normalized, current, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>启用开机自启。返回是否成功。</summary>
    public static bool Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key == null) return false;

            key.SetValue(ValueName, $"\"{ExecutablePath}\"", RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>关闭开机自启。返回是否成功。</summary>
    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key?.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按需同步启动项状态，使其与配置保持一致。</summary>
    public static bool Apply(bool enabled) => enabled ? Enable() : Disable();

    /// <summary>
    /// 首次运行时（尚无配置文件）采用注册表现状，避免安装脚本写入的自启项被误删；
    /// 配置文件已存在时以配置为准，保证界面开关能真正关掉自启。
    /// </summary>
    public static bool ResolveInitialState(bool configuredEnabled, bool configFileExisted)
    {
        if (!configFileExisted)
        {
            return IsEnabled();
        }

        Apply(configuredEnabled);
        return configuredEnabled;
    }
}
