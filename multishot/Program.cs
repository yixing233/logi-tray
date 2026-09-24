using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MouseBatteryTray;
using MultiTray;

/// <summary>
/// 离屏渲染自检宿主。
///
/// 为什么不截图：截真实窗口需要把窗口显示出来，会打断用户正在玩的游戏
/// （已经被明确抱怨过一次）。这里用 Measure/Arrange + RenderTargetBitmap
/// 直接把视觉树渲染成图片，**全程不创建可见窗口**，既能看到真实外观，
/// 又完全不打扰用户。
///
/// 亚克力是 DWM 合成效果，离屏渲染看不到背景虚化本身；
/// 但本次要验证的是**控件内容是否正确**（文字有没有丢、布局有没有塌），
/// 这正是离屏渲染能可靠覆盖的部分。
/// </summary>
internal static class Shot
{
    [STAThread]
    private static int Main(string[] args)
    {
        string outDir = args.Length > 0
            ? args[0]
            : @"C:\code\chat-records\mouse-tray\_shots";
        Directory.CreateDirectory(outDir);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        int failures = 0;
        app.Startup += (_, _) =>
        {
            try
            {
                failures += ShotSettings(outDir);
                failures += ShotCard(outDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine("异常: " + ex);
                failures++;
            }
            finally
            {
                app.Shutdown();
            }
        };

        app.Run();
        return failures == 0 ? 0 : 1;
    }

    private static int ShotSettings(string outDir)
    {
        var settings = MultiSettings.Load();

        // 浅色与深色各渲染一张：深色下最容易暴露「黑字黑底」
        int bad = 0;
        foreach (var (mode, tag) in new[] { ("light", "light"), ("dark", "dark") })
        {
            ThemeService.SetThemeMode(mode);
            var win = new MultiSettingsWindow(settings);
            string path = Path.Combine(outDir, $"settings_{tag}.png");
            var (bytes, texts, root) = Render(win, path);

            Console.WriteLine($"[settings/{tag}] {path}  {bytes} bytes");
            bad += Check(texts, tag, new[]
            {
                "电量阈值与提醒",
                "低电量提醒阈值",
                "严重低电量阈值",
                "启用低电量桌面通知",
                "后台刷新间隔",
                "启用的设备来源",
                "罗技（HID++）",
                "迈从 MCHOSE",
                "ATK / VXE / VGN",
                "开机自动启动",
                "保存设置",
                "取消",
            });
            bad += CheckToggleStyle(root, tag);

            // 未保存就关闭：避免自检改写用户真实配置
            win.Close();
        }
        return bad;
    }

    private static int ShotCard(string outDir)
    {
        var settings = MultiSettings.Load();
        var readings = DeviceReader.ReadAll(settings);

        int bad = 0;
        foreach (var (mode, tag) in new[] { ("light", "light"), ("dark", "dark") })
        {
            ThemeService.SetThemeMode(mode);
            var win = new DeviceCardWindow(readings, settings, () => { });
            string path = Path.Combine(outDir, $"card_{tag}.png");
            var (bytes, texts, root) = Render(win, path);

            Console.WriteLine($"[card/{tag}] {path}  {bytes} bytes");
            bad += Check(texts, tag, new[] { "立即刷新", "关闭" });
            bad += CheckCardContent(texts, readings, tag);
            win.Close();
        }
        return bad;
    }

    /// <summary>卡片应展示每台设备的名称与电量，缺任意一台都算失败。</summary>
    private static int CheckCardContent(List<string> found, List<DeviceReading> readings,
                                        string tag)
    {
        int missing = 0;
        foreach (var r in readings)
        {
            bool hasName = found.Exists(t => t.Contains(r.Name, StringComparison.Ordinal));
            if (!hasName)
            {
                Console.WriteLine($"  !! [{tag}] 卡片缺少设备: {r.Name}");
                missing++;
            }

            string expect = r.Percent >= 0 ? $"{r.Percent}%" : "--%";
            bool hasPct = found.Exists(t => t.Contains(expect, StringComparison.Ordinal));
            if (!hasPct)
            {
                Console.WriteLine($"  !! [{tag}] 卡片缺少电量 {expect}（{r.Name}）");
                missing++;
            }
        }
        Console.WriteLine(
            $"  [{tag}] 卡片设备 {readings.Count - missing}/{readings.Count} 完整");
        return missing;
    }

    /// <summary>
    /// 把窗口视觉树离屏渲染并收集其中所有文本。
    /// 返回渲染字节数与文本集合，供调用方断言关键文案确实存在。
    /// </summary>
    private static (int bytes, List<string> texts, DependencyObject root) Render(
        Window win, string path)
    {
        // 把窗口**移到所有显示器之外**再渲染。
        //
        // 为什么不用「摘出内容再离屏布局」：内容一旦与窗口脱离视觉树，
        // DynamicResource 就查不到窗口的 MergedDictionaries，
        // FluentToggleSwitchStyle 会静默失效、开关退化成系统默认复选框。
        // 那是宿主自己制造的假象 —— 真实应用里开关是正常的蓝色胶囊（用户截图可见）。
        //
        // 移到屏幕外则资源查找、样式应用、布局流程与真实运行**完全一致**，
        // 同时因为坐标在所有显示器左外侧，用户什么也看不到，不会打扰。
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.ShowInTaskbar = false;
        win.ShowActivated = false;
        win.Left = SystemParameters.VirtualScreenLeft - win.Width - 400;
        win.Top = SystemParameters.VirtualScreenTop - 400;

        win.Show();
        win.UpdateLayout();

        // 布局与动画要给调度器留出一次机会，否则拿到的是未完成状态
        win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        win.UpdateLayout();

        int w = (int)Math.Ceiling(win.ActualWidth > 0 ? win.ActualWidth : win.Width);
        int h = (int)Math.Ceiling(win.ActualHeight > 0
            ? win.ActualHeight
            : win.DesiredSize.Height);
        if (h <= 0) h = 600;

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);

        // 离屏渲染没有 DWM 亚克力，窗口透明底会渲染成透明，
        // 深色主题下就看不出「黑字」。先铺一层实色底再叠加窗口内容。
        var bg = new System.Windows.Media.DrawingVisual();
        using (var dc = bg.RenderOpen())
        {
            dc.DrawRectangle(ThemeService.SolidSurfaceBrush, null, new Rect(0, 0, w, h));
        }
        bmp.Render(bg);

        if (win.Content is UIElement content)
        {
            bmp.Render(content);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) encoder.Save(fs);

        var texts = new List<string>();
        DependencyObject? root = win.Content as DependencyObject;
        if (root != null) Collect(root, texts);

        return ((int)new FileInfo(path).Length, texts, root ?? win);
    }

    private static void Collect(DependencyObject node, List<string> into)
    {
        if (node is System.Windows.Controls.TextBlock tb && !string.IsNullOrEmpty(tb.Text))
        {
            into.Add(tb.Text);
        }
        else if (node is System.Windows.Controls.ContentControl cc &&
                 cc.Content is string s && !string.IsNullOrEmpty(s))
        {
            into.Add(s);
        }

        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            Collect(VisualTreeHelper.GetChild(node, i), into);
        }
    }

    /// <summary>
    /// 校验所有开关都是 Fluent 胶囊样式，而不是退化成系统默认方形复选框。
    ///
    /// 判据用布局后的实际宽度：胶囊开关整体宽 40px，而系统默认复选框只有约 13px。
    /// 这条断言能抓住「样式没被应用」和「Content 被模板丢弃」这两类静默失败 ——
    /// 它们都不会报错，只会让界面看起来空荡荡（用户实际遇到的就是这个）。
    /// </summary>
    private static int CheckToggleStyle(DependencyObject root, string tag)
    {
        var boxes = new List<System.Windows.Controls.CheckBox>();
        FindCheckBoxes(root, boxes);

        if (boxes.Count == 0)
        {
            Console.WriteLine($"  !! [{tag}] 没有找到任何开关");
            return 1;
        }

        int wrong = 0;
        foreach (var b in boxes)
        {
            if (b.ActualWidth < 30 || b.ActualHeight < 16)
            {
                Console.WriteLine(
                    $"  !! [{tag}] 开关未套用 Fluent 样式: {b.ActualWidth:F1}x{b.ActualHeight:F1}");
                wrong++;
            }
        }

        Console.WriteLine(
            $"  [{tag}] 开关样式 {boxes.Count - wrong}/{boxes.Count} 是 Fluent 胶囊");
        return wrong;
    }

    private static void FindCheckBoxes(DependencyObject node,
                                       List<System.Windows.Controls.CheckBox> into)
    {
        if (node is System.Windows.Controls.CheckBox cb) into.Add(cb);
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            FindCheckBoxes(VisualTreeHelper.GetChild(node, i), into);
        }
    }

    private static int Check(List<string> found, string tag, string[] required)
    {
        int missing = 0;
        foreach (string r in required)
        {
            bool ok = found.Exists(t => t.Contains(r, StringComparison.Ordinal));
            if (!ok)
            {
                Console.WriteLine($"  !! [{tag}] 缺少文案: {r}");
                missing++;
            }
        }
        Console.WriteLine($"  [{tag}] 文案检查 {required.Length - missing}/{required.Length} 通过");
        return missing;
    }
}
