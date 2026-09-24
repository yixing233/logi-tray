using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace MouseBatteryTray.Lite;

/// <summary>
/// 轻量版托盘图标：只提供「纯数字」样式。
///
/// 与该样式在完整版中的实现保持同一套视觉参数（4 倍超采样、按位数分档字号、
/// 底部 2px 电量横轨），因此两个版本的托盘图标看起来完全一致。
/// </summary>
internal static class TrayIconRenderer
{
    /// <summary>
    /// 超采样倍率。直接在 16×16 上画抗锯齿文字笔画太少，边缘会发毛；
    /// 在 4 倍画布上绘制再降采样，相当于 16 倍采样。
    /// </summary>
    private const int SuperSample = 4;

    /// <summary>托盘图标的目标边长（像素）。</summary>
    private const int IconSize = 16;

    /// <summary>渲染图标。调用方负责在使用后销毁返回的 Icon。</summary>
    public static Icon Render(int percent, bool charging, bool darkBackground)
    {
        int hi = IconSize * SuperSample;

        using var canvas = new Bitmap(hi, hi, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Transparent);
            g.ScaleTransform(SuperSample, SuperSample);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            Draw(g, percent, charging, darkBackground);
        }

        using var result = new Bitmap(IconSize, IconSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(canvas,
                        new Rectangle(0, 0, IconSize, IconSize),
                        new Rectangle(0, 0, hi, hi),
                        GraphicsUnit.Pixel);
        }

        return Icon.FromHandle(result.GetHicon());
    }

    private static void Draw(Graphics g, int percent, bool charging, bool darkBackground)
    {
        var accent = AppPalette.GetBatteryColor(percent, charging);
        using var accentBrush = new SolidBrush(Color.FromArgb(accent.R, accent.G, accent.B));

        // 1. 大号粗体数字
        // 字号按位数分档，取「16px 宽度内确实放得下」的最大值（实测 DrawString 宽度）：
        //   "100" @7.5px ≈ 15.8px 放得下；@8px 已 16.9px 会裁掉末位。
        //   "82"  @10px  ≈ 15.2px 放得下；@10.5px 就已超宽。
        //   "9"   @14px  ≈ 13.0px 宽、垂直 y=1..11 刚好不触顶；@16px 会顶到 y=0 被裁。
        string text = percent >= 0 ? percent.ToString() : "--";
        float fontSize = text.Length >= 3 ? 7.5f : (text.Length == 2 ? 10.0f : 14.0f);
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);

        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };

        // 文字区尽量向上占满，底部 2px 留给电量比例横轨
        var textRect = new RectangleF(0, 0, IconSize, 13.5f);
        g.DrawString(text, font, accentBrush, textRect, sf);

        // 2. 底部 2px 细微电量比例横轨
        Color trackColor = darkBackground
            ? Color.FromArgb(60, 255, 255, 255)
            : Color.FromArgb(40, 0, 0, 0);

        using (var trackBrush = new SolidBrush(trackColor))
        {
            g.FillRectangle(trackBrush, 0, 14, IconSize, 2);
        }

        if (percent > 0)
        {
            int fillW = Math.Max(2, (int)Math.Round(IconSize * Math.Clamp(percent / 100.0, 0, 1)));
            g.FillRectangle(accentBrush, 0, 14, fillW, 2);
        }
    }
}
