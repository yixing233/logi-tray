using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MouseBatteryTray;

namespace MultiTray;

/// <summary>
/// 多品牌版设置窗口：Windows 11 Fluent 亚克力界面。
///
/// 复用完整版的外观层（shared-wpf/ThemeService.cs + FluentStyles.xaml）：
///   * DWM 亚克力毛玻璃 + WindowChrome 圆角
///   * FluentSliderStyle / FluentToggleSwitchStyle / FluentSegmentedButtonStyle /
///     FluentPrimaryButtonStyle / FluentSecondaryButtonStyle 全部来自共享样式表
///   * 同一分组的项共用一张卡片作为背景（不是每项一个卡片）
///
/// 与完整版的差异：多品牌版没有图标样式与外观主题选项（固定跟随系统），
/// 多出「启用的设备来源」分组。
/// </summary>
public sealed class MultiSettingsWindow : Window
{
    private const double WindowWidth = 420;
    private const double CardCornerRadius = 8;

    private readonly MultiSettings _settings;
    private readonly Border _cardBorder;
    private readonly StackPanel _rootPanel;

    /// <summary>标题栏文字：主页面显示「设置」，关于页显示「关于」。</summary>
    private TextBlock _titleText = null!;

    /// <summary>返回按钮，仅在关于页可见。</summary>
    private Button _backButton = null!;

    /// <summary>页面宿主：主设置页与关于页在此切换。</summary>
    private ContentControl _pageHost = null!;

    /// <summary>关于页上的「检查更新」按钮与状态文字。</summary>
    private Button? _checkUpdateButton;
    private TextBlock? _updateStatusText;
    private bool _updateCheckRunning;

    private Slider _lowSlider = null!;
    private Slider _criticalSlider = null!;
    private TextBlock _lowValue = null!;
    private TextBlock _criticalValue = null!;
    private CheckBox _notifyToggle = null!;
    private CheckBox _autostartToggle = null!;
    private CheckBox _srcLogitech = null!;
    private CheckBox _srcMchose = null!;
    private CheckBox _srcAtk = null!;

    private readonly List<(Button button, int seconds)> _intervalButtons = new();
    private int _interval;

    public MultiSettingsWindow(MultiSettings settings)
    {
        _settings = settings;
        _interval = Math.Max(5, settings.Interval);

        Title = "multi-tray · 设置";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.Height;
        Width = WindowWidth;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        System.Windows.Shell.WindowChrome.SetWindowChrome(this,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                CornerRadius = new CornerRadius(CardCornerRadius),
                GlassFrameThickness = new Thickness(-1),
                ResizeBorderThickness = new Thickness(0)
            });

        FluentStylesLoader.Load(Resources);
        ThemeService.ApplyThemeResources(Resources);

        _rootPanel = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        _cardBorder = new Border
        {
            CornerRadius = new CornerRadius(CardCornerRadius),
            BorderThickness = new Thickness(1),
            Background = ThemeService.IsAcrylicEnabled
                ? Brushes.Transparent
                : ThemeService.SolidSurfaceBrush,
            Child = _rootPanel
        };
        _cardBorder.SetResourceReference(Border.BorderBrushProperty, "ThemeCardBorder");
        Content = _cardBorder;

        Build();

        SourceInitialized += (_, _) => ThemeService.ApplyAcrylicBackdrop(this);
        ContentRendered += (_, _) => ThemeService.ApplyAcrylicBackdrop(this);

        // 无边框窗口需要自己处理拖动：按住标题区域可移动
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.GetPosition(this).Y < 44) DragMove();
        };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void Build()
    {
        // ── 顶部标题栏：返回(仅关于页) + 标题 + 关闭 ──
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _backButton = new Button
        {
            Content = new TextBlock
            {
                Text = LucideIcons.ArrowLeft,
                FontFamily = ThemeService.LucideFont,
                FontSize = 13
            },
            Width = 30,
            Height = 28,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "返回设置",
            Cursor = Cursors.Hand,
            Focusable = false,
            Visibility = Visibility.Collapsed
        };
        _backButton.SetResourceReference(Button.StyleProperty, "FluentSecondaryButtonStyle");
        _backButton.Click += (_, _) => ShowSettingsPage();
        Grid.SetColumn(_backButton, 0);
        header.Children.Add(_backButton);

        _titleText = new TextBlock
        {
            Text = "设置",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _titleText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimary");
        Grid.SetColumn(_titleText, 1);
        header.Children.Add(_titleText);

        var closeBtn = new Button
        {
            Content = new TextBlock
            {
                Text = LucideIcons.X,
                FontFamily = ThemeService.LucideFont,
                FontSize = 13
            },
            Width = 30,
            Height = 28,
            Cursor = Cursors.Hand,
            Focusable = false
        };
        closeBtn.SetResourceReference(Button.StyleProperty, "FluentSecondaryButtonStyle");
        closeBtn.Click += (_, _) => Close();
        Grid.SetColumn(closeBtn, 2);
        header.Children.Add(closeBtn);

        _rootPanel.Children.Add(header);

        // ── 页面宿主：主设置页 / 关于页 在此切换 ──
        _pageHost = new ContentControl { Content = BuildSettingsPage() };
        _rootPanel.Children.Add(_pageHost);

        // ── 底部按钮：左侧「关于」入口，右侧取消 / 保存 ──
        var buttons = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var about = new Button
        {
            Content = "关于",
            Height = 34,
            MinWidth = 76,
            Cursor = Cursors.Hand,
            Focusable = false
        };
        about.SetResourceReference(Button.StyleProperty, "FluentSecondaryButtonStyle");
        about.Click += (_, _) => ShowAboutPage();
        Grid.SetColumn(about, 0);
        buttons.Children.Add(about);

        var cancel = new Button
        {
            Content = "取消",
            Height = 34,
            MinWidth = 92,
            Cursor = Cursors.Hand,
            Focusable = false
        };
        cancel.SetResourceReference(Button.StyleProperty, "FluentSecondaryButtonStyle");
        cancel.Click += (_, _) => Close();
        Grid.SetColumn(cancel, 2);
        buttons.Children.Add(cancel);

        var save = new Button
        {
            Content = "保存设置",
            Height = 34,
            MinWidth = 108,
            Cursor = Cursors.Hand,
            Focusable = false
        };
        save.SetResourceReference(Button.StyleProperty, "FluentPrimaryButtonStyle");
        save.Click += (_, _) => Save();
        Grid.SetColumn(save, 4);
        buttons.Children.Add(save);

        _rootPanel.Children.Add(buttons);
    }

    /// <summary>切回设置主页面。</summary>
    private void ShowSettingsPage()
    {
        _titleText.Text = "设置";
        _backButton.Visibility = Visibility.Collapsed;
        _pageHost.Content = BuildSettingsPage();
        RefreshLayout();
    }

    /// <summary>切到「关于」二级页面。</summary>
    private void ShowAboutPage()
    {
        _titleText.Text = "关于";
        _backButton.Visibility = Visibility.Visible;
        _pageHost.Content = BuildAboutPage();
        RefreshLayout();
    }

    /// <summary>
    /// 供界面自检宿主切到关于页（不经过按钮点击）。
    /// internal 而非 public：只有 InternalsVisibleTo 的 multishot 能看到。
    /// </summary>
    internal void ShowAboutPageForTest() => ShowAboutPage();

    /// <summary>供界面自检宿主切回设置页。</summary>
    internal void ShowSettingsPageForTest() => ShowSettingsPage();

    /// <summary>
    /// 页面切换后窗口会随内容高度收缩（SizeToContent=Height）。
    /// 立刻重排一次，避免底部残留上一页的画面。
    /// </summary>
    private void RefreshLayout()
    {
        UpdateLayout();
        InvalidateVisual();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            UpdateLayout();
            InvalidateVisual();
        });
    }

    /// <summary>主设置页：分组卡片。</summary>
    private UIElement BuildSettingsPage()
    {
        var sp = new StackPanel();

        // ── 分组一：电量阈值与提醒 ──
        sp.Children.Add(CreateGroupHeader("电量阈值与提醒"));

        _lowValue = CreateValueBadge();
        _criticalValue = CreateValueBadge();

        _lowSlider = CreatePercentSlider(
            Math.Clamp(_settings.LowThreshold, 5, 50), 5, 50,
            v => _lowValue.Text = $"{v}%");
        _lowValue.Text = $"{(int)_lowSlider.Value}%";

        _criticalSlider = CreatePercentSlider(
            Math.Clamp(_settings.CriticalThreshold, 5, 30), 5, 30,
            v => _criticalValue.Text = $"{v}%",
            critical: true);
        _criticalValue.Text = $"{(int)_criticalSlider.Value}%";

        _notifyToggle = CreateToggle(_settings.NotifyEnabled);

        sp.Children.Add(WrapGroupInCard(
            CreateSliderRow("低电量提醒阈值", _lowSlider, _lowValue, "5%", "50%"),
            CreateSliderRow("严重低电量阈值", _criticalSlider, _criticalValue, "5%", "30%"),
            CreateToggleRow("启用低电量桌面通知", _notifyToggle),
            CreateNoteRow("电量持续下降时会再次提醒，同一电量不会重复提醒；充电中不会提醒。")));

        // ── 分组二：后台刷新间隔 ──
        sp.Children.Add(CreateGroupHeader("后台刷新间隔"));

        var intervalRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 12, 12, 12)
        };
        foreach (int sec in new[] { 10, 15, 30, 60, 120 })
        {
            var b = new Button
            {
                Content = sec < 60 ? $"{sec} 秒" : $"{sec / 60} 分",
                Height = 30,
                MinWidth = 62,
                Margin = new Thickness(0, 0, 6, 0),
                Tag = sec,
                Cursor = Cursors.Hand,
                Focusable = false
            };
            b.SetResourceReference(Button.StyleProperty,
                sec == _interval ? "FluentPrimaryButtonStyle" : "FluentSecondaryButtonStyle");
            b.Click += (s, _) =>
            {
                if (s is Button btn && btn.Tag is int v)
                {
                    _interval = v;
                    RefreshIntervalStyles();
                }
            };
            _intervalButtons.Add((b, sec));
            intervalRow.Children.Add(b);
        }
        sp.Children.Add(WrapGroupInCard(intervalRow));

        // ── 分组三：启用的设备来源 ──
        sp.Children.Add(CreateGroupHeader("启用的设备来源"));

        _srcLogitech = CreateToggle(_settings.EnabledSources.Contains("logitech"));
        _srcMchose = CreateToggle(_settings.EnabledSources.Contains("mchose"));
        _srcAtk = CreateToggle(_settings.EnabledSources.Contains("atk"));

        sp.Children.Add(WrapGroupInCard(
            CreateToggleRow("罗技（HID++）", _srcLogitech),
            CreateToggleRow("迈从 MCHOSE", _srcMchose),
            CreateToggleRow("ATK / VXE / VGN", _srcAtk),
            CreateNoteRow("关闭某来源可避免对其反复探测。")));

        // ── 分组四：启动 ──
        sp.Children.Add(CreateGroupHeader("启动"));
        _autostartToggle = CreateToggle(AutoStartService.IsEnabled());
        sp.Children.Add(WrapGroupInCard(
            CreateToggleRow("开机自动启动", _autostartToggle)));

        return sp;
    }

    // ───────────── 关于页 ─────────────

    private const string RepoOwner = "yixing233";
    private const string RepoName = "logi-tray";
    private const string RepoUrl = "https://github.com/" + RepoOwner + "/" + RepoName;
    private const string AuthorName = "yixing233";
    private const string LicenseName = "GPL-3.0";

    /// <summary>「关于」二级页面：图标、名称版本、作者、协议、仓库、检查更新。</summary>
    private UIElement BuildAboutPage()
    {
        _checkUpdateButton = null;
        _updateStatusText = null;

        var sp = new StackPanel();

        // ── 图标 + 名称 + 版本 ──
        var header = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 18)
        };

        var icon = new Image
        {
            Width = 72,
            Height = 72,
            // 用 Fill：StackPanel 收缩宽度时 Uniform 会把缩放算错、导致图标被裁切
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            Source = LoadAppIcon()
        };
        header.Children.Add(icon);

        var nameText = new TextBlock
        {
            Text = "multi-tray",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 2)
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimary");
        header.Children.Add(nameText);

        var versionText = new TextBlock
        {
            Text = $"版本 {AppVersion()}",
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        versionText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
        header.Children.Add(versionText);

        var descText = new TextBlock
        {
            Text = "多品牌键鼠耳机电量托盘",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        };
        descText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextFaint");
        header.Children.Add(descText);

        sp.Children.Add(header);

        // ── 信息卡：作者 / 协议 / 仓库 ──
        var infoPanel = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
        infoPanel.Children.Add(CreateInfoRow("作者", AuthorName, null, false));
        infoPanel.Children.Add(CreateInfoRow("开源协议", LicenseName, null, false));
        infoPanel.Children.Add(CreateInfoRow("仓库地址", $"{RepoOwner}/{RepoName}",
            RepoUrl, true));
        sp.Children.Add(WrapInCard(infoPanel));

        // ── 检查更新 ──
        var actionPanel = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
        var actionGrid = new Grid();
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        _checkUpdateButton = new Button
        {
            Content = "检查更新",
            FontSize = 12.5,
            Height = 32,
            MinWidth = 92,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false
        };
        _checkUpdateButton.SetResourceReference(Button.StyleProperty,
            "FluentPrimaryButtonStyle");
        _checkUpdateButton.Click += (_, _) => CheckForUpdatesAsync();
        Grid.SetColumn(_checkUpdateButton, 0);
        actionGrid.Children.Add(_checkUpdateButton);

        _updateStatusText = new TextBlock
        {
            Text = "点击检查是否有新版本",
            FontSize = 11.5,
            Margin = new Thickness(12, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        _updateStatusText.SetResourceReference(TextBlock.ForegroundProperty,
            "ThemeTextSecondary");
        Grid.SetColumn(_updateStatusText, 1);
        actionGrid.Children.Add(_updateStatusText);

        actionPanel.Children.Add(actionGrid);
        sp.Children.Add(WrapInCard(actionPanel));

        return sp;
    }

    /// <summary>一行「标签：值」，值可选渲染成可点击链接。</summary>
    private UIElement CreateInfoRow(string label, string value, string? linkUrl,
                                    bool isLast)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, isLast ? 0 : 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
        Grid.SetColumn(labelText, 0);
        grid.Children.Add(labelText);

        if (linkUrl == null)
        {
            var valueText = new TextBlock
            {
                Text = value,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            valueText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimary");
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(valueText);
        }
        else
        {
            string url = linkUrl;
            var link = new Button
            {
                Content = value,
                FontSize = 12,
                Height = 26,
                Padding = new Thickness(8, 0, 10, 0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = url,
                Focusable = false
            };
            link.SetResourceReference(Button.StyleProperty, "FluentSecondaryButtonStyle");
            link.Click += (_, _) => OpenUrl(url);
            Grid.SetColumn(link, 1);
            grid.Children.Add(link);
        }

        return grid;
    }

    /// <summary>把内容包进一张卡片（与设置页样式一致，但无底部外边距）。</summary>
    private static Border WrapInCard(UIElement child)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = child
        };
        border.SetResourceReference(Border.BackgroundProperty, "ThemeCardBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "ThemeCardBorder");
        return border;
    }

    /// <summary>取应用图标。先从磁盘读 app.png（与程序集名无关，最可靠）。</summary>
    private static System.Windows.Media.ImageSource? LoadAppIcon()
    {
        try
        {
            string png = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.png");
            if (File.Exists(png))
            {
                return System.Windows.Media.Imaging.BitmapFrame.Create(
                    new Uri(png),
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            }
        }
        catch { }

        // 退回到程序集资源（按本程序集名拼 URI，不能用 GetEntryAssembly）
        try
        {
            string asm = typeof(MultiSettingsWindow).Assembly.GetName().Name ?? "multi-tray";
            return System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri($"pack://application:,,,/{asm};component/app.png", UriKind.Absolute),
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        }
        catch { }

        return null;
    }

    /// <summary>取应用版本号（如 1.1.0）。</summary>
    private static string AppVersion()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttributes(
                typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            if (info.Length > 0 &&
                info[0] is System.Reflection.AssemblyInformationalVersionAttribute iv &&
                !string.IsNullOrWhiteSpace(iv.InformationalVersion))
            {
                string v = iv.InformationalVersion;
                int plus = v.IndexOf('+');
                return plus > 0 ? v.Substring(0, plus) : v;
            }

            var ver = asm.GetName().Version;
            return ver == null ? "1.0.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
        }
        catch
        {
            return "1.0.0";
        }
    }

    /// <summary>用默认浏览器打开链接。</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // 打不开链接不该影响其它功能
        }
    }

    /// <summary>
    /// 查询 GitHub 最新 release 并与当前版本比较。只做只读 GET，不改本地文件。
    ///
    /// 刻意不用 REST API（api.github.com）：它对未认证 IP 限 60 次/小时，很容易 403。
    /// 这里请求 releases/latest 网页，读取 302 的 Location 头拿最新 tag，不受配额限制。
    /// </summary>
    private async void CheckForUpdatesAsync()
    {
        if (_updateCheckRunning || _checkUpdateButton == null || _updateStatusText == null)
        {
            return;
        }

        _updateCheckRunning = true;
        var button = _checkUpdateButton;
        var status = _updateStatusText;

        button.IsEnabled = false;
        button.Content = "检查中…";
        status.Text = "正在查询最新版本…";

        try
        {
            string? latestTag = await FetchLatestTagAsync().ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(latestTag))
            {
                status.Text = "未能获取版本信息，请稍后重试";
                return;
            }

            string current = AppVersion();
            if (CompareVersions(latestTag.TrimStart('v', 'V'), current) > 0)
            {
                status.Text = $"发现新版本 {latestTag}";
                if (MessageBox.Show(this,
                        $"发现新版本 {latestTag}（当前 {current}）。\n\n是否前往下载页面？",
                        "multi-tray 检查更新",
                        MessageBoxButton.YesNo, MessageBoxImage.Information)
                    == MessageBoxResult.Yes)
                {
                    OpenUrl($"{RepoUrl}/releases/latest");
                }
            }
            else
            {
                status.Text = $"已是最新版本 {current}";
            }
        }
        catch
        {
            status.Text = "检查更新失败，请确认网络连接";
        }
        finally
        {
            button.Content = "检查更新";
            button.IsEnabled = true;
            _updateCheckRunning = false;
        }
    }

    /// <summary>取最新 release 的 tag。禁止自动跟随重定向，从 302 的 Location 解析。</summary>
    private static async Task<string?> FetchLatestTagAsync()
    {
        var handler = new System.Net.Http.HttpClientHandler
        {
            AllowAutoRedirect = false
        };

        using var http = new System.Net.Http.HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("multi-tray-update-check");

        using var resp = await http.GetAsync($"{RepoUrl}/releases/latest")
            .ConfigureAwait(false);

        int code = (int)resp.StatusCode;
        if (code is >= 300 and < 400 && resp.Headers.Location is Uri loc)
        {
            string path = loc.AbsolutePath.TrimEnd('/');
            int idx = path.LastIndexOf('/');
            if (idx >= 0 && idx + 1 < path.Length)
            {
                return Uri.UnescapeDataString(path.Substring(idx + 1));
            }
        }

        return null;
    }

    /// <summary>比较点分版本号，&gt;0 表示 a 更新。</summary>
    private static int CompareVersions(string a, string b)
    {
        static int[] Parts(string s) => s
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()),
                out int n) ? n : 0)
            .ToArray();

        int[] pa = Parts(a);
        int[] pb = Parts(b);
        int len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int va = i < pa.Length ? pa[i] : 0;
            int vb = i < pb.Length ? pb[i] : 0;
            if (va != vb) return va - vb;
        }
        return 0;
    }

    // ───────────── 组件构造 ─────────────

    private static TextBlock CreateGroupHeader(string text)
    {
        // 复用完整版的图标按钮样式与配色；分组标题在 DeviceCardWidgets 里有同款，
        // 但设置页需要左对齐的纯标题（不带副标题与按钮），因此这里保持一致的字号与颜色。
        var header = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 6, 0, 6)
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "ThemeGroupHeader");
        return header;
    }

    private TextBlock CreateValueBadge()
    {
        var tb = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 38,
            TextAlignment = TextAlignment.Right
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimary");
        return tb;
    }

    private static Slider CreatePercentSlider(int value, int min, int max,
                                              Action<double> onChanged,
                                              bool critical = false)
    {
        var s = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        s.SetResourceReference(StyleProperty,
            critical ? "FluentCriticalSliderStyle" : "FluentSliderStyle");
        s.ValueChanged += (_, e) => onChanged(e.NewValue);
        return s;
    }

    /// <summary>
    /// 创建开关本体。
    ///
    /// 注意：**不要把文字放进 Content**。FluentToggleSwitchStyle 的模板只渲染
    /// 一个胶囊开关、没有 ContentPresenter，Content 会被静默丢弃 ——
    /// 表现为一整排没有文字的空开关（截图核对时发现的）。
    /// 文字由 <see cref="CreateToggleRow"/> 作为独立 TextBlock 放在旁边。
    /// </summary>
    private static CheckBox CreateToggle(bool isChecked)
    {
        var cb = new CheckBox
        {
            IsChecked = isChecked,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.Hand
        };
        cb.SetResourceReference(StyleProperty, "FluentToggleSwitchStyle");
        return cb;
    }

    /// <summary>阈值行：左标题 + 右数值 + 下一行滑块 + 两端刻度。</summary>
    private static UIElement CreateSliderRow(string label, Slider slider,
                                             TextBlock value, string minText,
                                             string maxText)
    {
        var panel = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimary");
        Grid.SetColumn(title, 0);
        head.Children.Add(title);

        Grid.SetColumn(value, 1);
        head.Children.Add(value);
        panel.Children.Add(head);

        panel.Children.Add(slider);

        var range = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        range.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        range.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lo = new TextBlock { Text = minText, FontSize = 10 };
        lo.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextFaint");
        Grid.SetColumn(lo, 0);
        range.Children.Add(lo);

        var hi = new TextBlock { Text = maxText, FontSize = 10 };
        hi.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextFaint");
        Grid.SetColumn(hi, 1);
        range.Children.Add(hi);

        panel.Children.Add(range);
        return panel;
    }

    /// <summary>开关行：左侧文字标签 + 右侧开关（同一行）。</summary>
    private static UIElement CreateToggleRow(string label, CheckBox toggle)
    {
        var grid = new Grid { Margin = new Thickness(12, 11, 12, 11) };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimary");
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        return grid;
    }

    private static UIElement CreateNoteRow(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 2, 12, 10)
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextFaint");
        return tb;
    }

    /// <summary>
    /// 把同一分组的所有行装进一张卡片（整组共用一块背景）。
    ///
    /// 行与行之间**不画分隔线**：这里刻意留白分隔，视觉更干净
    /// （用户明确要求去掉分割线）。
    /// </summary>
    private static Border WrapGroupInCard(params UIElement[] rows)
    {
        var panel = new StackPanel();
        foreach (var row in rows)
        {
            panel.Children.Add(row);
        }

        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = panel,
            Margin = new Thickness(0, 0, 0, 6)
        };
        border.SetResourceReference(Border.BackgroundProperty, "ThemeCardBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "ThemeCardBorder");
        return border;
    }

    private void RefreshIntervalStyles()
    {
        foreach (var (button, sec) in _intervalButtons)
        {
            button.SetResourceReference(Button.StyleProperty,
                sec == _interval ? "FluentPrimaryButtonStyle" : "FluentSecondaryButtonStyle");
        }
    }

    // ───────────── 保存 ─────────────

    private void Save()
    {
        int low = (int)_lowSlider.Value;
        int critical = (int)_criticalSlider.Value;

        // 严重阈值必须低于普通阈值，否则提醒层级失去意义
        if (critical >= low)
        {
            MessageBox.Show(this,
                $"严重低电量阈值（{critical}%）必须小于低电量阈值（{low}%）。",
                "阈值设置有误",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        _settings.LowThreshold = low;
        _settings.CriticalThreshold = critical;
        _settings.NotifyEnabled = _notifyToggle.IsChecked == true;
        _settings.Interval = _interval;
        _settings.EnabledSources.Clear();
        if (_srcLogitech.IsChecked == true) _settings.EnabledSources.Add("logitech");
        if (_srcMchose.IsChecked == true) _settings.EnabledSources.Add("mchose");
        if (_srcAtk.IsChecked == true) _settings.EnabledSources.Add("atk");
        if (_settings.EnabledSources.Count == 0) _settings.EnabledSources.Add("logitech");

        try
        {
            if (_autostartToggle.IsChecked == true) AutoStartService.Enable();
            else AutoStartService.Disable();
            _settings.Autostart = _autostartToggle.IsChecked == true;
        }
        catch
        {
            // 自启失败不应阻止其它设置保存
        }

        // 必须在这里落盘：持久化属于本窗口的职责。
        // 依赖调用方保存会导致「直接打开设置窗口」时改动全部丢失。
        _settings.Save();

        Close();
    }
}

/// <summary>TextBlock 设置资源引用的小helper，避免每处都写两行。</summary>
internal static class FrameworkElementExtensions
{
    public static T WithResource<T>(this T element,
        System.Windows.DependencyProperty property, object key)
        where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }
}
