using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using WpfAlign = System.Windows.HorizontalAlignment;

namespace MouseBatteryTray;

/// <summary>
/// 极简 Windows 11 Fluent 亚克力悬浮电量详情卡片。
/// 核心特性：
///   * 真正的硬件加速 DWM 亚克力（SetWindowCompositionAttribute + ACCENT_ENABLE_ACRYLICBLURBEHIND）
///   * 极简优雅的圆角卡片布局（宽度 232，CornerRadius 8，细微半透明边框）
///   * 顶部内置快捷“⚙ 设置”图标按钮，一键打开配置
///   * 无窗口边框、DirectWrite 高清字体、无抗锯齿模糊
///   * 任务栏上方智能浮动对齐，鼠标点击外部区域自动平滑收起
///   * 全程 0% CPU 占用，无需抓屏与离线着色
/// </summary>
public sealed class MouseBatteryDetailsWindow : Window
{
    private const double CardWidth = 232;
    private const double CardCornerRadius = 8;

    /// <summary>卡片底边与任务栏之间的留白（WPF 单位）。</summary>
    private const int SpacingAboveTaskbar = 8;

    private readonly Border _cardBorder;
    private readonly StackPanel _rootPanel;
    private readonly DispatcherTimer _outsideClickTimer;
    private readonly Action? _onOpenSettings;
    private BatterySnapshot? _lastSnapshot;

    /// <summary>最近一次真正渲染到视觉树里的快照，用于避免重复重建。</summary>
    private BatterySnapshot? _renderedSnapshot;
    private bool _wasPrimaryButtonDown;
    private bool _adjustingPosition;

    /// <summary>当前锚点的屏幕 X（物理像素）与是否已有有效锚点。</summary>
    private double _anchorX;
    private bool _hasAnchor;

    public MouseBatteryDetailsWindow(Action? onOpenSettings = null)
    {
        _onOpenSettings = onOpenSettings;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Topmost = true;
        Width = CardWidth;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(CardCornerRadius),
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0)
        });

        // 加载 FluentStyles 并注入当前主题画刷。
        // 卡片里部分文字用 SetResourceReference("ThemeTextSecondary") 动态绑定，
        // 若不加载这些资源，WPF 会回退到默认黑色前景 —— 深色模式下就成了黑字。
        try
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/logi-tray;component/FluentStyles.xaml", UriKind.Absolute)
            });
        }
        catch { }

        ThemeService.ApplyThemeResources(Resources);

        _rootPanel = new StackPanel
        {
            Margin = new Thickness(14, 10, 14, 12)
        };

        _cardBorder = new Border
        {
            CornerRadius = new CornerRadius(CardCornerRadius),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeService.BorderBrush,
            Background = Brushes.Transparent, // 保持纯透明：激活 DWM 硬件级亚克力实时虚化
            Width = CardWidth,
            Child = _rootPanel
        };

        Content = _cardBorder;

        SourceInitialized += (_, _) =>
        {
            ThemeService.ApplyAcrylicBackdrop(this);
            // 窗口句柄创建后才能取得目标 DPI；首次 Show 前的预定位再按实际比例校正。
            AdjustVerticalPosition();
        };

        // ContentRendered 在一次窗口生命周期内只会触发一次，仅靠它校正会让
        // 「第二次从托盘菜单打开」沿用上一次的定位。这里改为多路兜底：
        // SizeChanged 覆盖内容高度变化，IsVisibleChanged 覆盖每一次重新显示。
        ContentRendered += (_, _) => ThemeService.ApplyAcrylicBackdrop(this);
        SizeChanged += (_, _) => AdjustVerticalPosition();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, AdjustVerticalPosition);
            }
        };

        _outsideClickTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _outsideClickTimer.Tick += OutsideClickTimer_Tick;

        ThemeService.ThemeChanged += OnThemeChanged;
    }

    /// <summary>
    /// 重新按当前真实高度摆放卡片。每次重新显示 / 内容高度变化后都要调用，
    /// 否则会沿用上一次的 Top；同时它也是唯一写入 Left/Top 的地方，
    /// 保证面板避让不会被「贴任务栏」的逻辑覆盖掉。
    /// </summary>
    private void AdjustVerticalPosition()
    {
        if (_adjustingPosition || !_hasAnchor)
        {
            return;
        }

        double currentHeight = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
        if (currentHeight <= 0)
        {
            return;
        }

        _adjustingPosition = true;
        try
        {
            ApplyPlacement(_anchorX, currentHeight);
        }
        finally
        {
            _adjustingPosition = false;
        }
    }

    /// <summary>
    /// 唯一的定位实现：水平对齐锚点、垂直贴任务栏上方，
    /// 并在隐藏图标浮出面板展开时整体抬到面板之上。
    /// </summary>
    private void ApplyPlacement(double anchorX, double cardHeight)
    {
        _anchorX = anchorX;
        _hasAnchor = true;

        // Win32 返回的锚点与托盘溢出面板矩形是物理像素；WPF 窗口位置、工作区和尺寸是 DIP。
        // 混用会让高 DPI 下的锚点被当成 DIP，最终卡片被钳制到屏幕边缘。
        var dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        double anchorDipX = anchorX / scaleX;

        var wa = SystemParameters.WorkArea;
        double minLeft = wa.Left + 8;
        double maxLeft = wa.Right - CardWidth - 8;

        double left = Math.Clamp(anchorDipX - (CardWidth / 2.0), minLeft, maxLeft);
        double top = Math.Max(wa.Top + 8, wa.Bottom - SpacingAboveTaskbar - cardHeight);

        // 任务栏的 "^" 隐藏图标浮出面板展开时，鼠标停在面板里的图标上，按鼠标位置
        // 算出的卡片会正好压住整个面板。把卡片整体抬到面板上方，面板保持完整可见。
        if (UnmanagedMethods.TryGetOverflowPanelRect(out var panel))
        {
            const double Gap = 8;
            double panelLeft = panel.Left / scaleX;
            double panelTop = panel.Top / scaleY;
            double panelRight = panel.Right / scaleX;
            double panelBottom = panel.Bottom / scaleY;
            bool verticalOverlap = top < panelBottom && (top + cardHeight) > panelTop;
            bool horizontalOverlap = left < panelRight && (left + CardWidth) > panelLeft;

            if (verticalOverlap && horizontalOverlap)
            {
                left = Math.Clamp(
                    panelLeft + (((panelRight - panelLeft) - CardWidth) / 2.0),
                    minLeft, maxLeft);
                top = Math.Max(wa.Top + 8, panelTop - Gap - cardHeight);
            }
        }

        Left = left;
        Top = top;
    }

    private void OnThemeChanged()
    {
        Dispatcher.Invoke(() =>
        {
            ThemeService.ApplyAcrylicBackdrop(this);

            // 必须重新注入画刷：SetResourceReference 是动态查找，若只重建控件树
            // 而资源字典里仍是上一个主题的颜色，底部的动态绑定文字就会保持旧色
            // （深色模式下表现为黑字）。
            ThemeService.ApplyThemeResources(Resources);

            if (_cardBorder != null)
            {
                _cardBorder.BorderBrush = ThemeService.BorderBrush;
                _cardBorder.Background = ThemeService.IsAcrylicEnabled ? Brushes.Transparent : ThemeService.SolidSurfaceBrush;
            }
            if (_lastSnapshot != null)
            {
                UpdateData(_lastSnapshot);
            }
        });
    }

    public void UpdateData(BatterySnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        _renderedSnapshot = snapshot;
        _rootPanel.Children.Clear();

        Brush primary = ThemeService.PrimaryTextBrush;
        Brush secondary = ThemeService.SecondaryTextBrush;
        Brush faint = ThemeService.FaintTextBrush;
        Brush batteryAccent = ThemeService.GetBatteryBrush(snapshot.Percent, snapshot.IsCharging);

        _cardBorder.BorderBrush = ThemeService.BorderBrush;
        _cardBorder.Background = ThemeService.IsAcrylicEnabled ? Brushes.Transparent : ThemeService.SolidSurfaceBrush;

        // ---- 分组 1：设备与电量状态 (带 ⚙ 设置图标按钮)
        _rootPanel.Children.Add(CreateGroupHeader("设备", snapshot.DeviceName, false, true));

        // 大电量显示行 (参考性能监视的关键指标突出展示)
        var batteryRow = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        batteryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        batteryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var statePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        statePanel.Children.Add(new TextBlock
        {
            Text = snapshot.IsCharging ? "正在充电" : (snapshot.IsConnected ? "正常放电中" : "设备离线"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = primary
        });
        statePanel.Children.Add(new TextBlock
        {
            Text = snapshot.LevelText.Length > 0 ? $"电量等级 · {snapshot.LevelText}" : "电量状态",
            FontSize = 10.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeService.TechBlueBrush,
            Margin = new Thickness(0, 1, 0, 0)
        });
        batteryRow.Children.Add(statePanel);

        var rightPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (snapshot.Percent > 0)
        {
            rightPanel.Children.Add(CreateHorizontalBatteryIcon(snapshot.Percent, snapshot.IsCharging, batteryAccent));
        }

        var pctBlock = new TextBlock
        {
            Text = snapshot.Percent > 0 ? $"{snapshot.Percent}%" : "--%",
            FontSize = 21,
            FontWeight = FontWeights.Bold,
            Foreground = batteryAccent,
            VerticalAlignment = VerticalAlignment.Center
        };
        rightPanel.Children.Add(pctBlock);

        Grid.SetColumn(rightPanel, 1);
        batteryRow.Children.Add(rightPanel);
        _rootPanel.Children.Add(batteryRow);

        // 细腻电量条 (带平滑展开动画)
        if (snapshot.Percent > 0)
        {
            _rootPanel.Children.Add(CreateAnimatedProgressBar(snapshot.Percent, batteryAccent));
        }

        // 分割线
        _rootPanel.Children.Add(CreateSeparator());

        // ---- 分组 2：续航与放电状态预测 (仅在非充电且有预测时显示)
        if (snapshot.IsConnected && !snapshot.IsCharging && !string.IsNullOrEmpty(snapshot.RemainingTimeText))
        {
            string confidenceText = !string.IsNullOrEmpty(snapshot.Confidence) ? $"置信度: {snapshot.Confidence}" : "";
            _rootPanel.Children.Add(CreateGroupHeader("续航预测", confidenceText, false, false));

            _rootPanel.Children.Add(CreateMetricRow("预计剩余使用", snapshot.RemainingTimeText));

            if (snapshot.RatePerHour > 0)
            {
                _rootPanel.Children.Add(CreateMetricRow("放电速率", $"{snapshot.RatePerHour:F1}% / 小时"));
            }

            if (!string.IsNullOrEmpty(snapshot.LastUpdatedText))
            {
                _rootPanel.Children.Add(CreateMetricRow("最后更新", snapshot.LastUpdatedText));
            }

            _rootPanel.Children.Add(CreateSeparator());
        }

        // ---- 分组 3：历史电量轨迹 (近 24 小时简易直方图)
        if (snapshot.HourlyBuckets != null && snapshot.HourlyBuckets.Count > 0)
        {
            int recordedHours = 0;
            int minP = 100, maxP = 0;
            foreach (var h in snapshot.HourlyBuckets)
            {
                if (h.Percent > 0)
                {
                    recordedHours++;
                    minP = Math.Min(minP, h.Percent);
                    maxP = Math.Max(maxP, h.Percent);
                }
            }

            if (recordedHours > 0)
            {
                int activeH = snapshot.OnlineHoursCount;
                int sleepH = recordedHours - activeH;
                string summary = activeH > 0
                    ? $"{activeH} 小时活跃 · {sleepH} 小时休眠"
                    : $"{recordedHours} 小时休眠保持";

                _rootPanel.Children.Add(CreateGroupHeader("近 24 小时分析", summary, false, false));
                _rootPanel.Children.Add(CreateMetricRow("记录电量区间", $"{minP}% ~ {maxP}%"));

                // 24 小时电量柱状缩略图
                _rootPanel.Children.Add(CreateHistoryMiniBars(snapshot.HourlyBuckets));
            }
        }

        if (IsVisible)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, AdjustVerticalPosition);
        }
    }

    private static Border CreateSeparator()
    {
        return new Border
        {
            Height = 1,
            Background = ThemeService.SeparatorBrush,
            Margin = new Thickness(0, 7, 0, 6)
        };
    }

    private static Border CreateAnimatedProgressBar(int percent, Brush batteryAccent)
    {
        var track = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = ThemeService.TrackBackgroundBrush,
            Margin = new Thickness(0, 4, 0, 3),
            HorizontalAlignment = WpfAlign.Stretch
        };

        var fillBar = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = batteryAccent,
            HorizontalAlignment = WpfAlign.Left,
            Width = 0
        };

        track.Child = fillBar;

        track.Loaded += (_, _) =>
        {
            double availableWidth = track.ActualWidth;
            if (availableWidth > 0)
            {
                double targetWidth = availableWidth * Math.Clamp(percent / 100.0, 0, 1);
                var anim = new DoubleAnimation
                {
                    From = 0,
                    To = targetWidth,
                    Duration = TimeSpan.FromMilliseconds(420),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                fillBar.BeginAnimation(FrameworkElement.WidthProperty, anim);
            }
        };

        return track;
    }

    private static StackPanel CreateHorizontalBatteryIcon(int percent, bool isCharging, Brush batteryAccent)
    {
        var container = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        // 1. 电池主体外壳 (Rounded Shell)
        var shell = new Border
        {
            Width = 27,
            Height = 13.5,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1.2),
            BorderBrush = batteryAccent,
            Background = Brushes.Transparent,
            Padding = new Thickness(1.2),
            VerticalAlignment = VerticalAlignment.Center
        };

        var innerGrid = new Grid();

        // 2. 内部填充条 (以 10% 为跨度量化，带平滑渐变动画)
        int stepPercent = Math.Clamp((int)Math.Round(percent / 10.0) * 10, 0, 100);
        if (percent > 0 && stepPercent == 0) stepPercent = 10;

        var fillBlock = new Border
        {
            Height = 9,
            CornerRadius = new CornerRadius(1.5),
            Background = batteryAccent,
            HorizontalAlignment = WpfAlign.Left,
            Width = 0
        };

        innerGrid.Children.Add(fillBlock);

        shell.Loaded += (_, _) =>
        {
            double maxInnerWidth = 27 - 2.4 - 2.4;
            double targetW = Math.Max(2, maxInnerWidth * (stepPercent / 100.0));
            var anim = new DoubleAnimation
            {
                From = 0,
                To = targetW,
                Duration = TimeSpan.FromMilliseconds(350),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fillBlock.BeginAnimation(FrameworkElement.WidthProperty, anim);
        };

        if (isCharging)
        {
            var bolt = new TextBlock
            {
                Text = LucideIcons.Zap,
                FontFamily = ThemeService.LucideFont,
                FontSize = 8.5,
                Foreground = Brushes.White,
                HorizontalAlignment = WpfAlign.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            innerGrid.Children.Add(bolt);
        }

        shell.Child = innerGrid;
        container.Children.Add(shell);

        var cap = new Border
        {
            Width = 2.5,
            Height = 7.5,
            CornerRadius = new CornerRadius(0, 1.5, 1.5, 0),
            Background = batteryAccent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 0, 0)
        };
        container.Children.Add(cap);

        return container;
    }

    private Grid CreateGroupHeader(string groupTitle, string subtitle, bool hasPreviousGroup, bool showSettingsBtn = false)
    {
        var header = new Grid
        {
            Margin = hasPreviousGroup ? new Thickness(0, 8, 0, 2) : new Thickness(0, 0, 0, 2)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (showSettingsBtn)
        {
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        header.Children.Add(new TextBlock
        {
            Text = groupTitle,
            Foreground = ThemeService.TechBlueBrush,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (!string.IsNullOrEmpty(subtitle))
        {
            var subText = new TextBlock
            {
                Text = subtitle,
                Foreground = ThemeService.TechBlueBrush,
                FontSize = 10.5,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = WpfAlign.Right,
                TextAlignment = TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = showSettingsBtn ? new Thickness(0, 0, 6, 0) : new Thickness(0)
            };
            Grid.SetColumn(subText, 1);
            header.Children.Add(subText);
        }

        if (showSettingsBtn)
        {
            var btn = new Button
            {
                Content = LucideIcons.Settings,
                FontFamily = ThemeService.LucideFont,
                FontSize = 13,
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "打开设置",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = WpfAlign.Right,
                Style = CreateSettingsIconButtonStyle()
            };
            btn.Click += (_, _) =>
            {
                Hide();
                _onOpenSettings?.Invoke();
            };
            Grid.SetColumn(btn, 2);
            header.Children.Add(btn);
        }

        return header;
    }

    private static Style CreateSettingsIconButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Button.OverridesDefaultStyleProperty, true));
        style.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));

        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "border";
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        borderFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfAlign.Center);
        contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(contentFactory);

        template.VisualTree = borderFactory;

        var mouseOverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        mouseOverTrigger.Setters.Add(new Setter
        {
            TargetName = "border",
            Property = Border.BackgroundProperty,
            Value = new SolidColorBrush(Color.FromArgb(40, 2, 132, 199)) // 科技蓝半透明微光背景
        });
        template.Triggers.Add(mouseOverTrigger);

        style.Setters.Add(new Setter(Button.TemplateProperty, template));
        style.Setters.Add(new Setter(Button.ForegroundProperty, ThemeService.TechBlueBrush));
        return style;
    }

    private Grid CreateMetricRow(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = ThemeService.SecondaryTextBrush,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        var valBlock = new TextBlock
        {
            Text = value,
            Foreground = ThemeService.PrimaryTextBrush,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = WpfAlign.Right,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(valBlock, 1);
        row.Children.Add(valBlock);
        return row;
    }

    private static UIElement CreateHistoryMiniBars(IReadOnlyList<HourlyBucket> history)
    {
        const double chartHeight = 36.0;
        const int maxSlots = 24;

        int count = Math.Min(maxSlots, history.Count);
        int startIndex = history.Count - count;

        // 找出记录内的最高与最低电量
        int minP = 100, maxP = 0;
        for (int i = 0; i < count; i++)
        {
            int p = history[startIndex + i].Percent;
            if (p > 0)
            {
                minP = Math.Min(minP, p);
                maxP = Math.Max(maxP, p);
            }
        }

        // 智能动态基线：
        // 如果 24 小时内的电量变化在 25% 以内 (如 88% ~ 94%，微放电仅 6%)，
        // 将基线浮动到 (minP - 15%)，保留足够的底部稳固高度，同时将 6%~10% 的微小放电级差放大 3~4 倍！
        // 若发生了大幅充放电 (跨度 > 25%)，自动回退到 0~100% 绝对刻度。
        double baseline = (maxP - minP <= 25 && minP >= 20) ? Math.Max(0, minP - 15) : 0;
        double rangeSpan = Math.Max(15, 100 - baseline);

        // 外层卡槽容器：固定高度 36px，居中
        var container = new Border
        {
            Height = chartHeight,
            Margin = new Thickness(0, 5, 0, 2),
            Background = ThemeService.TrackBackgroundBrush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(0)
        };

        var barPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom
        };

        for (int i = 0; i < count; i++)
        {
            var sample = history[startIndex + i];
            if (sample.Percent > 0)
            {
                double norm = Math.Clamp((sample.Percent - baseline) / rangeSpan, 0.12, 1.0);
                double h = Math.Clamp(chartHeight * norm, 4.0, chartHeight);

                var bar = new Border
                {
                    Width = 7.0,
                    Height = h,
                    CornerRadius = new CornerRadius(1.5, 1.5, 0, 0),
                    Background = ThemeService.GetBatteryBrush(sample.Percent, false),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = (i == count - 1) ? new Thickness(0) : new Thickness(0, 0, 2, 0),
                    Opacity = sample.IsSleeping ? 0.38 : 1.0,
                    ToolTip = sample.IsSleeping
                        ? $"{sample.HourText}: {sample.Percent}% (休眠保持)"
                        : $"{sample.HourText}: {sample.Percent}% (活跃记录)"
                };
                barPanel.Children.Add(bar);
            }
            else
            {
                var dot = new Border
                {
                    Width = 7.0,
                    Height = 2.0,
                    Background = ThemeService.FaintTextBrush,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = (i == count - 1) ? new Thickness(0) : new Thickness(0, 0, 2, 0),
                    ToolTip = $"{sample.HourText}: 暂无记录"
                };
                barPanel.Children.Add(dot);
            }
        }

        container.Child = barPanel;

        // 包含底部辅助时间刻度文字 (24小时前 ... 现在)
        var outerPanel = new StackPanel();
        outerPanel.Children.Add(container);

        string startHourStr = (count > 0 && !string.IsNullOrEmpty(history[startIndex].HourText))
            ? history[startIndex].HourText
            : "00:00";

        var timeGrid = new Grid { Margin = new Thickness(2, 3, 2, 0) };
        var leftTime = new TextBlock
        {
            Text = startHourStr,
            FontSize = 9.5,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = $"24 小时记录起点：{startHourStr}"
        };
        leftTime.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
        timeGrid.Children.Add(leftTime);

        var rightTime = new TextBlock
        {
            Text = "现在",
            FontSize = 9.5,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = $"最新记录：现在 ({DateTime.Now:HH:mm})"
        };
        rightTime.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondary");
        timeGrid.Children.Add(rightTime);

        outerPanel.Children.Add(timeGrid);
        return outerPanel;
    }

    public void ToggleNear(double screenX, double screenY)
    {
        if (IsVisible)
        {
            Hide();
            _outsideClickTimer.Stop();
        }
        else
        {
            ThemeService.ApplyAcrylicBackdrop(this);

            // 注意：Program.ShowDetailsAt 在调用 ToggleNear 之前已经 UpdateData 过一次。
            // 这里只在「快照比已渲染内容更新」时才重建，避免每次打开都把整棵视觉树
            // （24 根柱子 + 全部文本 + 画刷）重复构建两遍。
            if (_lastSnapshot != null && !ReferenceEquals(_renderedSnapshot, _lastSnapshot))
            {
                UpdateData(_lastSnapshot);
            }

            // 预先测量视觉树，获取当前卡片真实需要的高度（休眠短卡片与活跃长卡片动态贴合）
            Measure(new Size(CardWidth, double.PositiveInfinity));
            double realHeight = DesiredSize.Height > 0 ? DesiredSize.Height : 160;

            ApplyPlacement(screenX, realHeight);
            Show();
            Activate();
            _wasPrimaryButtonDown = (UnmanagedMethods.GetAsyncKeyState(UnmanagedMethods.VK_LBUTTON) & 0x8000) != 0;
            _outsideClickTimer.Start();
        }
    }

    private void OutsideClickTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            _outsideClickTimer.Stop();
            return;
        }

        bool isDown = (UnmanagedMethods.GetAsyncKeyState(UnmanagedMethods.VK_LBUTTON) & 0x8000) != 0;
        bool justPressed = isDown && !_wasPrimaryButtonDown;
        _wasPrimaryButtonDown = isDown;

        if (justPressed)
        {
            UnmanagedMethods.GetCursorPos(out var pt);
            // GetCursorPos 返回屏幕物理像素；先转换为窗口本地 WPF 坐标，避免高 DPI 下误判卡片内点击。
            Point localPoint = PointFromScreen(new Point(pt.X, pt.Y));
            var rect = new Rect(0, 0, ActualWidth, ActualHeight);
            if (!rect.Contains(localPoint))
            {
                Hide();
                _outsideClickTimer.Stop();
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _outsideClickTimer.Stop();
        ThemeService.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }
}
