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
    private int _selectedInterval;
    private string _selectedStyle;
    private string _selectedThemeMode;
    private Border _rootBorder = null!;

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
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        var titleText = new TextBlock
        {
            Text = "logi-tray · 设置",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimary");
        titleBar.Children.Add(titleText);

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
        Grid.SetColumn(closeBtn, 1);
        titleBar.Children.Add(closeBtn);
        sp.Children.Add(titleBar);

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

        // 6. 底部操作按钮
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
            _config.Save();
            _onSaved?.Invoke(_config);
            Close();
        };
        btnRow.Children.Add(saveBtn);
        sp.Children.Add(btnRow);

        UpdateBadgeColors();
        _rootBorder.Child = sp;
        return _rootBorder;
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
