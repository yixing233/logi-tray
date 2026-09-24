using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace MouseBatteryTray;

/// <summary>
/// 空闲时把工作集归还给系统。
///
/// 实测：WPF 的渲染栈在第一次显示窗口之后会常驻进程——用一个空 WPF 窗口验证过，
/// 显示一次再关闭，工作集始终停在 ~110 MB 不再回落。托盘程序绝大多数时间没有窗口，
/// 那部分主要是可换出的页。
///
/// 修剪由「窗口从可见变为隐藏」这一状态转换触发，每个关闭周期只做一次：
/// 定时重复修剪只会把刚调入的页再次换出，带来无谓的磁盘 IO，收益却是零。
/// </summary>
internal static class MemoryOptimizer
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>min/max 同时传 (SIZE_T)-1 时，系统会修剪该进程的工作集。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    private static DispatcherTimer? _timer;

    /// <summary>本周期内是否已经修剪过，避免重复修剪同一段空闲期。</summary>
    private static bool _trimmedThisIdlePeriod = true;

    /// <summary>工作集低于此值就不必修剪，避免为了一点内存反复换页。</summary>
    private const long TrimThresholdBytes = 40L * 1024 * 1024;

    /// <summary>收起窗口后延迟一小会儿再修剪，避开关闭动画与紧随其后的重绘。</summary>
    private static readonly TimeSpan TrimDelay = TimeSpan.FromSeconds(3);

    private static DateTime _hiddenSinceUtc = DateTime.MinValue;

    public static void Start()
    {
        if (_timer != null)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>有窗口显示（或即将显示）时调用，开启新的空闲周期。</summary>
    public static void NotifyActivity()
    {
        _trimmedThisIdlePeriod = false;
        _hiddenSinceUtc = DateTime.MinValue;
    }

    private static void Tick()
    {
        try
        {
            if (AnyWindowVisible())
            {
                // 有窗口在场：重置空闲计时，本周期尚未修剪
                _hiddenSinceUtc = DateTime.MinValue;
                _trimmedThisIdlePeriod = false;
                return;
            }

            if (_trimmedThisIdlePeriod)
            {
                return;
            }

            if (_hiddenSinceUtc == DateTime.MinValue)
            {
                _hiddenSinceUtc = DateTime.UtcNow;
                return;
            }

            if (DateTime.UtcNow - _hiddenSinceUtc < TrimDelay)
            {
                return;
            }

            _trimmedThisIdlePeriod = true;

            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            if (proc.WorkingSet64 < TrimThresholdBytes)
            {
                return;
            }

            Trim();
        }
        catch
        {
            // 内存优化失败不应影响主功能
        }
    }

    private static bool AnyWindowVisible()
    {
        var app = Application.Current;
        if (app == null)
        {
            return false;
        }

        foreach (Window window in app.Windows)
        {
            if (window.IsVisible)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>先回收托管堆，再把工作集交还系统。</summary>
    public static void Trim()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);

            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // 忽略：属于尽力而为的优化
        }
    }
}
