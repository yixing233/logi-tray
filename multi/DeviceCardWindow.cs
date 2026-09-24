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
/// 外观**完全复用完整版那套组件**（shared-wpf/DeviceCardWidgets.cs +
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
    // 宽度对齐完整版的 232：多设备版与它并排显示时观感一致，
    // 太宽会显得笨重（先前用了 268，比完整版宽出一圈）。
    private const double CardWidth = 232;
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
        // 不在这里 Start：定时器只在卡片可见时才需要跑，
        // 常驻空转会让隐藏状态下也每 50ms 醒一次（无谓的 CPU 唤醒）。
        // 由 ToggleNear 在显示时启动、隐藏时停止。

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

        // ── 顶部标题行：标题 + 刷新图标 + 设置图标 ──
        _rootPanel.Children.Add(CreateTopBar());

        if (_readings.Count == 0)
        {
            _rootPanel.Children.Add(DeviceCardWidgets.CreateGroupHeader(
                "设备", "未检测到", true));
            _rootPanel.Children.Add(DeviceCardWidgets.CreateHint(
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

        // 底部不再放「立即刷新 / 关闭」按钮：
        //   刷新已改为标题栏图标按钮；
        //   关闭由「点击卡片外侧」完成（用户要求）。
    }

    /// <summary>
    /// 刷新图标的字形。
    ///
    /// 刻意不用完整版的 LucideIcons.Refresh（U+E0AC）：那个字形渲染出来是
    /// 一个带刻度的圆盘，语义更像「仪表」，不像「刷新」。
    /// U+E145 是标准的顺时针循环箭头 —— 码点由逐格渲染对照确认
    /// （字形名表被裁剪，无法从字体里查名字，只能画出来看）。
    /// </summary>
    private const string RefreshGlyph = "\ue145";

    /// <summary>标题栏：左侧标题，右侧刷新与设置两个图标按钮。</summary>
    private Grid CreateTopBar()
    {
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        bar.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "设备电量",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeService.TechBlueBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 0);
        bar.Children.Add(title);

        var refresh = CreateIconButton(RefreshGlyph, "立即刷新");
        refresh.Click += (_, _) => UpdateData(DeviceReader.ReadAll(_settings));
        Grid.SetColumn(refresh, 1);
        bar.Children.Add(refresh);

        var settings = CreateIconButton(LucideIcons.Settings, "打开设置");
        settings.Click += (_, _) =>
        {
            // 与完整版一致：先收起卡片再打开设置，避免两个窗口重叠
            Hide();
            _outsideClickTimer.Stop();
            _onOpenSettings();
        };
        Grid.SetColumn(settings, 2);
        bar.Children.Add(settings);

        return bar;
    }

    /// <summary>标题栏用的图标按钮（透明底，悬停时科技蓝微光）。</summary>
    private static Button CreateIconButton(string glyph, string tooltip)
    {
        var btn = new Button
        {
            Content = glyph,
            FontFamily = ThemeService.LucideFont,
            FontSize = 13,
            Width = 24,
            Height = 22,
            Padding = new Thickness(0),
            ToolTip = tooltip,
            Cursor = Cursors.Hand,
            Focusable = false,
            Style = DeviceCardWidgets.CreateIconButtonStyle()
        };
        // 图标按钮之间留一点间距，避免贴在一起
        btn.Margin = new Thickness(2, 0, 0, 0);
        return btn;
    }

    /// <summary>一台设备一个分组：标题 + 状态 + 电池图标 + 大号电量 + 进度条。</summary>
    private void AddDeviceGroup(DeviceReading r, bool hasPrevious)
    {
        string state = r.IsOnline
            ? (r.IsCharging ? "正在充电" : "正常放电中")
            : "设备离线";
        string subtitle = r.IsOnline ? "已连接" : "已休眠";

        _rootPanel.Children.Add(DeviceCardWidgets.CreateGroupHeader(
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
            // 离线设备不显示「来源 · XXX」：来源是内部实现细节，
            // 对用户没有意义（用户要求移除）。
            Text = r.IsOnline
                ? $"电量等级 · {Protocols.LevelText(r.Percent)}"
                : "",
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
            right.Children.Add(DeviceCardWidgets.CreateHorizontalBatteryIcon(
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
                DeviceCardWidgets.CreateAnimatedProgressBar(r.Percent, accent));
        }
        // 读不到电量时不显示任何引导文案（用户要求移除），
        // 电量那一栏的 "--%" 已经说明了状态。

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

    /// <summary>
    /// 点击卡片外部收起。
    ///
    /// 必须用 GetAsyncKeyState 直读物理按键，**不能用 WPF 的 Mouse.LeftButton**：
    /// 卡片是 ShowActivated=false（不抢焦点），窗口未激活时 WPF 的鼠标状态
    /// 一直是 Released，于是「点了外面」永远检测不到、卡片永远关不掉。
    /// 完整版同样用 GetAsyncKeyState。
    ///
    /// 判定同时用两种信号，缺一不可：
    ///   1. 0x8000 当前是否按下 —— 检测「按下沿」；
    ///   2. 0x0001 自上次调用以来是否按过 —— 补漏。
    /// 只看按下沿会漏掉「按下与抬起都落在两次轮询之间」的快速点击，
    /// 用户表现为「点了外面没反应」。
    /// </summary>
    private void OutsideClickTick(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        short state = UnmanagedMethods.GetAsyncKeyState(UnmanagedMethods.VK_LBUTTON);
        bool isDown = (state & 0x8000) != 0;
        bool pressedSinceLast = (state & 0x0001) != 0;

        bool justPressed = (isDown && !_wasPrimaryDown) || pressedSinceLast;
        _wasPrimaryDown = isDown;

        if (!justPressed) return;

        // GetCursorPos 是物理像素，先转成窗口本地 WPF 坐标，
        // 否则高 DPI 下会把卡片内的点击误判成外部点击。
        if (!UnmanagedMethods.GetCursorPos(out var pt)) return;
        Point local = PointFromScreen(new Point(pt.X, pt.Y));
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        if (!rect.Contains(local))
        {
            Hide();
            _outsideClickTimer.Stop();
        }
    }

    /// <summary>
    /// 在托盘图标上方切换显示/收起。
    ///
    /// 复用同一个窗口实例：收起走 Hide() 而不是 Close()。
    /// Close() 会销毁窗口、下次必须 new 一个，既慢又会让 WPF 反复重建整棵
    /// 视觉树与渲染资源（用户反馈「每次都像新开一个窗口」）。
    /// </summary>
    public void ToggleNear(double screenX, double screenY)
    {
        _anchorX = screenX;

        if (IsVisible)
        {
            Hide();
            _outsideClickTimer.Stop();
            return;
        }

        ThemeService.ApplyAcrylicBackdrop(this);

        // 先量出内容真实高度再定位，避免用旧高度摆错位置
        Measure(new Size(CardWidth, double.PositiveInfinity));
        AdjustVerticalPosition();

        Show();

        // 记录按下时左键的真实状态：从托盘点击打开时它通常仍按着，
        // 若不设这个基准，抬起那一刻会被误判成「点击了外部」而立即关闭。
        _wasPrimaryDown =
            (UnmanagedMethods.GetAsyncKeyState(UnmanagedMethods.VK_LBUTTON) & 0x8000) != 0;

        _outsideClickTimer.Start();
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
}
