using System;
using System.Reflection;
using System.Windows;

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
    private static readonly string AssemblyName =
        Assembly.GetEntryAssembly()?.GetName().Name ?? "multi-tray";

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
