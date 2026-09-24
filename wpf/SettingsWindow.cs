using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Controls.Primitives;

namespace MouseBatteryTray;

/// <summary>
/// 基于原生 Win11 FluentStyles 的高对比度现代化设置窗口。
/// 特性：
///   * 不置顶（常规对话框行为，可正常被其他窗口覆盖或聚焦）
///   * 完美深色模式适配（深色亚克力磨砂底 + 纯白与亮银高对比文字，彻底告别黑上黑）
///   * 支持“跟随系统”、“浅色模式”、“深色模式”全功能实时无缝切换
///   * 支持“电池胶囊”、“环形进度”、“纯数字”三种托盘图标样式即时切换
///   * 硬件级 DWM 亚克力（SetWindowCompositionAttribute + ACCENT_ENABLE_ACRYLICBLURBEHIND）
/// </summary>
public sealed class SettingsWindow : Window
{
    private const double WindowCornerRadius = 10;

    private const string RepoOwner = "yixing233";
    private const string RepoName = "logi-tray";
    private const string RepoUrl = "https://github.com/" + RepoOwner + "/" + RepoName;
    private const string AuthorName = "yixing233";
    private const string LicenseName = "GPL-3.0";

    private readonly SettingsConfig _config;
    private readonly Action<SettingsConfig> _onSaved;
    private readonly Action<string>? _onStyleLivePreview;

    private Slider _lowSlider = null!;
    private Slider _critSlider = null!;
    private TextBlock _lowValText = null!;
    private TextBlock _critValText = null!;
    private Border _lowBadge = null!;
    private Border _critBadge = null!;
    private CheckBox _notifyToggle = null!;
    private CheckBox _acrylicToggle = null!;
    private CheckBox _autoStartToggle = null!;
    private TextBlock _autoStartDesc = null!;
    private int _selectedInterval;
    private string _selectedStyle;
    private string _selectedThemeMode;
    private Border _rootBorder = null!;

    /// <summary>设置主页面与「关于」二级页面共用的内容宿主。</summary>
    private ContentControl _pageHost = null!;
    private TextBlock _titleText = null!;
    private Button _backButton = null!;

    /// <summary>关于页上的「检查更新」按钮与其状态文字。</summary>
    private Button? _checkUpdateButton;
    private TextBlock? _updateStatusText;
    private bool _updateCheckRunning;

    private readonly List<Button> _intervalButtons = new();
    private readonly List<Button> _styleButtons = new();
    private readonly List<Button> _themeButtons = new();

    public SettingsWindow(SettingsConfig config, Action<SettingsConfig> onSaved, Action<string>? onStyleLivePreview = null)
    {
        _config = config;
        _onSaved = onSaved;
        _onStyleLivePreview = onStyleLivePreview;
        _selectedInterval = config.Interval;
        _selectedStyle = string.IsNullOrEmpty(config.TrayIconStyle) ? "battery" : config.TrayIconStyle;
        _selectedThemeMode = string.IsNullOrEmpty(config.ThemeMode) ? "system" : config.ThemeMode;

        Width = 410;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = false;
        ShowInTaskbar = true;
        Title = "logi-tray · 设置";
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        try
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/logi-tray;component/app.ico", UriKind.Absolute),
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        }
        catch { }

        // 载入具备绝对清晰度、防白化悬停的 FluentStyles 资源字典
        try
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/logi-tray;component/FluentStyles.xaml", UriKind.Absolute)
            });
        }
        catch { }

        // 初始化当前深浅主题画刷资源
        ThemeService.ApplyThemeResources(Resources);

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(WindowCornerRadius),
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0)
        });

        Content = BuildUi();

        SourceInitialized += (_, _) =>
        {
            ThemeService.ApplyAcrylicBackdrop(this);
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (System.IO.File.Exists(icoPath))
                {
                    IntPtr hIconBig = UnmanagedMethods.LoadImage(IntPtr.Zero, icoPath, 1, 48, 48, 0x0010);
                    if (hIconBig == IntPtr.Zero)
                        hIconBig = UnmanagedMethods.LoadImage(IntPtr.Zero, icoPath, 1, 32, 32, 0x0010);
                    IntPtr hIconSmall = UnmanagedMethods.LoadImage(IntPtr.Zero, icoPath, 1, 16, 16, 0x0010);

                    if (hIconBig != IntPtr.Zero)
                        UnmanagedMethods.SendMessage(hwnd, UnmanagedMethods.WM_SETICON, (IntPtr)UnmanagedMethods.ICON_BIG, hIconBig);
                    if (hIconSmall != IntPtr.Zero)
                        UnmanagedMethods.SendMessage(hwnd, UnmanagedMethods.WM_SETICON, (IntPtr)UnmanagedMethods.ICON_SMALL, hIconSmall);
                }
            }
            catch { }
        };
        ContentRendered += (_, _) => ThemeService.ApplyAcrylicBackdrop(this);

        ThemeService.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        Dispatcher.Invoke(() =>
        {
            ThemeService.ApplyAcrylicBackdrop(this);
            ThemeService.ApplyThemeResources(Resources);

            if (_rootBorder != null)
            {
                _rootBorder.BorderBrush = ThemeService.BorderBrush;
                _rootBorder.Background = ThemeService.IsAcrylicEnabled ? Brushes.Transparent : ThemeService.SolidSurfaceBrush;
            }

            UpdateThemeButtonStyles();
            UpdateStyleButtonStyles();
            UpdateIntervalButtonStyles();
            UpdateBadgeColors();
        });
    }

    private UIElement BuildUi()
    {
        _rootBorder = new Border
        {
            CornerRadius = new CornerRadius(WindowCornerRadius),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeService.BorderBrush,
            Background = ThemeService.IsAcrylicEnabled ? Brushes.Transparent : ThemeService.SolidSurfaceBrush,
            Padding = new Thickness(22, 16, 22, 20)
        };

        var sp = new StackPanel();

        // 1. 标题栏 (支持点击拖拽窗口)
        var titleBar = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        // 返回按钮：仅二级页面显示（主页面时折叠，标题左移）
        _backButton = new Button
        {
            Content = LucideIcons.ArrowLeft,
            FontFamily = ThemeService.LucideFont,
            FontSize = 14,
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            Style = (Style)FindResource("FluentSecondaryButtonStyle")
        };
        _backButton.Click += (_, _) => ShowSettingsPage();
        Grid.SetColumn(_backButton, 0);
        titleBar.Children.Add(_backButton);

        _titleText = new TextBlock
        {
            Text = "logi-tray · 设置",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _titleText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimary");
        Grid.SetColumn(_titleText, 1);
        titleBar.Children.Add(_titleText);

        var closeBtn = new Button
        {
            Content = LucideIcons.X,
            FontFamily = ThemeService.LucideFont,
            FontSize = 13,
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)FindResource("FluentSecondaryButtonStyle")
        };
        closeBtn.Click += (_, _) => Close();
        Grid.SetColumn(closeBtn, 2);
        titleBar.Children.Add(closeBtn);
        sp.Children.Add(titleBar);

        // 2. 页面宿主：主设置页 / 关于页 在此切换
        _pageHost = new ContentControl { Content = BuildSettingsPage() };
        sp.Children.Add(_pageHost);

        UpdateBadgeColors();
        _rootBorder.Child = sp;
        return _rootBorder;
    }

    /// <summary>切回设置主页面。</summary>
    private void ShowSettingsPage()
    {
        _titleText.Text = "logi-tray · 设置";
        _backButton.Visibility = Visibility.Collapsed;
        _pageHost.Content = BuildSettingsPage();
        UpdateBadgeColors();
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
    /// 页面切换后窗口会随内容高度收缩（SizeToContent=Height）。
    /// 立刻重排并重绘，避免底部残留上一页的画面。
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

    private UIElement BuildSettingsPage()
    {
        var sp = new StackPanel();

        // 2. 分组标题：电量阈值与提醒
        var grp1 = new TextBlock
        {
            Text = "电量阈值与提醒",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        grp1.SetResourceReference(TextBlock.ForegroundProperty, "ThemeGroupHeader");
        sp.Children.Add(grp1);

        // 卡片 1：低电量阈值
        var cardLow = CreateCardContainer();
        var lowHeader = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        var lowTitle = new TextBlock
        {
            Text = "低电量提醒阈值",
            FontSize = 12.5,
            FontWeight = FontWeights.Medium
        };
        lowTitle.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimary");
        lowHeader.Children.Add(lowTitle);

        _lowBadge = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 1, 7, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        _lowValText = new TextBlock
        {
            Text = $"{_config.LowThreshold}%",
            FontSize = 12,
            FontWeight = FontWeights.Bold
        };
        _lowBadge.Child = _lowValText;
        lowHeader.Children.Add(_lowBadge);
        cardLow.Children.Add(lowHeader);

        _lowSlider = new Slider
        {
            Minimum = 10,
            Maximum = 50,
            TickFrequency = 1,
            IsSnapToTickEnabled = false,
            Value = _config.LowThreshold,
            Style = (Style)FindResource("FluentSliderStyle"),
            Margin = new Thickness(0, 4, 0, 0)
        };
        _lowSlider.ValueChanged += (_, e) =>
        {
            int val = (int)Math.Round(e.NewValue);
            if (_lowValText != null)
                _lowValText.Text = $"{val}%";
            if (_critSlider != null && _critSlider.Value >= val)
                _critSlider.Value = Math.Max(5, val - 5);
        };
        cardLow.Children.Add(_lowSlider);

        var lowRangeGrid = new Grid { Margin = new Thickness(2, 4, 2, 0) };
        var lowMin = new TextBlock { Text = "10%", FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Left };
        lowMin.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
        lowRangeGrid.Children.Add(lowMin);

        var lowMax = new TextBlock { Text = "50%", FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        lowMax.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
        lowRangeGrid.Children.Add(lowMax);
        cardLow.Children.Add(lowRangeGrid);

        sp.Children.Add(WrapInCardBorder(cardLow));

        // 卡片 2：严重低电量阈值
        var cardCrit = CreateCardContainer();
        var critHeader = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        var critTitle = new TextBlock
        {
            Text = "严重低电量警告阈值",
            FontSize = 12.5,
            FontWeight = FontWeights.Medium
        };
        critTitle.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimary");
        critHeader.Children.Add(critTitle);

        _critBadge = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 1, 7, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        _critValText = new TextBlock
        {
            Text = $"{_config.CriticalThreshold}%",
            FontSize = 12,
            FontWeight = FontWeights.Bold
        };
        _critBadge.Child = _critValText;
        critHeader.Children.Add(_critBadge);
        cardCrit.Children.Add(critHeader);

        _critSlider = new Slider
        {
            Minimum = 5,
            Maximum = 30,
            TickFrequency = 1,
            IsSnapToTickEnabled = false,
            Value = _config.CriticalThreshold,
            Style = (Style)FindResource("FluentCriticalSliderStyle"),
            Margin = new Thickness(0, 4, 0, 0)
        };
        _critSlider.ValueChanged += (_, e) =>
        {
            int val = (int)Math.Round(e.NewValue);
            if (_critValText != null)
                _critValText.Text = $"{val}%";
            if (_lowSlider != null && _lowSlider.Value <= val)
                _lowSlider.Value = Math.Min(50, val + 5);
        };
        cardCrit.Children.Add(_critSlider);

        var critRangeGrid = new Grid { Margin = new Thickness(2, 4, 2, 0) };
        var critMin = new TextBlock { Text = "5%", FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Left };
        critMin.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
        critRangeGrid.Children.Add(critMin);

        var critMax = new TextBlock { Text = "30%", FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        critMax.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
        critRangeGrid.Children.Add(critMax);
        cardCrit.Children.Add(critRangeGrid);

        sp.Children.Add(WrapInCardBorder(cardCrit));

        // 卡片 3：低电量桌面通知
        var notifyCardGrid = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        notifyCardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        notifyCardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var notifyTextPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var notifyTitle = new TextBlock
        {
            Text = "启用低电量桌面通知",
            FontSize = 12.5,
            FontWeight = FontWeights.Medium
        };
        notifyTitle.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimary");
        notifyTextPanel.Children.Add(notifyTitle);

        var notifyDesc = new TextBlock
        {
            Text = "电量低于设定阈值时在系统右下角弹出提示",
            FontSize = 10.5,
            Margin = new Thickness(0, 2, 0, 0)
        };
        notifyDesc.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
        notifyTextPanel.Children.Add(notifyDesc);
        notifyCardGrid.Children.Add(notifyTextPanel);

        _notifyToggle = new CheckBox
        {
            IsChecked = _config.NotifyEnabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)FindResource("FluentToggleSwitchStyle")
        };
        Grid.SetColumn(_notifyToggle, 1);
        notifyCardGrid.Children.Add(_notifyToggle);

        var notifyBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = notifyCardGrid,
            Margin = new Thickness(0, 0, 0, 14)
        };
        notifyBorder.SetResourceReference(Border.BackgroundProperty, "ThemeCardBackground");
        notifyBorder.SetResourceReference(Border.BorderBrushProperty, "ThemeCardBorder");
        sp.Children.Add(notifyBorder);

        // 3. 分组：托盘图标样式 (电池、环形、纯数字)
        var grp2 = new TextBlock
        {
            Text = "托盘图标样式",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        grp2.SetResourceReference(TextBlock.ForegroundProperty, "ThemeGroupHeader");
        sp.Children.Add(grp2);

        var styleGrid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 14) };
        var styleOptions = new (string key, string icon, string label)[]
        {
            ("battery", LucideIcons.Battery, "电池胶囊"),
            ("ring", LucideIcons.Circle, "环形进度"),
            ("number", LucideIcons.Hash, "纯数字")
        };

        foreach (var (key, icon, label) in styleOptions)
        {
            var btn = new Button
            {
                Content = CreateButtonLabelWithLucide(icon, label),
                Height = 32,
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                Tag = key
            };
            btn.Click += (s, _) =>
            {
                _selectedStyle = key;
                UpdateStyleButtonStyles();
                _onStyleLivePreview?.Invoke(key);
            };
            _styleButtons.Add(btn);
            styleGrid.Children.Add(btn);
        }
        UpdateStyleButtonStyles();
        sp.Children.Add(styleGrid);

        // 4. 分组：外观主题 (跟随系统、浅色模式、深色模式)
        var grp3 = new TextBlock
        {
            Text = "外观主题",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        grp3.SetResourceReference(TextBlock.ForegroundProperty, "ThemeGroupHeader");
        sp.Children.Add(grp3);

        var themeGrid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 14) };
        var themeOptions = new (string key, string icon, string label)[]
        {
            ("system", LucideIcons.Monitor, "跟随系统"),
            ("light", LucideIcons.Sun, "浅色模式"),
            ("dark", LucideIcons.Moon, "深色模式")
        };

        foreach (var (key, icon, label) in themeOptions)
        {
            var btn = new Button
            {
                Content = CreateButtonLabelWithLucide(icon, label),
                Height = 32,
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                Tag = key
            };
            btn.Click += (s, _) =>
            {
                _selectedThemeMode = key;
                UpdateThemeButtonStyles();
                ThemeService.SetThemeMode(key);
            };
            _themeButtons.Add(btn);
            themeGrid.Children.Add(btn);
        }
        UpdateThemeButtonStyles();
        sp.Children.Add(themeGrid);

        // 亚克力效果切换卡片
        var acrylicCardGrid = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        acrylicCardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        acrylicCardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var acrylicTextPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var acrylicTitle = new TextBlock
        {
            Text = "启用亚克力毛玻璃背景",
            FontSize = 12.5,
            FontWeight = FontWeights.Medium
        };
        acrylicTitle.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimary");
        acrylicTextPanel.Children.Add(acrylicTitle);

        var acrylicDesc = new TextBlock
        {
            Text = "呈现 Windows 11 DWM 实时虚化；关闭后采用极简纯色底板",
            FontSize = 10.5,
            Margin = new Thickness(0, 2, 0, 0)
        };
        acrylicDesc.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
        acrylicTextPanel.Children.Add(acrylicDesc);
        acrylicCardGrid.Children.Add(acrylicTextPanel);

        _acrylicToggle = new CheckBox
        {
            IsChecked = _config.AcrylicEnabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)FindResource("FluentToggleSwitchStyle")
        };
        _acrylicToggle.Checked += (_, _) => ThemeService.SetAcrylicEnabled(true);
        _acrylicToggle.Unchecked += (_, _) => ThemeService.SetAcrylicEnabled(false);
        Grid.SetColumn(_acrylicToggle, 1);
        acrylicCardGrid.Children.Add(_acrylicToggle);

        var acrylicBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = acrylicCardGrid,
            Margin = new Thickness(0, 0, 0, 14)
        };
        acrylicBorder.SetResourceReference(Border.BackgroundProperty, "ThemeCardBackground");
        acrylicBorder.SetResourceReference(Border.BorderBrushProperty, "ThemeCardBorder");
        sp.Children.Add(acrylicBorder);

        // 开机自启卡片
        var autoStartCardGrid = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        autoStartCardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        autoStartCardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var autoStartTextPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var autoStartTitle = new TextBlock
        {
            Text = "开机自动启动",
            FontSize = 12.5,
            FontWeight = FontWeights.Medium
        };
        autoStartTitle.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimary");
        autoStartTextPanel.Children.Add(autoStartTitle);

        _autoStartDesc = new TextBlock
        {
            Text = AutoStartService.IsEnabled()
                ? "登录 Windows 后自动在后台运行，无需手动打开"
                : "登录 Windows 后不会自动运行，需手动启动",
            FontSize = 10.5,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _autoStartDesc.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
        autoStartTextPanel.Children.Add(_autoStartDesc);
        autoStartCardGrid.Children.Add(autoStartTextPanel);

        _autoStartToggle = new CheckBox
        {
            IsChecked = AutoStartService.IsEnabled(),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)FindResource("FluentToggleSwitchStyle")
        };
        _autoStartToggle.Checked += (_, _) => UpdateAutoStartState(true);
        _autoStartToggle.Unchecked += (_, _) => UpdateAutoStartState(false);
        Grid.SetColumn(_autoStartToggle, 1);
        autoStartCardGrid.Children.Add(_autoStartToggle);

        var autoStartBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = autoStartCardGrid,
            Margin = new Thickness(0, 0, 0, 14)
        };
        autoStartBorder.SetResourceReference(Border.BackgroundProperty, "ThemeCardBackground");
        autoStartBorder.SetResourceReference(Border.BorderBrushProperty, "ThemeCardBorder");
        sp.Children.Add(autoStartBorder);

        // 5. 分组：后台刷新间隔
        var grp4 = new TextBlock
        {
            Text = "后台刷新间隔",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        grp4.SetResourceReference(TextBlock.ForegroundProperty, "ThemeGroupHeader");
        sp.Children.Add(grp4);

        var intervalGrid = new UniformGrid { Columns = 5, Margin = new Thickness(0, 0, 0, 20) };
        var intervals = new (int sec, string label)[]
        {
            (10, "10 秒"),
            (15, "15 秒"),
            (30, "30 秒"),
            (60, "1 分"),
            (120, "2 分")
        };

        foreach (var (sec, label) in intervals)
        {
            var btn = new Button
            {
                Content = label,
                FontSize = 11.5,
                Height = 32,
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                Tag = sec
            };
            btn.Click += (s, _) =>
            {
                _selectedInterval = sec;
                UpdateIntervalButtonStyles();
            };
            _intervalButtons.Add(btn);
            intervalGrid.Children.Add(btn);
        }
        UpdateIntervalButtonStyles();
        sp.Children.Add(intervalGrid);

        // 6. 底部操作按钮：左侧「关于」入口，右侧取消 / 保存
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var aboutBtn = new Button
        {
            Content = CreateButtonLabelWithLucide(LucideIcons.Info, "关于"),
            FontSize = 12.5,
            Height = 32,
            Padding = new Thickness(12, 0, 14, 0),
            Cursor = Cursors.Hand,
            Style = (Style)FindResource("FluentSecondaryButtonStyle"),
            VerticalAlignment = VerticalAlignment.Center
        };
        aboutBtn.Click += (_, _) => ShowAboutPage();
        Grid.SetColumn(aboutBtn, 0);
        footer.Children.Add(aboutBtn);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelBtn = new Button
        {
            Content = "取消",
            FontSize = 12.5,
            Width = 76,
            Height = 32,
            Margin = new Thickness(0, 0, 10, 0),
            Style = (Style)FindResource("FluentSecondaryButtonStyle"),
            Cursor = Cursors.Hand
        };
        cancelBtn.Click += (_, _) => Close();
        btnRow.Children.Add(cancelBtn);

        var saveBtn = new Button
        {
            Content = "保存设置",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Width = 88,
            Height = 32,
            Style = (Style)FindResource("FluentPrimaryButtonStyle"),
            Cursor = Cursors.Hand
        };
        saveBtn.Click += (_, _) =>
        {
            _config.LowThreshold = (int)_lowSlider.Value;
            _config.CriticalThreshold = (int)_critSlider.Value;
            _config.NotifyEnabled = _notifyToggle.IsChecked ?? true;
            _config.Interval = _selectedInterval;
            _config.TrayIconStyle = _selectedStyle;
            _config.ThemeMode = _selectedThemeMode;
            _config.AcrylicEnabled = _acrylicToggle.IsChecked ?? true;
            _config.Autostart = _autoStartToggle.IsChecked ?? false;
            _config.Save();

            // 立即把启动项写入/移出注册表
            bool applied = AutoStartService.Apply(_config.Autostart);
            if (!applied)
            {
                System.Windows.MessageBox.Show(this,
                    "写入开机启动项失败，可能是注册表权限受限。\n" +
                    "程序设置已保存，但开机自启可能不会生效。",
                    "logi-tray", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _onSaved?.Invoke(_config);
            Close();
        };
        btnRow.Children.Add(saveBtn);
        Grid.SetColumn(btnRow, 1);
        footer.Children.Add(btnRow);
        sp.Children.Add(footer);

        return sp;
    }

    /// <summary>
    /// 「关于」二级页面：应用图标、名称与版本、作者、仓库地址、检查更新。
    /// </summary>
    private UIElement BuildAboutPage()
    {
        _checkUpdateButton = null;
        _updateStatusText = null;

        var sp = new StackPanel();

        // --- 应用图标 + 名称 + 版本 ---
        var header = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 18)
        };

        var iconImage = new System.Windows.Controls.Image
        {
            Width = 72,
            Height = 72,
            // 用 Fill 避免 StackPanel 收缩宽度时把 Uniform 缩放算错、导致图标被裁切
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        try
        {
            iconImage.Source = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/logi-tray;component/app.png", UriKind.Absolute),
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        }
        catch
        {
            // 图标加载失败时退回 .ico，避免整页报错
            try
            {
                iconImage.Source = System.Windows.Media.Imaging.BitmapFrame.Create(
                    new Uri("pack://application:,,,/logi-tray;component/app.ico", UriKind.Absolute),
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            }
            catch { }
        }
        header.Children.Add(iconImage);

        var nameText = new TextBlock
        {
            Text = "logi-tray",
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

        sp.Children.Add(header);

        // --- 信息卡：作者 / 开源协议 / 仓库地址 ---
        var infoPanel = CreateCardContainer();

        infoPanel.Children.Add(CreateInfoRow("作者", AuthorName, linkUrl: null));
        infoPanel.Children.Add(CreateInfoRow("开源协议", LicenseName, linkUrl: null));
        infoPanel.Children.Add(CreateInfoRow("仓库地址", $"{RepoOwner}/{RepoName}",
                                             linkUrl: RepoUrl, isLast: true));

        var infoCard = WrapInCardBorder(infoPanel);
        sp.Children.Add(infoCard);

        // --- 检查更新 ---
        var actionPanel = CreateCardContainer();

        var actionGrid = new Grid();
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _checkUpdateButton = new Button
        {
            Content = CreateButtonLabelWithLucide(LucideIcons.Refresh, "检查更新"),
            FontSize = 12.5,
            Height = 32,
            Padding = new Thickness(12, 0, 14, 0),
            Cursor = Cursors.Hand,
            Style = (Style)FindResource("FluentPrimaryButtonStyle"),
            VerticalAlignment = VerticalAlignment.Center
        };
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
        _updateStatusText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
        Grid.SetColumn(_updateStatusText, 1);
        actionGrid.Children.Add(_updateStatusText);

        actionPanel.Children.Add(actionGrid);
        sp.Children.Add(WrapInCardBorder(actionPanel));

        return sp;
    }

    /// <summary>取应用版本号（如 1.0.1）。</summary>
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
                // 去掉 +commit 之类的构建后缀
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

    /// <summary>一行「标签：值」，可选把值渲染成可点击链接。</summary>
    private UIElement CreateInfoRow(string label, string value, string? linkUrl, bool isLast = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, isLast ? 0 : 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

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
                Content = CreateButtonLabelWithLucide(LucideIcons.ExternalLink, value),
                FontSize = 12,
                Height = 26,
                Padding = new Thickness(8, 0, 10, 0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = url,
                Style = (Style)FindResource("FluentSecondaryButtonStyle")
            };
            link.Click += (_, _) => OpenUrl(url);
            Grid.SetColumn(link, 1);
            grid.Children.Add(link);
        }

        return grid;
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
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"无法打开链接：{url}\n\n{ex.Message}",
                "logi-tray", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 查询 GitHub 最新 release 并与当前版本比较。
    /// 只做只读 GET，不改动任何本地文件。
    ///
    /// 这里刻意不使用 REST API（api.github.com）：它对本机 IP 限制 60 次/小时，
    /// 未认证用户很容易触发 403。改为请求 releases/latest 网页并读取 302 的
    /// Location 头，同样能拿到最新 tag，且不受该配额限制。
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
        button.Content = CreateButtonLabelWithLucide(LucideIcons.Refresh, "检查中...");
        status.Text = "正在查询最新版本...";

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
                if (System.Windows.MessageBox.Show(this,
                        $"发现新版本 {latestTag}（当前 {current}）。\n\n是否前往下载页面？",
                        "logi-tray 检查更新",
                        MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    OpenUrl($"{RepoUrl}/releases/latest");
                }
            }
            else
            {
                status.Text = $"已是最新版本 {current}";
            }
        }
        catch (Exception ex)
        {
            status.Text = "检查更新失败，请确认网络连接";
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            button.Content = CreateButtonLabelWithLucide(LucideIcons.Refresh, "检查更新");
            button.IsEnabled = true;
            _updateCheckRunning = false;
        }
    }

    /// <summary>
    /// 取最新 release 的 tag。请求 releases/latest，禁止自动跟随重定向，
    /// 从 302 的 Location 头解析出形如 .../releases/tag/v1.0.1 的版本号。
    /// </summary>
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
        http.DefaultRequestHeaders.UserAgent.ParseAdd("logi-tray-update-check");

        using var resp = await http.GetAsync($"{RepoUrl}/releases/latest").ConfigureAwait(false);

        int code = (int)resp.StatusCode;
        if (code is >= 300 and < 400 &&
            resp.Headers.Location is Uri loc)
        {
            string path = loc.AbsolutePath.TrimEnd('/');
            int idx = path.LastIndexOf('/');
            if (idx >= 0 && idx + 1 < path.Length)
            {
                return Uri.UnescapeDataString(path.Substring(idx + 1));
            }
        }

        if (resp.IsSuccessStatusCode)
        {
            // 没有重定向时说明该仓库还没有任何 release
            return null;
        }

        return null;
    }

    /// <summary>比较点分版本号，返回 a 相对 b 的大小（&gt;0 表示 a 更新）。</summary>
    private static int CompareVersions(string a, string b)
    {
        static int[] Parts(string s) => s.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out int n) ? n : 0)
            .ToArray();

        int[] pa = Parts(a);
        int[] pb = Parts(b);
        int len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int va = i < pa.Length ? pa[i] : 0;
            int vb = i < pb.Length ? pb[i] : 0;
            if (va != vb)
            {
                return va - vb;
            }
        }
        return 0;
    }

    private static StackPanel CreateCardContainer() => new()
    {
        Margin = new Thickness(12, 10, 12, 10)
    };

    private static Border WrapInCardBorder(UIElement child)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = child,
            Margin = new Thickness(0, 0, 0, 8)
        };
        border.SetResourceReference(Border.BackgroundProperty, "ThemeCardBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "ThemeCardBorder");
        return border;
    }

    private void UpdateStyleButtonStyles()
    {
        foreach (var btn in _styleButtons)
        {
            string key = (string)btn.Tag;
            bool selected = key == _selectedStyle;
            btn.Style = (Style)FindResource(selected ? "FluentPrimaryButtonStyle" : "FluentSecondaryButtonStyle");
        }
    }

    private void UpdateThemeButtonStyles()
    {
        foreach (var btn in _themeButtons)
        {
            string key = (string)btn.Tag;
            bool selected = key == _selectedThemeMode;
            btn.Style = (Style)FindResource(selected ? "FluentPrimaryButtonStyle" : "FluentSecondaryButtonStyle");
        }
    }

    private void UpdateIntervalButtonStyles()
    {
        foreach (var btn in _intervalButtons)
        {
            int sec = (int)btn.Tag;
            bool selected = sec == _selectedInterval;
            btn.Style = (Style)FindResource(selected ? "FluentPrimaryButtonStyle" : "FluentSecondaryButtonStyle");
        }
    }

    private static StackPanel CreateButtonLabelWithLucide(string iconGlyph, string labelText)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        sp.Children.Add(new TextBlock
        {
            Text = iconGlyph,
            FontFamily = ThemeService.LucideFont,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        });

        sp.Children.Add(new TextBlock
        {
            Text = labelText,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        });

        return sp;
    }

    /// <summary>
    /// 勾选/取消开机自启时立即写入或移除注册表启动项，并刷新说明文字。
    /// </summary>
    private void UpdateAutoStartState(bool enabled)
    {
        bool ok = AutoStartService.Apply(enabled);

        if (_autoStartDesc != null)
        {
            _autoStartDesc.Text = enabled
                ? (ok ? "登录 Windows 后自动在后台运行，无需手动打开"
                      : "⚠ 写入启动项失败，请检查注册表权限")
                : "登录 Windows 后不会自动运行，需手动启动";
        }
    }

    private void UpdateBadgeColors()
    {
        bool dark = ThemeService.CurrentTheme == AppTheme.Dark;

        if (_lowBadge != null && _lowValText != null)
        {
            _lowBadge.Background = dark
                ? new SolidColorBrush(Color.FromArgb(46, 56, 189, 248))
                : new SolidColorBrush(Color.FromArgb(28, 2, 132, 199));
            _lowBadge.BorderBrush = dark
                ? new SolidColorBrush(Color.FromArgb(90, 56, 189, 248))
                : new SolidColorBrush(Color.FromArgb(60, 2, 132, 199));
            _lowValText.Foreground = dark
                ? new SolidColorBrush(Color.FromRgb(56, 189, 248))
                : new SolidColorBrush(Color.FromRgb(2, 132, 199));
        }

        if (_critBadge != null && _critValText != null)
        {
            _critBadge.Background = dark
                ? new SolidColorBrush(Color.FromArgb(50, 244, 63, 94))
                : new SolidColorBrush(Color.FromArgb(28, 225, 29, 72));
            _critBadge.BorderBrush = dark
                ? new SolidColorBrush(Color.FromArgb(100, 244, 63, 94))
                : new SolidColorBrush(Color.FromArgb(70, 225, 29, 72));
            _critValText.Foreground = dark
                ? new SolidColorBrush(Color.FromRgb(251, 113, 133))
                : new SolidColorBrush(Color.FromRgb(190, 18, 60)); // 醒目玫瑰深红 #BE123C
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }
}
