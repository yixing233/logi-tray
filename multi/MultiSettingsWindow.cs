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
        // ── 顶部标题栏 ──
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "设置",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimary");
        Grid.SetColumn(title, 0);
        header.Children.Add(title);

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
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);

        _rootPanel.Children.Add(header);

        // ── 分组一：电量阈值与提醒 ──
        _rootPanel.Children.Add(CreateGroupHeader("电量阈值与提醒"));

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

        _notifyToggle = CreateToggle("启用低电量桌面通知", _settings.NotifyEnabled);

        _rootPanel.Children.Add(WrapGroupInCard(
            CreateSliderRow("低电量提醒阈值", _lowSlider, _lowValue, "5%", "50%"),
            CreateSliderRow("严重低电量阈值", _criticalSlider, _criticalValue, "5%", "30%"),
            CreateToggleRow(_notifyToggle),
            CreateNoteRow("电量持续下降时会再次提醒，同一电量不会重复提醒；充电中不会提醒。")));

        // ── 分组二：后台刷新间隔 ──
        _rootPanel.Children.Add(CreateGroupHeader("后台刷新间隔"));

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
        _rootPanel.Children.Add(WrapGroupInCard(intervalRow));

        // ── 分组三：启用的设备来源 ──
        _rootPanel.Children.Add(CreateGroupHeader("启用的设备来源"));

        _srcLogitech = CreateToggle("罗技（HID++）",
            _settings.EnabledSources.Contains("logitech"));
        _srcMchose = CreateToggle("迈从 MCHOSE",
            _settings.EnabledSources.Contains("mchose"));
        _srcAtk = CreateToggle("ATK / VXE / VGN",
            _settings.EnabledSources.Contains("atk"));

        _rootPanel.Children.Add(WrapGroupInCard(
            CreateToggleRow(_srcLogitech),
            CreateToggleRow(_srcMchose),
            CreateToggleRow(_srcAtk),
            CreateNoteRow("关闭某来源可避免对其反复探测。")));

        // ── 分组四：启动 ──
        _rootPanel.Children.Add(CreateGroupHeader("启动"));
        _autostartToggle = CreateToggle("开机自动启动", AutoStartService.IsEnabled());
        _rootPanel.Children.Add(WrapGroupInCard(CreateToggleRow(_autostartToggle)));

        // ── 底部按钮 ──
        var buttons = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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
        Grid.SetColumn(cancel, 0);
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
        Grid.SetColumn(save, 2);
        buttons.Children.Add(save);

        _rootPanel.Children.Add(buttons);
    }

    // ───────────── 组件构造 ─────────────

    private static TextBlock CreateGroupHeader(string text)
    {
        // 复用完整版的图标按钮样式与配色；分组标题在 AcrylicWidgets 里有同款，
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

    private static CheckBox CreateToggle(string text, bool isChecked)
    {
        var cb = new CheckBox
        {
            Content = text,
            IsChecked = isChecked,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
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

    private static UIElement CreateToggleRow(CheckBox toggle)
    {
        toggle.Margin = new Thickness(12, 11, 12, 11);
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        return toggle;
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

    /// <summary>把同一分组的所有行装进一张卡片（整组共用一块背景）。</summary>
    private static Border WrapGroupInCard(params UIElement[] rows)
    {
        var panel = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                var divider = new Border
                {
                    Height = 1,
                    Margin = new Thickness(12, 0, 12, 0)
                };
                divider.SetResourceReference(Border.BackgroundProperty, "ThemeCardBorder");
                panel.Children.Add(divider);
            }
            panel.Children.Add(rows[i]);
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
