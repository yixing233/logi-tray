using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MouseBatteryTray.Lite;

/// <summary>
/// 轻量版详情卡片：电量、状态、续航预测与 24 小时柱状图。
///
/// 用普通 WinForms 无边框窗口实现，不做亚克力效果——这正是它省内存的原因。
/// 定位逻辑与完整版一致：贴在托盘图标上方，并避开「隐藏图标」浮出面板。
/// </summary>
internal sealed class DetailsForm : Form
{
    private const int CardWidth = 232;
    private const int SpacingAboveTaskbar = 8;

    private readonly Font _titleFont = new("Microsoft YaHei UI", 11f, FontStyle.Bold);
    private readonly Font _bigFont = new("Microsoft YaHei UI", 26f, FontStyle.Bold);
    private readonly Font _labelFont = new("Microsoft YaHei UI", 8.5f);
    private readonly Font _valueFont = new("Microsoft YaHei UI", 9f, FontStyle.Bold);
    private readonly Font _tinyFont = new("Microsoft YaHei UI", 7.5f);

    private readonly Action _onOpenSettings;

    private readonly Label _deviceLabel;
    private readonly Label _percentLabel;
    private readonly Label _statusLabel;
    private readonly Label _remainingLabel;
    private readonly Label _rangeLabel;
    private readonly ChartPanel _chart;
    private readonly Button _settingsButton;

    private BatterySnapshot? _lastSnapshot;

    public DetailsForm(Action onOpenSettings)
    {
        _onOpenSettings = onOpenSettings;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Width = CardWidth;
        BackColor = Color.White;
        DoubleBuffered = true;
        KeyPreview = true;

        bool dark = AppPalette.IsSystemDark();
        ApplyTheme(dark);

        // ---- 顶部：设备名 + 设置按钮 ----
        _deviceLabel = new Label
        {
            AutoSize = false,
            Font = _titleFont,
            Text = "罗技设备",
            Location = new Point(14, 12),
            Size = new Size(CardWidth - 52, 22),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _settingsButton = new Button
        {
            Text = "⚙",
            Font = new Font("Segoe UI Symbol", 10f),
            FlatStyle = FlatStyle.Flat,
            Size = new Size(26, 24),
            Location = new Point(CardWidth - 38, 11),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        _settingsButton.FlatAppearance.BorderSize = 0;
        _settingsButton.Click += (_, _) => _onOpenSettings();

        // ---- 大号电量 ----
        _percentLabel = new Label
        {
            AutoSize = false,
            Font = _bigFont,
            Text = "--",
            Location = new Point(12, 38),
            Size = new Size(120, 46),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _statusLabel = new Label
        {
            AutoSize = false,
            Font = _valueFont,
            Text = "放电中",
            Location = new Point(126, 44),
            Size = new Size(CardWidth - 140, 18),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _remainingLabel = new Label
        {
            AutoSize = false,
            Font = _labelFont,
            Text = "",
            Location = new Point(126, 63),
            Size = new Size(CardWidth - 140, 18),
            TextAlign = ContentAlignment.MiddleLeft
        };

        // ---- 分隔线 ----
        var divider = new Panel
        {
            Height = 1,
            Location = new Point(14, 92),
            Width = CardWidth - 28
        };

        // ---- 24 小时概览 ----
        var chartTitle = new Label
        {
            AutoSize = true,
            Font = _labelFont,
            Text = "24 小时电量",
            Location = new Point(14, 100)
        };

        _rangeLabel = new Label
        {
            AutoSize = false,
            Font = _tinyFont,
            Text = "",
            Location = new Point(100, 101),
            Size = new Size(CardWidth - 114, 16),
            TextAlign = ContentAlignment.MiddleRight
        };

        _chart = new ChartPanel
        {
            Location = new Point(14, 120),
            Size = new Size(CardWidth - 28, 62)
        };

        Controls.AddRange(new Control[]
        {
            _deviceLabel, _settingsButton, _percentLabel, _statusLabel,
            _remainingLabel, divider, chartTitle, _rangeLabel, _chart
        });

        ApplyThemeToControls(dark);

        // 内容高度固定，宽度固定：定位时直接可用
        ClientSize = new Size(CardWidth, 194);

        // 点击卡片外部自动收起（与完整版一致的行为）
        Deactivate += (_, _) => Hide();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Hide();
            }
        };
    }

    private bool _dark;

    private void ApplyTheme(bool dark)
    {
        _dark = dark;
        BackColor = dark ? Color.FromArgb(32, 32, 32) : Color.White;
    }

    private Color TextPrimary => _dark ? Color.FromArgb(248, 250, 252) : Color.FromArgb(15, 23, 42);
    private Color TextSecondary => _dark ? Color.FromArgb(158, 172, 190) : Color.FromArgb(51, 65, 85);
    private Color TextFaint => _dark ? Color.FromArgb(110, 126, 148) : Color.FromArgb(100, 116, 139);

    private void ApplyThemeToControls(bool dark)
    {
        _deviceLabel.ForeColor = TextPrimary;
        _percentLabel.ForeColor = TextPrimary;
        _statusLabel.ForeColor = TextSecondary;
        _remainingLabel.ForeColor = TextFaint;
        _rangeLabel.ForeColor = TextFaint;

        foreach (Control c in Controls)
        {
            if (c is Label lbl && lbl.Font == _labelFont)
            {
                lbl.ForeColor = TextSecondary;
            }
        }

        _settingsButton.BackColor = dark ? Color.FromArgb(48, 48, 48) : Color.FromArgb(242, 244, 247);
        _settingsButton.ForeColor = TextSecondary;
        _settingsButton.FlatAppearance.MouseOverBackColor =
            dark ? Color.FromArgb(62, 62, 62) : Color.FromArgb(228, 232, 238);

        _chart.Dark = dark;
    }

    /// <summary>用最新的电量快照刷新界面。</summary>
    public void UpdateData(BatterySnapshot snapshot)
    {
        _lastSnapshot = snapshot;

        _deviceLabel.Text = string.IsNullOrWhiteSpace(snapshot.DeviceName) ? "罗技设备" : snapshot.DeviceName;

        if (snapshot.Percent >= 0)
        {
            _percentLabel.Text = $"{snapshot.Percent}%";
            var c = AppPalette.GetBatteryColor(snapshot.Percent, snapshot.IsCharging);
            _percentLabel.ForeColor = Color.FromArgb(c.R, c.G, c.B);
        }
        else
        {
            _percentLabel.Text = "--";
            _percentLabel.ForeColor = TextFaint;
        }

        _statusLabel.Text = !snapshot.IsConnected
            ? "已休眠"
            : (snapshot.IsCharging ? "充电中" : "放电中");

        // 充电时续航文案本身就是「充电中」，与状态行重复，这里留空避免出现两遍
        bool remainingDuplicatesStatus = snapshot.IsCharging &&
            string.Equals(snapshot.RemainingTimeText?.Trim(), "充电中", StringComparison.Ordinal);

        _remainingLabel.Text = string.IsNullOrWhiteSpace(snapshot.RemainingTimeText) || remainingDuplicatesStatus
            ? ""
            : snapshot.RemainingTimeText;

        if (snapshot.MinPercent >= 0 && snapshot.MaxPercent >= 0)
        {
            _rangeLabel.Text = $"最低 {snapshot.MinPercent}% · 最高 {snapshot.MaxPercent}%";
        }
        else
        {
            _rangeLabel.Text = "";
        }

        _chart.SetBuckets(snapshot.HourlyBuckets);
        _chart.Invalidate();
    }

    /// <summary>
    /// 显示在托盘图标上方。若图标位于「隐藏图标」浮出面板中，
    /// 则上移到面板之上，避免遮住托盘图标本身。
    /// </summary>
    public void ShowNear(Point anchor)
    {
        try
        {
            var screen = Screen.FromPoint(anchor);
            var wa = screen.WorkingArea;

            int left = anchor.X - (Width / 2);
            left = Math.Max(wa.Left + 4, Math.Min(left, wa.Right - Width - 4));

            int top = wa.Bottom - SpacingAboveTaskbar - Height;

            if (UnmanagedMethods.TryGetOverflowPanelRect(out var panel) &&
                panel.Right > panel.Left && panel.Bottom > panel.Top)
            {
                bool overlaps = left < panel.Right && left + Width > panel.Left;
                if (overlaps)
                {
                    top = panel.Top - 8 - Height;
                    int panelCenter = (panel.Left + panel.Right) / 2;
                    left = panelCenter - (Width / 2);
                    left = Math.Max(wa.Left + 4, Math.Min(left, wa.Right - Width - 4));
                }
            }

            Location = new Point(left, Math.Max(wa.Top + 4, top));
        }
        catch
        {
            Location = new Point(anchor.X - Width / 2, anchor.Y - Height - 8);
        }

        Show();
        BringToFront();
        Activate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);

        if (!Visible)
        {
            return;
        }

        // Deactivate 在窗口刚显示、尚未拿到焦点时也会触发，
        // 因此延后一拍再判断，避免一显示就立刻自动收起。
        BeginInvoke(new Action(() =>
        {
            if (!IsDisposed && Visible && !ContainsFocus)
            {
                Hide();
            }
        }));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _bigFont.Dispose();
            _labelFont.Dispose();
            _valueFont.Dispose();
            _tinyFont.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>24 小时柱状图。自绘以避免引入图表库。</summary>
    private sealed class ChartPanel : Panel
    {
        private List<HourlyBucket> _buckets = new();

        public bool Dark { get; set; }

        public ChartPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        public void SetBuckets(List<HourlyBucket> buckets)
        {
            _buckets = buckets ?? new List<HourlyBucket>();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            const int chartHeight = 42;
            const float barWidth = 7f;
            const float gap = 2f;
            int baseline = Height - 18;

            if (_buckets.Count == 0)
            {
                using var emptyBrush = new SolidBrush(
                    Dark ? Color.FromArgb(110, 126, 148) : Color.FromArgb(100, 116, 139));
                using var f = new Font("Microsoft YaHei UI", 8f);
                g.DrawString("暂无历史记录", f, emptyBrush, new PointF(0, 8));
                return;
            }

            // 基线：低电量区间（20..45）时抬升基线以放大差异，与完整版规则一致
            int minP = int.MaxValue, maxP = int.MinValue;
            foreach (var b in _buckets)
            {
                if (b.Percent > 0)
                {
                    if (b.Percent < minP) minP = b.Percent;
                    if (b.Percent > maxP) maxP = b.Percent;
                }
            }

            int baseLineValue = (maxP - minP <= 25 && minP >= 20) ? Math.Max(0, minP - 15) : 0;
            int rangeSpan = Math.Max(1, 100 - baseLineValue);

            float x = 0;
            foreach (var bucket in _buckets)
            {
                if (bucket.Percent > 0)
                {
                    double norm = Math.Clamp((bucket.Percent - baseLineValue) / (double)rangeSpan, 0.12, 1.0);
                    float h = (float)Math.Clamp(chartHeight * norm, 4.0, chartHeight);

                    var c = AppPalette.GetBatteryColor(bucket.Percent, false);
                    int alpha = bucket.IsSleeping ? 97 : 255;   // 休眠柱降低不透明度
                    using var brush = new SolidBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
                    g.FillRectangle(brush, x, baseline - h, barWidth, h);
                }
                else
                {
                    using var brush = new SolidBrush(
                        Dark ? Color.FromArgb(110, 126, 148) : Color.FromArgb(160, 170, 185));
                    g.FillRectangle(brush, x, baseline - 2, barWidth, 2);
                }

                x += barWidth + gap;
            }

            // 底部时间刻度
            using var labelBrush = new SolidBrush(
                Dark ? Color.FromArgb(110, 126, 148) : Color.FromArgb(100, 116, 139));
            using var tiny = new Font("Microsoft YaHei UI", 7f);
            g.DrawString("-24h", tiny, labelBrush, new PointF(0, baseline + 2));

            string nowText = "现在";
            var size = g.MeasureString(nowText, tiny);
            g.DrawString(nowText, tiny, labelBrush,
                new PointF(Width - size.Width, baseline + 2));
        }
    }
}
