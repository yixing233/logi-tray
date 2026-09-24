using System;
using System.Reflection;
using System.Windows;
using MouseBatteryTray;

namespace MultiTray;

/// <summary>
/// 把完整版的 FluentStyles.xaml 载入当前窗口的资源字典。
///
/// 为什么不直接照抄完整版的写法：那里硬编码了程序集名
///     pack://application:,,,/logi-tray;component/FluentStyles.xaml
/// 多品牌版的程序集是 multi-tray，这个 URI 会解析失败；而调用处是
/// try/catch 包裹的，失败后**静默**回退到默认画刷 —— 表现为深色模式下文字全黑。
///
/// 这里按入口程序集名动态拼装，并在失败时尝试相对 URI 兜底。
/// </summary>
internal static class FluentStylesLoader
{
    /// <summary>
    /// 资源所在程序集名。
    ///
    /// 不能用 Assembly.GetEntryAssembly()：那只在被自己启动时才对。
    /// 一旦被别的宿主进程加载（自检工具、测试宿主），入口程序集就变成宿主，
    /// 拼出的 pack URI 指向一个不存在该资源的程序集，三次尝试全部失败、
    /// 样式静默失效（表现为开关退化成系统默认方形复选框）。
    ///
    /// FluentStyles.xaml 与 ThemeService 一起编在本程序集里，
    /// 因此直接问 ThemeService 类型属于哪个程序集，永远是正确答案。
    /// </summary>
    private static readonly string AssemblyName =
        typeof(ThemeService).Assembly.GetName().Name ?? "multi-tray";

    public static void Load(ResourceDictionary target)
    {
        // 1. 相对 URI：同程序集内的资源，与程序集名无关
        if (TryAdd(target, new Uri("FluentStyles.xaml", UriKind.Relative))) return;

        // 2. 按入口程序集拼 pack URI
        if (TryAdd(target, new Uri(
                $"pack://application:,,,/{AssemblyName};component/FluentStyles.xaml",
                UriKind.Absolute)))
            return;

        // 3. 最后回退到完整版的程序集名（两者共用同名资源时可用）
        TryAdd(target, new Uri(
            "pack://application:,,,/logi-tray;component/FluentStyles.xaml",
            UriKind.Absolute));
    }

    private static bool TryAdd(ResourceDictionary target, Uri uri)
    {
        try
        {
            target.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
