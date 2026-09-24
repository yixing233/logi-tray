using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MouseBatteryTray;

namespace MultiTray;

/// <summary>
/// 多设备电量卡片：Windows 11 Fluent 亚克力悬浮窗。
///
/// 外观**完全复用完整版那套组件**（shared-wpf/AcrylicWidgets.cs +
/// ThemeService.cs + FluentStyles.xaml），包括：
///   * 分组标题用科技蓝，右侧跟随设备名
///   * 电池图标（外壳 + 按 10% 量化的填充 + 充电闪电）
///   * 大号电量百分比 + 带动画的细腻进度条
///   * 分隔线、指标行、图标按钮样式，全部取自共享组件
///
/// 多设备时每台设备占一个分组，量最高的排在最前，这样一屏能按顺序扫到。
/// 刻意不画历史曲线、不做续航预测 —— 本工具只回答「各设备还剩多少电」
/// 与「有没有谁快没电了」。
/// </summary>
public sealed class DeviceCardWindow : Window
{
    private const double CardWidth = 268;
    private const double CardCornerRadius = 8;
    private const int SpacingAboveTaskbar = 8;

    private readonly Border _cardBorder;
    private readonly StackPanel _rootPanel;
    private readonly MultiSettings _settings;
    private readonly Action _onOpenSettings;

    /// <summary>点击卡片外部收起的轮询定时器（与完整版一致的做法）。</summary>
    private readonly DispatcherTimer _outsideClickTimer;

    private readonly List<string> _renderedKeys = new();
    private List<DeviceReading> _readings;
    private bool _wasPrimaryDown;
    private bool _adjusting;

    /// <summary>
    /// 托盘图标的屏幕 X（物理像素）。卡片水平居中对齐到它，
    /// 而不是固定贴在屏幕右下角 —— 后者会让卡片离图标很远（用户反馈过）。
    /// </summary>
    private double _anchorX = double.NaN;

    /// <summary>是否已有有效锚点。</summary>
    private bool HasAnchor => !double.IsNaN(_anchorX);

    public DeviceCardWindow(List<DeviceReading> readings, MultiSettings settings,
                            Action onOpenSettings)
    {
        _readings = readings;
        _settings = settings;
        _onOpenSettings = onOpenSettings;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        // 亚克力要求根背景透明，否则不透明底会盖住毛玻璃层
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Topmost = true;
        Width = CardWidth;
        WindowStartupLocation = WindowStartupLocation.Manual;
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

        _rootPanel = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };

        _cardBorder = new Border
        {
            CornerRadius = new CornerRadius(CardCornerRadius),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeService.BorderBrush,
            // 亚克力开启时纯透明，以激活 DWM 硬件级实时虚化；
            // 关闭时必须换成不透明底板 —— 否则窗口没有任何背景，
            // 文字会直接叠在桌面/其它窗口上，完全看不清（实测截图确认过）。
            Background = ThemeService.IsAcrylicEnabled
                ? Brushes.Transparent
                : ThemeService.SolidSurfaceBrush,
            Width = CardWidth,
            Child = _rootPanel
        };
        Content = _cardBorder;

        SourceInitialized += (_, _) => ThemeService.ApplyAcrylicBackdrop(this);
        ContentRendered += (_, _) => ThemeService.ApplyAcrylicBackdrop(this);
        SizeChanged += (_, _) => AdjustVerticalPosition();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                    AdjustVerticalPosition);
            }
        };

        _outsideClickTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _outsideClickTimer.Tick += OutsideClickTick;
        _outsideClickTimer.Start();

        ThemeService.ThemeChanged += OnThemeChanged;

        Build();

        Closed += (_, _) =>
        {
            _outsideClickTimer.Stop();
            ThemeService.ThemeChanged -= OnThemeChanged;
        };
    }

    /// <summary>刷新数据并就地重建内容（不重建窗口，避免消息循环被切断）。</summary>
    public void UpdateData(List<DeviceReading> readings)
    {
        _readings = readings;
        Build();
        AdjustVerticalPosition();
    }

    private void Build()
    {
        _rootPanel.Children.Clear();
        _renderedKeys.Clear();

        if (_readings.Count == 0)
        {
            _rootPanel.Children.Add(AcrylicWidgets.CreateGroupHeader(
                "设备", "未检测到", false));
            _rootPanel.Children.Add(AcrylicWidgets.CreateHint(
                "支持罗技、迈从 MCHOSE、ATK / VXE / VGN。", 4, 6));
        }
        else
        {
            // 按电量从低到高排列：最需要充电的排最前
            var ordered = _readings
                .OrderBy(r => r.Percent >= 0 ? r.Percent : int.MaxValue)
                .ThenBy(r => r.Name, StringComparer.CurrentCulture)
                .ToList();

            bool first = true;
            foreach (var r in ordered)
            {
                AddDeviceGroup(r, hasPrevious: !first);
                first = false;
            }
        }

        _rootPanel.Children.Add(AcrylicWidgets.CreateSeparator());

        string hint = _settings.NotifyEnabled
            ? $"低电量提醒：≤{_settings.LowThreshold}% 提醒，≤{_settings.CriticalThreshold}% 严重提醒"
            : "低电量提醒已关闭";
        _rootPanel.Children.Add(AcrylicWidgets.CreateHint(hint, 0, 6));

        // 底部操作行：立即刷新 / 关闭
        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        var refresh = AcrylicWidgets.CreateActionButton("立即刷新", primary: false);
        refresh.Click += (_, _) =>
            UpdateData(DeviceReader.ReadAll(_settings));
        Grid.SetColumn(refresh, 0);
        buttons.Children.Add(refresh);

        var close = AcrylicWidgets.CreateActionButton("关闭", primary: true);
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 2);
        buttons.Children.Add(close);

        _rootPanel.Children.Add(buttons);
    }

    /// <summary>一台设备一个分组：标题 + 状态 + 电池图标 + 大号电量 + 进度条。</summary>
    private void AddDeviceGroup(DeviceReading r, bool hasPrevious)
    {
        string state = r.IsOnline
            ? (r.IsCharging ? "正在充电" : "正常放电中")
            : "设备离线";
        string subtitle = r.IsOnline ? "已连接" : "已休眠";

        _rootPanel.Children.Add(AcrylicWidgets.CreateGroupHeader(
            r.Name, subtitle, hasPrevious));

        Brush accent = r.Percent >= 0
            ? ThemeService.GetBatteryBrush(r.Percent, r.IsCharging)
            : ThemeService.FaintTextBrush;

        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = state,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeService.PrimaryTextBrush
        });
        left.Children.Add(new TextBlock
        {
            Text = r.IsOnline
                ? $"电量等级 · {Protocols.LevelText(r.Percent)}"
                : $"来源 · {r.Source}",
            FontSize = 10.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeService.TechBlueBrush,
            Margin = new Thickness(0, 1, 0, 0)
        });
        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (r.Percent > 0)
        {
            right.Children.Add(AcrylicWidgets.CreateHorizontalBatteryIcon(
                r.Percent, r.IsCharging, accent));
        }
        right.Children.Add(new TextBlock
        {
            Text = r.Percent >= 0 ? $"{r.Percent}%" : "--%",
            FontSize = 21,
            FontWeight = FontWeights.Bold,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(right, 1);
        row.Children.Add(right);

        _rootPanel.Children.Add(row);

        if (r.Percent > 0)
        {
            _rootPanel.Children.Add(
                AcrylicWidgets.CreateAnimatedProgressBar(r.Percent, accent));
        }
        else
        {
            _rootPanel.Children.Add(AcrylicWidgets.CreateHint(
                r.IsOnline ? "正在读取…" : "唤醒设备后点「立即刷新」重试", 2, 2));
        }

        _renderedKeys.Add(r.Key);
    }

    private void OnThemeChanged()
    {
        Dispatcher.Invoke(() =>
        {
            ThemeService.ApplyAcrylicBackdrop(this);
            // 必须重新注入画刷：只重建控件树而资源字典仍是旧主题时，
            // 动态绑定的文字会保持旧色（深色模式下表现为黑字）
            ThemeService.ApplyThemeResources(Resources);
            _cardBorder.BorderBrush = ThemeService.BorderBrush;
            _cardBorder.Background = ThemeService.IsAcrylicEnabled
                ? Brushes.Transparent
                : ThemeService.SolidSurfaceBrush;
            Build();
        });
    }

    private void OutsideClickTick(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        bool down = Mouse.LeftButton == MouseButtonState.Pressed
                    || Mouse.RightButton == MouseButtonState.Pressed;
        if (down)
        {
            _wasPrimaryDown = true;
            return;
        }

        if (_wasPrimaryDown)
        {
            _wasPrimaryDown = false;
            // 点击落在卡片范围之外就收起
            if (!IsMouseOver) Close();
        }
    }

    /// <summary>
    /// 把卡片摆到托盘图标正上方。
    ///
    /// 水平方向**以锚点（托盘图标的 X）居中**，而不是固定贴屏幕右边 ——
    /// 后者会让卡片离图标很远，用户看到的卡片出现在右下角落，与图标脱节。
    /// 锚点由 TrayContext 在托盘图标被按下时记录，是图标真实位置；
    /// 右键菜单弹出后菜单自身会位移，那时再取光标就晚了。
    ///
    /// Win32 返回的锚点与溢出面板矩形是物理像素，而 WPF 的窗口位置与
    /// 工作区是 DIP，混用会让高 DPI 下锚点被当成 DIP 而钳到屏幕边缘。
    /// </summary>
    private void AdjustVerticalPosition()
    {
        if (_adjusting || !IsVisible) return;

        double height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
        if (height <= 0) return;

        _adjusting = true;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

            var wa = SystemParameters.WorkArea;
            double minLeft = wa.Left + 8;
            double maxLeft = wa.Right - CardWidth - 8;

            // 有锚点就居中到图标，否则退回右下角（例如非托盘入口打开）
            double anchorDipX = HasAnchor ? _anchorX / scaleX : (wa.Right - 40);
            double left = Math.Clamp(anchorDipX - CardWidth / 2.0, minLeft, maxLeft);
            double top = Math.Max(wa.Top + 8,
                wa.Bottom - SpacingAboveTaskbar - height);

            // 隐藏图标浮出面板展开时把卡片抬到面板之上，
            // 否则会正好压住整个面板（与完整版同一处理）
            if (UnmanagedMethods.TryGetOverflowPanelRect(out var panel))
            {
                double pl = panel.Left / scaleX, pr = panel.Right / scaleX;
                double pt = panel.Top / scaleY, pb = panel.Bottom / scaleY;

                bool vOverlap = top < pb && top + height > pt;
                bool hOverlap = left < pr && left + CardWidth > pl;
                if (vOverlap && hOverlap)
                {
                    left = Math.Clamp((pl + pr) / 2 - CardWidth / 2, minLeft, maxLeft);
                    top = Math.Max(wa.Top + 8,
                        pt - SpacingAboveTaskbar - height);
                }
            }

            Left = left;
            Top = top;
        }
        catch
        {
            // 定位失败保持原位置，不影响功能
        }
        finally
        {
            _adjusting = false;
        }
    }

    /// <summary>
    /// 记录托盘图标锚点（物理像素）并（重新）打开卡片。
    /// 由 TrayContext 在图标被点击时调用。
    /// </summary>
    public void ShowNear(double screenX, double screenY)
    {
        _anchorX = screenX;

        if (!IsVisible)
        {
            Show();
        }

        // 先按内容测量出真实高度，再定位，避免第一帧用旧高度摆错位置
        Measure(new Size(CardWidth, double.PositiveInfinity));
        AdjustVerticalPosition();
        Activate();

        // 记录按下时左键是否仍处于按下状态：从托盘点击打开时它通常是按下的，
        // 若不做这个基准，抬起那一刻会被误判为「点击了外部」而立即关闭。
        _wasPrimaryDown =
            (UnmanagedMethods.GetAsyncKeyState(UnmanagedMethods.VK_LBUTTON) & 0x8000) != 0;
    }
}
