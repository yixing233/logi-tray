using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using MouseBatteryTray;

namespace MultiTray;

/// <summary>
/// 托盘图标渲染。
///
/// 设计取向与用户的需求一致：**显示电量最低的那台设备**。
/// 多设备时一眼看到最需要注意的那台，正是低电量预警的核心诉求。
///
/// 图标样式沿用罗技版已验证的实现方式：
///   * 4 倍超采样后高质量缩放到 16px，避免细笔画出现锯齿
///   * 三位数、两位数、一位数分别用不同字号，保证 "100%" 不被裁掉
/// </summary>
internal static class TrayIconRenderer
{
    private const int IconSize = 16;
    private const int SuperSample = 4;

    /// <summary>把一组读数画成托盘图标。列表为空时画「无设备」。</summary>
    public static Icon Render(IReadOnlyList<DeviceReading> readings, bool dark)
    {
        int size = IconSize * SuperSample;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // 在「16 单位」坐标系里绘制，由变换放大到超采样画布。
            //
            // 这一步不能省：早先直接在 64×64 画布上用为 16px 设计的字号绘制，
            // 缩回 16px 后数字只有约 2px 高，几乎看不见（截图核对时才发现）。
            // 加上缩放后，字号与几何尺寸都能按 16 单位直观书写。
            g.ScaleTransform(SuperSample, SuperSample);
            Draw(g, IconSize, readings, dark);
        }

        using var scaled = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using (var g2 = Graphics.FromImage(scaled))
        {
            g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g2.DrawImage(bmp, new Rectangle(0, 0, IconSize, IconSize));
        }

        IntPtr h = scaled.GetHicon();
        try
        {
            // FromHandle 不接管句柄所有权，必须自己克隆一份再销毁原句柄，
            // 否则每次刷新都会泄漏一个 HICON（罗技版就踩过这个坑）。
            using var tmp = Icon.FromHandle(h);
            var copy = (Icon)tmp.Clone();
            return copy;
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static void Draw(Graphics g, int size, IReadOnlyList<DeviceReading> readings,
                             bool dark)
    {
        // 选出要显示的那台：优先在线且有电量，其中取电量最低者
        var online = readings.Where(r => r.IsOnline && r.Percent >= 0).ToList();

        if (online.Count == 0)
        {
            DrawNoData(g, size, readings.Count > 0, dark);
            return;
        }

        var target = online.OrderBy(r => r.Percent).First();
        bool showBadge = online.Count > 1;

        DrawBattery(g, size, target, dark, showBadge, online.Count);
    }

    private static void DrawBattery(Graphics g, int size, DeviceReading r, bool dark,
                                    bool showBadge, int deviceCount)
    {
        var rgb = AppPalette.GetBatteryColor(r.Percent, r.IsCharging);
        var color = Color.FromArgb(rgb.R, rgb.G, rgb.B);

        // 全部坐标以 16 为单位（调用方已按超采样倍数放大）。
        //
        // 字号沿用轻量版经过实机截图核对过的取值：
        //   * 三位数 7.5px —— TextRenderer.MeasureText 会把 "100" 量成 20px，
        //     看似放不下，但 DrawString 居中绘制时实际可用满 16px 宽度，
        //     7.5px 能完整显示 "100"（早先用 8px 时被裁成 "10"，已修正）。
        //   * 两位数 10px、一位数 14px，保证清晰度。
        string text = r.Percent.ToString();
        float fontSize = text.Length >= 3 ? 7.5f : (text.Length == 2 ? 10.0f : 14.0f);

        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        // 文字区高度 13.5，底部 2.5 留给电量条
        var rect = new RectangleF(0, 0, size, 13.5f);
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            // 不允许自动换行，避免 "100" 被折成两行
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using var textBrush = new SolidBrush(color);
        g.DrawString(text, font, textBrush, rect, sf);

        // 底部电量条：直观给出剩余比例
        const float barY = 14f;
        const float barH = 2f;
        float frac = Math.Clamp(r.Percent / 100f, 0f, 1f);
        float full = size * frac;

        using var trackBrush = new SolidBrush(dark
            ? Color.FromArgb(110, 110, 110)
            : Color.FromArgb(200, 200, 200));
        g.FillRectangle(trackBrush, 0f, barY, size, barH);
        if (full > 0.5f)
        {
            using var fillBrush = new SolidBrush(color);
            g.FillRectangle(fillBrush, 0f, barY, full, barH);
        }

        // 多设备时右上角画一个小点，提示「这是最低的那台，还有其它设备」
        if (showBadge && deviceCount > 1)
        {
            const float mark = 3.4f;
            using var markBrush = new SolidBrush(dark
                ? Color.FromArgb(250, 250, 250)
                : Color.FromArgb(45, 45, 45));
            g.FillEllipse(markBrush, size - mark - 0.5f, 0.5f, mark, mark);
        }
    }

    private static void DrawNoData(Graphics g, int size, bool hasDevices, bool dark)
    {
        var color = dark ? Color.FromArgb(190, 190, 190) : Color.FromArgb(110, 110, 110);
        if (!hasDevices)
        {
            using var font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("?", font, brush, new RectangleF(0, -0.5f, size, size), sf);
            return;
        }

        // 有设备但都读不到 → 画一条横线表示离线
        using var pen = new Pen(color, 2.0f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        float y = size / 2f;
        g.DrawLine(pen, size * 0.22f, y, size * 0.78f, y);
    }
}
