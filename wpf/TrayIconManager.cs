using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using GdiColor = System.Drawing.Color;
using GdiBrush = System.Drawing.SolidBrush;
using GdiFont = System.Drawing.Font;
using GdiFontStyle = System.Drawing.FontStyle;
using GdiBitmap = System.Drawing.Bitmap;
using GdiGraphics = System.Drawing.Graphics;
using GdiIcon = System.Drawing.Icon;
using GdiPen = System.Drawing.Pen;
using GdiBrushes = System.Drawing.Brushes;
using GdiStringFormat = System.Drawing.StringFormat;
using GdiRectangleF = System.Drawing.RectangleF;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace MouseBatteryTray;

public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly Action<int, int> _onShowDetails;
    private readonly Action _onShowSettings;
    private readonly Action _onExit;
    private readonly Action<string>? _onStyleChanged;
    private readonly Action<string>? _onThemeModeChanged;

    private int _lastPercent = -999;
    private bool _lastCharging;
    private string _currentStyle = "battery";

    // 托盘图标被按下那一刻的光标位置。右键菜单弹出后菜单会为避开屏幕边缘而位移，
    // 此时再取 GetCursorPos 得到的是菜单项坐标，会让详情卡片锚到错误的位置。
    private int _anchorX;
    private int _anchorY;
    private bool _hasAnchor;

    private readonly ToolStripMenuItem _itemBattery;
    private readonly ToolStripMenuItem _itemRing;
    private readonly ToolStripMenuItem _itemNumber;

    private readonly ToolStripMenuItem _itemThemeSystem;
    private readonly ToolStripMenuItem _itemThemeLight;
    private readonly ToolStripMenuItem _itemThemeDark;

    public string CurrentStyle => _currentStyle;

    /// <summary>托盘图标锚点的 X（物理像素）。没有有效锚点时回退到当前光标。</summary>
    private int AnchorX => _hasAnchor ? _anchorX : CurrentCursorX();

    /// <summary>托盘图标锚点的 Y（物理像素）。</summary>
    private int AnchorY => _hasAnchor ? _anchorY : CurrentCursorY();

    private static int CurrentCursorX()
    {
        UnmanagedMethods.GetCursorPos(out var pt);
        return pt.X;
    }

    private static int CurrentCursorY()
    {
        UnmanagedMethods.GetCursorPos(out var pt);
        return pt.Y;
    }

    /// <summary>
    /// 记录锚点。鼠标按下与菜单 Opening 时都直接读光标，此刻菜单尚未弹出，
    /// 取到的就是托盘图标的真实位置。
    /// </summary>
    private void CaptureAnchor()
    {
        if (UnmanagedMethods.GetCursorPos(out var pt))
        {
            _anchorX = pt.X;
            _anchorY = pt.Y;
            _hasAnchor = true;
        }
    }

    public TrayIconManager(Action<int, int> onShowDetails, Action onShowSettings, Action onExit,
                           string initialStyle = "battery", Action<string>? onStyleChanged = null,
                           Action<string>? onThemeModeChanged = null)
    {
        _onShowDetails = onShowDetails;
        _onShowSettings = onShowSettings;
        _onExit = onExit;
        _onStyleChanged = onStyleChanged;
        _onThemeModeChanged = onThemeModeChanged;
        _currentStyle = string.IsNullOrEmpty(initialStyle) ? "battery" : initialStyle;

        _contextMenu = new ContextMenuStrip
        {
            ShowImageMargin = true
        };

        var detailsItem = new ToolStripMenuItem("显示详情");
        detailsItem.Click += (_, _) => _onShowDetails(AnchorX, AnchorY);
        _contextMenu.Items.Add(detailsItem);

        var settingsItem = new ToolStripMenuItem("设置...");
        settingsItem.Click += (_, _) => _onShowSettings();
        _contextMenu.Items.Add(settingsItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // 1. 样式切换子菜单
        var styleMenu = new ToolStripMenuItem("图标样式");

        _itemBattery = new ToolStripMenuItem("电池胶囊");
        _itemBattery.Click += (_, _) => SetStyle("battery", true);
        styleMenu.DropDownItems.Add(_itemBattery);

        _itemRing = new ToolStripMenuItem("环形进度");
        _itemRing.Click += (_, _) => SetStyle("ring", true);
        styleMenu.DropDownItems.Add(_itemRing);

        _itemNumber = new ToolStripMenuItem("纯数字");
        _itemNumber.Click += (_, _) => SetStyle("number", true);
        styleMenu.DropDownItems.Add(_itemNumber);

        _contextMenu.Items.Add(styleMenu);

        // 2. 外观主题子菜单
        var themeMenu = new ToolStripMenuItem("外观主题");

        _itemThemeSystem = new ToolStripMenuItem("跟随系统");
        _itemThemeSystem.Click += (_, _) => SetThemeMode("system", true);
        themeMenu.DropDownItems.Add(_itemThemeSystem);

        _itemThemeLight = new ToolStripMenuItem("浅色模式");
        _itemThemeLight.Click += (_, _) => SetThemeMode("light", true);
        themeMenu.DropDownItems.Add(_itemThemeLight);

        _itemThemeDark = new ToolStripMenuItem("深色模式");
        _itemThemeDark.Click += (_, _) => SetThemeMode("dark", true);
        themeMenu.DropDownItems.Add(_itemThemeDark);

        _contextMenu.Items.Add(themeMenu);
        _contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => _onExit();
        _contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "logi-tray",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _onShowDetails(AnchorX, AnchorY);
            }
        };

        // 在菜单弹出之前抓取光标：这是托盘图标所在的位置，也是唯一可靠的锚点。
        _notifyIcon.MouseDown += (_, _) => CaptureAnchor();
        _contextMenu.Opening += (_, _) => CaptureAnchor();

        UpdateStyleChecks();
        UpdateThemeChecks();
        UpdateIcon(-1, false);

        ThemeService.ThemeChanged += () =>
        {
            UpdateThemeChecks();
            UpdateIcon(_lastPercent, _lastCharging);
        };
    }

    public void SetThemeMode(string mode, bool notifyCallback = false)
    {
        ThemeService.SetThemeMode(mode);
        UpdateThemeChecks();
        UpdateIcon(_lastPercent, _lastCharging);
        if (notifyCallback)
        {
            _onThemeModeChanged?.Invoke(mode);
        }
    }

    private void UpdateThemeChecks()
    {
        string mode = ThemeService.ConfiguredThemeMode;
        _itemThemeSystem.Checked = mode == "system";
        _itemThemeLight.Checked = mode == "light";
        _itemThemeDark.Checked = mode == "dark";
    }

    public void SetStyle(string style, bool notifyCallback = false)
    {
        if (_currentStyle == style) return;
        _currentStyle = style;
        UpdateStyleChecks();
        UpdateIcon(_lastPercent, _lastCharging);
        if (notifyCallback)
        {
            _onStyleChanged?.Invoke(_currentStyle);
        }
    }

    private void UpdateStyleChecks()
    {
        _itemBattery.Checked = _currentStyle == "battery";
        _itemRing.Checked = _currentStyle == "ring";
        _itemNumber.Checked = _currentStyle == "number";
    }

    public void UpdateSnapshot(BatterySnapshot snapshot)
    {
        if (snapshot.Percent != _lastPercent || snapshot.IsCharging != _lastCharging)
        {
            _lastPercent = snapshot.Percent;
            _lastCharging = snapshot.IsCharging;
            UpdateIcon(snapshot.Percent, snapshot.IsCharging);
        }

        string tooltip = snapshot.Percent >= 0
            ? $"{snapshot.DeviceName}: {snapshot.Percent}% ({snapshot.RemainingTimeText})"
            : $"{snapshot.DeviceName}: 离线或休眠";

        if (tooltip.Length > 63) tooltip = tooltip[..63];
        _notifyIcon.Text = tooltip;
    }

    public void ShowNotification(string title, string text)
    {
        _notifyIcon.ShowBalloonTip(3000, title, text, ToolTipIcon.Warning);
    }

    /// <summary>
    /// 超采样倍率。GDI+ 直接在 16×16 上抗锯齿时采样点太少，环形进度弧的锯齿
    /// 尤其明显。改为在 4 倍画布上矢量绘制，再用高质量插值缩回 16×16，
    /// 相当于 16 倍采样，边缘平滑度显著提升。
    /// </summary>
    private const int SuperSample = 4;

    /// <summary>托盘图标的目标边长（像素）。</summary>
    private const int IconSize = 16;

    private void UpdateIcon(int percent, bool charging)
    {
        try
        {
            var mediaColor = ThemeService.GetBatteryColor(percent, charging);
            GdiColor accentColor = GdiColor.FromArgb(mediaColor.R, mediaColor.G, mediaColor.B);

            using var bmp = RenderIcon(percent, charging, accentColor);
            IntPtr hIcon = bmp.GetHicon();
            GdiIcon? oldIcon = _notifyIcon.Icon;
            _notifyIcon.Icon = GdiIcon.FromHandle(hIcon);

            if (oldIcon != null)
            {
                UnmanagedMethods.DestroyIcon(oldIcon.Handle);
                oldIcon.Dispose();
            }
        }
        catch { }
    }

    /// <summary>
    /// 渲染 16×16 托盘图标：先在高分辨率画布上绘制，再降采样。
    /// 矢量图形按 16×16 逻辑坐标书写，由 ScaleTransform 统一放大；
    /// 像素字模则用 FillLogicalPixel 按倍率铺成方块，降采样后依然锐利。
    /// </summary>
    private GdiBitmap RenderIcon(int percent, bool charging, GdiColor accentColor)
    {
        int hi = IconSize * SuperSample;

        using var canvas = new GdiBitmap(hi, hi, GdiPixelFormat.Format32bppArgb);
        using (var g = GdiGraphics.FromImage(canvas))
        {
            g.Clear(GdiColor.Transparent);
            g.ScaleTransform(SuperSample, SuperSample);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            switch (_currentStyle)
            {
                case "ring":
                    DrawRingIcon(g, canvas, percent, charging, accentColor);
                    break;
                case "number":
                    DrawNumberIcon(g, percent, charging, accentColor);
                    break;
                default: // "battery"
                    DrawBatteryIcon(g, canvas, percent, charging, accentColor);
                    break;
            }
        }

        var result = new GdiBitmap(IconSize, IconSize, GdiPixelFormat.Format32bppArgb);
        using (var g = GdiGraphics.FromImage(result))
        {
            g.Clear(GdiColor.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(canvas,
                        new Rectangle(0, 0, IconSize, IconSize),
                        new Rectangle(0, 0, hi, hi),
                        GraphicsUnit.Pixel);
        }

        return result;
    }

    // ========================================================================
    // 样式 1：纯图形经典圆角横向电池胶囊 (Battery) - 纯净无文字杂质，极致清爽原生
    // ========================================================================
    private static void DrawBatteryIcon(GdiGraphics g, GdiBitmap bmp, int percent, bool charging, GdiColor accentColor)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 纯正横向电池长宽比（宽13.5 x 高11.0），居中于 16x16
        float bodyX = 0f;
        float bodyY = 2.5f;
        float bodyW = 13.5f;
        float bodyH = 11.0f;
        float radius = 2.5f;

        GdiColor shellColor = GdiColor.FromArgb(235, 60, 70, 80);

        // 1. 电池外壳（平滑 Win11 优雅圆角外框）
        using (var shellPath = CreateRoundedPath(bodyX, bodyY, bodyW, bodyH, radius))
        {
            using var shellPen = new GdiPen(shellColor, 1.0f);
            g.DrawPath(shellPen, shellPath);
        }

        // 2. 正极端子凸起 (右侧带圆角端子)
        using (var capPath = CreateRoundedPath(14f, 5.0f, 1.8f, 6.0f, 1.0f))
        {
            using var capBrush = new GdiBrush(shellColor);
            g.FillPath(capBrush, capPath);
        }

        // 3. 内部纯色电量填充 (带内圆角)
        if (percent > 0)
        {
            int step = Math.Clamp((int)Math.Round(percent / 10.0) * 10, 0, 100);
            if (step == 0) step = 10;
            float fillW = Math.Max(2.5f, 11.5f * (step / 100.0f));

            using var fillPath = CreateRoundedPath(1.0f, 3.5f, fillW, 9.0f, 1.8f);
            using var fillBrush = new GdiBrush(accentColor);
            g.FillPath(fillBrush, fillPath);
        }

        // 若充电中，在电池内部居中绘制精致闪电符号
        if (charging)
        {
            DrawBolt(bmp, 5, 4, GdiColor.White);
        }
    }

    // ========================================================================
    // 样式 2：纯图形加粗环形电量进度 (Ring) - 加粗至 3.0px，高饱满度仪表盘
    // ========================================================================
    private static void DrawRingIcon(GdiGraphics g, GdiBitmap bmp, int percent, bool charging, GdiColor accentColor)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 环形外廓：严格以 (7.5, 7.5) 为中心对称放置，大幅加粗至 3.0px 笔画
        float cx = 7.5f;
        float cy = 7.5f;
        float r = 5.8f;
        float stroke = 3.0f;
        var box = new GdiRectangleF(cx - r, cy - r, r * 2, r * 2);

        // 1. 底轨 (微透半透明轨道环)
        GdiColor trackColor = ThemeService.IsSystemDark()
            ? GdiColor.FromArgb(60, 255, 255, 255)
            : GdiColor.FromArgb(45, 0, 0, 0);

        using (var trackPen = new GdiPen(trackColor, stroke))
        {
            g.DrawEllipse(trackPen, box);
        }

        // 2. 环形电量进度弧线 (顺时针旋转，末端带圆角胶囊收口)
        if (percent > 0)
        {
            float sweep = Math.Clamp(360.0f * (percent / 100.0f), 6.0f, 360.0f);
            using var arcPen = new GdiPen(accentColor, stroke)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawArc(arcPen, box, -90.0f, sweep);
        }

        // 若充电中，在环形中央叠加上闪电符号
        if (charging)
        {
            DrawBolt(bmp, 6, 4, accentColor);
        }
    }

    // ========================================================================
    // 样式 3：纯数字电量 (Number)
    // ========================================================================
    private static void DrawNumberIcon(GdiGraphics g, int percent, bool charging, GdiColor accentColor)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        // 1. 上半部大号粗体数字 (电量状态色)
        // 字号按位数分档，取「16px 宽度内确实放得下」的最大值（实测 DrawString 宽度）：
        //   "100" @7.5px ≈ 15.8px 放得下；@8px 已 16.9px 会裁掉末位。
        //   "82"  @10px  ≈ 15.2px 放得下；@10.5px 就已超宽。
        //   "9"   @14px  ≈ 13.0px 宽、垂直 y=1..11 刚好不触顶；@16px 会顶到 y=0 被裁。
        string text = percent >= 0 ? percent.ToString() : "--";
        float fontSize = text.Length >= 3 ? 7.5f : (text.Length == 2 ? 10.0f : 14.0f);
        using var font = new GdiFont("Segoe UI", fontSize, GdiFontStyle.Bold, GraphicsUnit.Pixel);

        using var sf = new GdiStringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };

        // 文字区尽量向上占满，底部 2px 留给电量比例横轨
        var textRect = new GdiRectangleF(0, 0, 16, 13.5f);
        using (var textBrush = new GdiBrush(accentColor))
        {
            g.DrawString(text, font, textBrush, textRect, sf);
        }

        // 2. 底部 2px 细微电量比例横轨
        GdiColor trackColor = ThemeService.IsSystemDark()
            ? GdiColor.FromArgb(60, 255, 255, 255)
            : GdiColor.FromArgb(40, 0, 0, 0);

        using (var trackBrush = new GdiBrush(trackColor))
        {
            g.FillRectangle(trackBrush, 0, 14, 16, 2);
        }

        if (percent > 0)
        {
            int fillW = Math.Max(2, (int)Math.Round(16.0 * Math.Clamp(percent / 100.0, 0, 1)));
            using var fillBrush = new GdiBrush(accentColor);
            g.FillRectangle(fillBrush, 0, 14, fillW, 2);
        }
    }

    // ========================================================================
    // 1-Bit 像素字库与图形辅助
    // ========================================================================
    private static readonly string[] BoltPattern =
    {
        "..#",
        ".##",
        "###",
        ".#.",
        "##.",
        "#..",
        "..."
    };

    /// <summary>
    /// 在「逻辑坐标」（16×16 网格）上填充一个像素。
    /// 实际画布是 IconSize*SuperSample 见方，所以这里按倍率铺成方块，
    /// 否则单像素只会落在高分辨率画布的一角，降采样后几乎消失。
    /// </summary>
    private static void FillLogicalPixel(GdiBitmap bmp, int logicalX, int logicalY, GdiColor color)
    {
        for (int dy = 0; dy < SuperSample; dy++)
        {
            for (int dx = 0; dx < SuperSample; dx++)
            {
                int x = (logicalX * SuperSample) + dx;
                int y = (logicalY * SuperSample) + dy;
                if (x >= 0 && x < bmp.Width && y >= 0 && y < bmp.Height)
                {
                    bmp.SetPixel(x, y, color);
                }
            }
        }
    }

    private static void DrawBolt(GdiBitmap bmp, int sx, int sy, GdiColor color)
    {
        for (int r = 0; r < 7; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                if (BoltPattern[r][c] == '#')
                {
                    int px = sx + c;
                    int py = sy + r;
                    if (px >= 0 && px < IconSize && py >= 0 && py < IconSize)
                    {
                        FillLogicalPixel(bmp, px, py, color);
                    }
                }
            }
        }
    }

    // 3x8 完美偶数对称字模（高8px，在 16px 画布中上下各精确留白 4px，水平双字间距 2px 达到左右各 4px 绝对对称）
    private static readonly string[] Digit0_3x8 = { "###", "#.#", "#.#", "#.#", "#.#", "#.#", "#.#", "###" };
    private static readonly string[] Digit1_3x8 = { ".#.", "##.", ".#.", ".#.", ".#.", ".#.", ".#.", "###" };
    private static readonly string[] Digit2_3x8 = { "###", "..#", "..#", "###", "###", "#..", "#..", "###" };
    private static readonly string[] Digit3_3x8 = { "###", "..#", "..#", "###", "..#", "..#", "..#", "###" };
    private static readonly string[] Digit4_3x8 = { "#.#", "#.#", "#.#", "###", "###", "..#", "..#", "..#" };
    private static readonly string[] Digit5_3x8 = { "###", "#..", "#..", "###", "..#", "..#", "..#", "###" };
    private static readonly string[] Digit6_3x8 = { "###", "#..", "#..", "###", "#.#", "#.#", "#.#", "###" };
    private static readonly string[] Digit7_3x8 = { "###", "..#", "..#", "..#", "..#", "..#", "..#", "..#" };
    private static readonly string[] Digit8_3x8 = { "###", "#.#", "#.#", "###", "#.#", "#.#", "#.#", "###" };
    private static readonly string[] Digit9_3x8 = { "###", "#.#", "#.#", "###", "..#", "..#", "..#", "###" };
    private static readonly string[] DigitDash_3x8 = { "...", "...", "...", "###", "###", "...", "...", "..." };

    private static void DrawPixelDigitsCentered(GdiBitmap bmp, string text, GdiColor color)
    {
        if (text.Length == 2)
        {
            // 双位数：字1在 x=4..6, 字2在 x=9..11, y=4..11 (上下左右各 4px 绝对同心对称居中)
            Draw3x8Glyph(bmp, 4, 4, text[0], color);
            Draw3x8Glyph(bmp, 9, 4, text[1], color);
        }
        else if (text.Length == 1)
        {
            Draw3x8Glyph(bmp, 6, 4, text[0], color);
        }
        else if (text.Length >= 3)
        {
            Draw3x8Glyph(bmp, 3, 4, '1', color);
            Draw3x8Glyph(bmp, 7, 4, '0', color);
            Draw3x8Glyph(bmp, 11, 4, '0', color);
        }
    }

    private static void Draw3x8Glyph(GdiBitmap bmp, int startX, int startY, char ch, GdiColor color)
    {
        string[]? glyph = ch switch
        {
            '0' => Digit0_3x8,
            '1' => Digit1_3x8,
            '2' => Digit2_3x8,
            '3' => Digit3_3x8,
            '4' => Digit4_3x8,
            '5' => Digit5_3x8,
            '6' => Digit6_3x8,
            '7' => Digit7_3x8,
            '8' => Digit8_3x8,
            '9' => Digit9_3x8,
            _ => DigitDash_3x8
        };

        if (glyph == null) return;
        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                if (glyph[r][c] == '#')
                {
                    int px = startX + c;
                    int py = startY + r;
                    if (px >= 0 && px < IconSize && py >= 0 && py < IconSize)
                    {
                        FillLogicalPixel(bmp, px, py, color);
                    }
                }
            }
        }
    }

    private static readonly string[] Digit0 = { "###", "#.#", "#.#", "#.#", "#.#", "#.#", "###" };
    private static readonly string[] Digit1 = { ".#.", "##.", ".#.", ".#.", ".#.", ".#.", "###" };
    private static readonly string[] Digit2 = { "###", "..#", "..#", "###", "#..", "#..", "###" };
    private static readonly string[] Digit3 = { "###", "..#", "..#", "###", "..#", "..#", "###" };
    private static readonly string[] Digit4 = { "#.#", "#.#", "#.#", "###", "..#", "..#", "..#" };
    private static readonly string[] Digit5 = { "###", "#..", "#..", "###", "..#", "..#", "###" };
    private static readonly string[] Digit6 = { "###", "#..", "#..", "###", "#.#", "#.#", "###" };
    private static readonly string[] Digit7 = { "###", "..#", "..#", "..#", "..#", "..#", "..#" };
    private static readonly string[] Digit8 = { "###", "#.#", "#.#", "###", "#.#", "#.#", "###" };
    private static readonly string[] Digit9 = { "###", "#.#", "#.#", "###", "..#", "..#", "###" };
    private static readonly string[] DigitDash = { "...", "...", "...", "###", "...", "...", "..." };

    private static void DrawPixelDigits(GdiBitmap bmp, int startX, int startY, string text, GdiColor color)
    {
        int curX = startX;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            string[]? glyph = ch switch
            {
                '0' => Digit0,
                '1' => Digit1,
                '2' => Digit2,
                '3' => Digit3,
                '4' => Digit4,
                '5' => Digit5,
                '6' => Digit6,
                '7' => Digit7,
                '8' => Digit8,
                '9' => Digit9,
                '-' => DigitDash,
                _ => null
            };

            if (glyph != null)
            {
                for (int r = 0; r < 7; r++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        if (glyph[r][c] == '#')
                        {
                            int px = curX + c;
                            int py = startY + r;
                            if (px >= 0 && px < IconSize && py >= 0 && py < IconSize)
                            {
                                FillLogicalPixel(bmp, px, py, color);
                            }
                        }
                    }
                }
                curX += 4;
            }
        }
    }

    private static GraphicsPath CreateRoundedPath(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }
}
