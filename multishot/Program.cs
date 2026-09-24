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
                failures += CheckBranding(outDir);
                failures += ShotIcons(outDir);
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

    /// <summary>
    /// 断言多品牌版与罗技版在品牌上确实区分开了。
    ///
    /// 依据：两版的 app.ico 必须是不同文件（多品牌版用的是三根电量条图标，
    /// 不是罗技的「G」）。这条断言专门防回归 —— 早先多品牌版直接沿用了
    /// 罗技图标，用户指出后才区分开，而这种事光靠看代码不容易发现。
    /// </summary>
    private static int CheckBranding(string outDir)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string mine = Path.Combine(baseDir, "app.ico");

        // 自检宿主自己的输出目录里也会带一份（ProjectReference 传递过来）
        if (!File.Exists(mine))
        {
            Console.WriteLine("  !! 找不到 multi-tray 的 app.ico");
            return 1;
        }

        // 与源码树里的完整版图标比对：两者必须不同
        string wpfIco = @"C:\code\chat-records\mouse-tray\wpf\app.ico";
        if (!File.Exists(wpfIco))
        {
            Console.WriteLine("  [品牌] 跳过（找不到 wpf/app.ico 作对照）");
            return 0;
        }

        string a = Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(mine)));
        string b = Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(wpfIco)));

        if (a == b)
        {
            Console.WriteLine("  !! 多品牌版与罗技版使用了同一个图标（品牌未区分）");
            return 1;
        }

        // ICO 必须含多个尺寸，否则任务栏/Alt-Tab 会放大糊图
        var bytes = File.ReadAllBytes(mine);
        int count = bytes.Length >= 6 ? bytes[4] | (bytes[5] << 8) : 0;
        if (count < 4)
        {
            Console.WriteLine($"  !! app.ico 只含 {count} 个尺寸，任务栏会显示模糊");
            return 1;
        }

        Console.WriteLine($"  [品牌] 图标与罗技版不同，含 {count} 个尺寸");
        return 0;
    }

    /// <summary>
    /// 把要用的图标码点渲染成对照图。
    ///
    /// 字形名表被裁剪过，无法从字体里查名字；而我先前凭猜码点把设备图标
    /// 指到了菜单、登出箭头、地球上。唯一可靠的确认方式就是画出来看。
    /// 这里把每个码点连同它的编号一起渲染，便于逐一核对。
    /// </summary>
    private static int ShotIcons(string outDir)
    {
        // 扫描一个候选区间，把码点连同编号一起画出来核对。
        //
        // 为什么不用现成的 LucideIcons.Refresh：它渲染出来是个带刻度的圆盘，
        // 语义上更像「仪表」而不是「刷新」。字体按图标名**字母序**排列
        // （已证实 battery E053 < check E06C < hash E0EF < monitor E11D <
        // settings E154 < sun E178 < x E1B2 < zap E1B4），
        // 所以循环箭头类图标就在 settings 前后的 r/s 段，扫一遍即可确认。
        const int start = 0xE140;
        const int end = 0xE190;
        const int cols = 8;
        const int cell = 62;
        int count = end - start;
        int rows = (count + cols - 1) / cols;

        int w = cols * cell;
        int h = rows * cell;

        var canvas = new System.Windows.Controls.Canvas
        {
            Width = w,
            Height = h,
            Background = Brushes.White
        };

        for (int i = 0; i < count; i++)
        {
            int cp = start + i;
            double cx = (i % cols) * cell;
            double cy = (i / cols) * cell;

            var box = new System.Windows.Shapes.Rectangle
            {
                Width = cell,
                Height = cell,
                Stroke = new SolidColorBrush(Color.FromRgb(238, 238, 238)),
                StrokeThickness = 1
            };
            System.Windows.Controls.Canvas.SetLeft(box, cx);
            System.Windows.Controls.Canvas.SetTop(box, cy);
            canvas.Children.Add(box);

            var glyph = new System.Windows.Controls.TextBlock
            {
                Text = char.ConvertFromUtf32(cp),
                FontFamily = ThemeService.LucideFont,
                FontSize = 24,
                Foreground = Brushes.Black,
                Width = cell,
                TextAlignment = TextAlignment.Center
            };
            System.Windows.Controls.Canvas.SetLeft(glyph, cx);
            System.Windows.Controls.Canvas.SetTop(glyph, cy + 6);
            canvas.Children.Add(glyph);

            var label = new System.Windows.Controls.TextBlock
            {
                Text = $"{cp:X4}",
                FontSize = 9,
                Foreground = Brushes.Gray,
                Width = cell,
                TextAlignment = TextAlignment.Center
            };
            System.Windows.Controls.Canvas.SetLeft(label, cx);
            System.Windows.Controls.Canvas.SetTop(label, cy + cell - 17);
            canvas.Children.Add(label);
        }

        canvas.Measure(new Size(w, h));
        canvas.Arrange(new Rect(0, 0, w, h));
        canvas.UpdateLayout();

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(canvas);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        string path = Path.Combine(outDir, "icons.png");
        using (var fs = File.Create(path)) enc.Save(fs);

        Console.WriteLine($"[icons] {path}  {w}x{h}  E{start:X3}-E{end:X3}");
        return 0;
    }

    private static int ShotIconsUnused(string outDir)
    {
        var items = new (string label, string glyph)[]
        {
            ("Settings", LucideIcons.Settings),
            ("Refresh", LucideIcons.Refresh),
            ("X", LucideIcons.X),
            ("Battery", LucideIcons.Battery),
            ("Zap", LucideIcons.Zap),
            ("Check", LucideIcons.Check),
            ("Mouse?", "\ue115"),
            ("Keyboard?", "\ue10e"),
            ("Headphones", "\ue0f1"),
        };
        var panel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Background = Brushes.White
        };

        foreach (var (label, glyph) in items)
        {
            var col = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(0, 0, 16, 0)
            };
            col.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = glyph,
                FontFamily = ThemeService.LucideFont,
                FontSize = 32,
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            col.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            col.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"U+{(int)glyph[0]:X4}",
                FontSize = 9,
                Foreground = Brushes.Silver,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            panel.Children.Add(col);
        }

        var border = new System.Windows.Controls.Border
        {
            Background = Brushes.White,
            Padding = new Thickness(12),
            Child = panel
        };
        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int w = (int)Math.Ceiling(border.DesiredSize.Width);
        int h = (int)Math.Ceiling(border.DesiredSize.Height);
        border.Arrange(new Rect(0, 0, w, h));
        border.UpdateLayout();

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(border);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        string path = Path.Combine(outDir, "icons.png");
        using (var fs = File.Create(path)) enc.Save(fs);

        Console.WriteLine($"[icons] {path}  {w}x{h}");
        return 0;
    }

    private static int ShotSettings(string outDir)    {
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

            // 关于页：同一窗口切页后再渲染一次，确认二级页面内容齐全
            win.ShowAboutPageForTest();
            string aboutPath = Path.Combine(outDir, $"about_{tag}.png");
            var (aboutBytes, aboutTexts, aboutRoot) = Render(win, aboutPath);
            Console.WriteLine($"[about/{tag}] {aboutPath}  {aboutBytes} bytes");
            bad += Check(aboutTexts, tag + "/about", new[]
            {
                "关于",
                "multi-tray",
                "版本 ",
                "作者",
                "开源协议",
                "GPL-3.0",
                "仓库地址",
                "yixing233/logi-tray",
                "检查更新",
            });
            bad += CheckButtonPresent(aboutRoot, tag + "/about", "检查更新");
            bad += CheckAboutIcon(aboutRoot, tag + "/about");

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
            bad += Check(texts, tag, new[] { "设备电量" });
            bad += CheckCardContent(texts, readings, tag);
            bad += CheckAbsent(texts, tag, new[]
            {
                "来源",
                "唤醒设备后点",
                "低电量提醒",
                // 底部按钮已按要求移除：刷新改为标题栏图标，关闭改为点击外侧
                "立即刷新",
                "关闭",
            });
            bad += CheckCardIcons(root, tag);
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

    /// <summary>
    /// 卡片标题栏应有两个图标按钮（刷新、设置）。
    ///
    /// 图标是字体字形，不是字符串，常规的文案断言看不到它们；
    /// 这里按控件类型找 Button，并核对它的字形码点，
    /// 避免出现「按钮在但图标缺失/写错码点」这种看不出来的问题。
    /// </summary>
    private static int CheckCardIcons(DependencyObject root, string tag)
    {
        var buttons = new List<System.Windows.Controls.Button>();
        FindButtons(root, buttons);

        var glyphs = new List<string>();
        foreach (var b in buttons)
        {
            if (b.Content is string s) glyphs.Add(s);
        }

        int bad = 0;
        if (!glyphs.Contains("\ue145"))
        {
            Console.WriteLine($"  !! [{tag}] 缺少刷新图标按钮");
            bad++;
        }
        if (!glyphs.Contains(MouseBatteryTray.LucideIcons.Settings))
        {
            Console.WriteLine($"  !! [{tag}] 缺少设置图标按钮");
            bad++;
        }

        Console.WriteLine($"  [{tag}] 图标按钮 {2 - bad}/2 存在（共发现 {buttons.Count} 个按钮）");
        return bad;
    }

    /// <summary>
    /// 断言关于页顶部的应用图标真的加载出来了。
    ///
    /// 只检查「有没有 Image 控件」不够：Image 存在但 Source 为 null 时
    /// 界面是一片空白，不报任何错。必须确认 Source 非空且已解码出像素。
    /// </summary>
    private static int CheckAboutIcon(DependencyObject root, string tag)
    {
        var images = new List<System.Windows.Controls.Image>();
        FindImages(root, images);

        if (images.Count == 0)
        {
            Console.WriteLine($"  !! [{tag}] 关于页没有图标控件");
            return 1;
        }

        var img = images[0];
        if (img.Source == null)
        {
            Console.WriteLine($"  !! [{tag}] 关于页图标 Source 为空（界面会是空白）");
            return 1;
        }

        int pw = img.Source.Width > 0 ? (int)img.Source.Width : 0;
        if (pw <= 0)
        {
            Console.WriteLine($"  !! [{tag}] 关于页图标尺寸异常: {pw}");
            return 1;
        }

        Console.WriteLine($"  [{tag}] 关于页图标已加载 {pw}px");
        return 0;
    }

    private static void FindImages(DependencyObject node,
                                   List<System.Windows.Controls.Image> into)
    {
        if (node is System.Windows.Controls.Image im) into.Add(im);
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            FindImages(VisualTreeHelper.GetChild(node, i), into);
        }
    }

    /// <summary>断言某个按钮文案存在（用于「关于」入口与检查更新按钮）。</summary>
    private static int CheckButtonPresent(DependencyObject root, string tag, string label)
    {
        var buttons = new List<System.Windows.Controls.Button>();
        FindButtons(root, buttons);

        bool found = buttons.Exists(b => b.Content is string s && s.Contains(label));
        if (!found)
        {
            Console.WriteLine($"  !! [{tag}] 缺少按钮: {label}");
            return 1;
        }
        Console.WriteLine($"  [{tag}] 按钮「{label}」存在");
        return 0;
    }

    private static void FindButtons(DependencyObject node,
                                    List<System.Windows.Controls.Button> into)
    {
        if (node is System.Windows.Controls.Button b) into.Add(b);
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            FindButtons(VisualTreeHelper.GetChild(node, i), into);
        }
    }

    /// <summary>
    /// 断言这些文案**不**出现在界面上。
    ///
    /// 用户明确要求移除卡片底部的说明与离线设备那行提示；
    /// 只靠肉眼比对截图容易漏（先前就漏掉过空开关），因此固化成断言。
    /// </summary>
    private static int CheckAbsent(List<string> found, string tag, string[] forbidden)
    {
        int present = 0;
        foreach (string f in forbidden)
        {
            if (found.Exists(t => t.Contains(f, StringComparison.Ordinal)))
            {
                Console.WriteLine($"  !! [{tag}] 仍有文案未移除: {f}");
                present++;
            }
        }
        Console.WriteLine(
            $"  [{tag}] 应移除文案 {forbidden.Length - present}/{forbidden.Length} 已移除");
        return present;
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
