using System;
using System.Drawing;
using System.Windows.Forms;

namespace MouseBatteryTray.Lite;

/// <summary>
/// 轻量版设置窗口：低电量阈值、严重低电量阈值、桌面通知、开机自启、
/// 后台刷新间隔，以及「关于」二级页面入口。
///
/// 与完整版不同，这里不提供托盘样式与主题切换（轻量版固定为数字图标 + 跟随系统）。
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly SettingsConfig _config;
    private readonly Action<SettingsConfig> _onSaved;

    private readonly TrackBar _lowSlider;
    private readonly TrackBar _critSlider;
    private readonly Label _lowValue;
    private readonly Label _critValue;
    private readonly CheckBox _notifyToggle;
    private readonly CheckBox _autoStartToggle;
    private readonly Button[] _intervalButtons;

    private int _selectedInterval;
    private bool _loading = true;

    private static readonly (int Seconds, string Label)[] Intervals =
    {
        (10, "10 秒"),
        (15, "15 秒"),
        (30, "30 秒"),
        (60, "1 分"),
        (120, "2 分")
    };

    private readonly bool _dark;

    public SettingsForm(SettingsConfig config, Action<SettingsConfig> onSaved)
    {
        _config = config;
        _onSaved = onSaved;
        _dark = AppPalette.IsSystemDark();

        Text = "logi-tray-lite · 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // 高度按布局累计值给足，避免底部按钮行被裁掉
        ClientSize = new Size(410, 496);
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = _dark ? Color.FromArgb(32, 32, 32) : Color.White;
        ForeColor = TextPrimary;

        try
        {
            string ico = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (System.IO.File.Exists(ico))
            {
                Icon = new Icon(ico);
            }
        }
        catch { }

        int y = 16;

        // ================= 电量阈值与提醒 =================
        Controls.Add(MakeGroupHeader("电量阈值与提醒", ref y));

        var lowPanel = MakeCard(y, 88);
        lowPanel.Controls.Add(MakeLabel("低电量提醒阈值", 12, 10, bold: true));

        _lowValue = MakeBadge(lowPanel, 8);
        lowPanel.Controls.Add(_lowValue);

        _lowSlider = MakeSlider(lowPanel, 10, 32, 5, 50, config.LowThreshold);
        _lowSlider.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            _lowValue.Text = $"{_lowSlider.Value}%";
            EnforceThresholdOrder();
        };
        lowPanel.Controls.Add(_lowSlider);
        var lowMin = MakeScaleLabel("5%", 12, 64, false);
        var lowMax = MakeScaleLabel("50%", 12, 64, true);
        lowPanel.Controls.Add(lowMin);
        lowPanel.Controls.Add(lowMax);
        // TrackBar 是不透明控件，后加入的标签会被它盖住，必须提到最前
        lowMin.BringToFront();
        lowMax.BringToFront();
        Controls.Add(lowPanel);
        y += 96;

        var critPanel = MakeCard(y, 88);
        critPanel.Controls.Add(MakeLabel("严重低电量警告阈值", 12, 10, bold: true));
        _critValue = MakeBadge(critPanel, 8);
        critPanel.Controls.Add(_critValue);

        _critSlider = MakeSlider(critPanel, 10, 32, 5, 30, config.CriticalThreshold);
        _critSlider.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            _critValue.Text = $"{_critSlider.Value}%";
            EnforceThresholdOrder();
        };
        critPanel.Controls.Add(_critSlider);
        var critMin = MakeScaleLabel("5%", 12, 64, false);
        var critMax = MakeScaleLabel("30%", 12, 64, true);
        critPanel.Controls.Add(critMin);
        critPanel.Controls.Add(critMax);
        critMin.BringToFront();
        critMax.BringToFront();
        Controls.Add(critPanel);
        y += 96;

        var notifyPanel = MakeCard(y, 44);
        notifyPanel.Controls.Add(MakeLabel("启用低电量桌面通知", 12, 13, bold: false));
        _notifyToggle = new CheckBox
        {
            Checked = config.NotifyEnabled,
            Appearance = Appearance.Button,
            FlatStyle = FlatStyle.Flat,
            Text = "",
            Size = new Size(46, 22),
            Location = new Point(notifyPanel.Width - 58, 11),
            Cursor = Cursors.Hand
        };
        StyleToggle(_notifyToggle);
        notifyPanel.Controls.Add(_notifyToggle);
        // 控件须先有父容器，StyleToggle 才能同步父容器底色
        _notifyToggle.BackColor = CardBack;
        Controls.Add(notifyPanel);
        y += 54;

        // ================= 启动 =================
        Controls.Add(MakeGroupHeader("启动", ref y));

        var autoPanel = MakeCard(y, 44);
        autoPanel.Controls.Add(MakeLabel("开机自动启动", 12, 13, bold: false));
        _autoStartToggle = new CheckBox
        {
            Checked = AutoStartService.IsEnabled(),
            Appearance = Appearance.Button,
            FlatStyle = FlatStyle.Flat,
            Text = "",
            Size = new Size(46, 22),
            Location = new Point(autoPanel.Width - 58, 11),
            Cursor = Cursors.Hand
        };
        StyleToggle(_autoStartToggle);
        autoPanel.Controls.Add(_autoStartToggle);
        _autoStartToggle.BackColor = CardBack;
        Controls.Add(autoPanel);
        y += 54;

        // ================= 后台刷新间隔 =================
        Controls.Add(MakeGroupHeader("后台刷新间隔", ref y));

        var intervalPanel = MakeCard(y, 50);
        _intervalButtons = new Button[Intervals.Length];
        int bx = 10;
        int bw = (intervalPanel.Width - 20 - (Intervals.Length - 1) * 4) / Intervals.Length;
        for (int i = 0; i < Intervals.Length; i++)
        {
            var btn = new Button
            {
                Text = Intervals[i].Label,
                Size = new Size(bw, 28),
                Location = new Point(bx, 11),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Tag = Intervals[i].Seconds,
                Font = new Font("Microsoft YaHei UI", 8.5f)
            };
            btn.FlatAppearance.BorderSize = 1;
            int seconds = Intervals[i].Seconds;
            btn.Click += (_, _) =>
            {
                _selectedInterval = seconds;
                UpdateIntervalButtons();
            };
            _intervalButtons[i] = btn;
            intervalPanel.Controls.Add(btn);
            bx += bw + 4;
        }
        Controls.Add(intervalPanel);
        y += 62;

        // ================= 底部按钮 =================
        var aboutBtn = new Button
        {
            Text = "关于",
            Size = new Size(76, 32),
            Location = new Point(16, y),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        aboutBtn.FlatAppearance.BorderSize = 1;
        aboutBtn.Click += (_, _) => OpenAbout();
        Controls.Add(aboutBtn);

        var cancelBtn = new Button
        {
            Text = "取消",
            Size = new Size(76, 32),
            Location = new Point(ClientSize.Width - 190, y),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        cancelBtn.FlatAppearance.BorderSize = 1;
        cancelBtn.Click += (_, _) => Close();
        Controls.Add(cancelBtn);

        var saveBtn = new Button
        {
            Text = "保存设置",
            Size = new Size(96, 32),
            Location = new Point(ClientSize.Width - 108, y),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        saveBtn.FlatAppearance.BorderSize = 0;
        saveBtn.BackColor = Color.FromArgb(0, 120, 212);
        saveBtn.ForeColor = Color.White;
        saveBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(16, 110, 190);
        saveBtn.Click += (_, _) => SaveAndClose();
        Controls.Add(saveBtn);

        // 初始化控件状态
        _selectedInterval = config.Interval;
        if (Array.FindIndex(Intervals, t => t.Seconds == _selectedInterval) < 0)
        {
            _selectedInterval = 30;
        }

        _lowValue.Text = $"{_lowSlider.Value}%";
        _critValue.Text = $"{_critSlider.Value}%";
        UpdateIntervalButtons();
        StyleSecondaryButtons();
        _loading = false;
    }

    private Color TextPrimary => _dark ? Color.FromArgb(248, 250, 252) : Color.FromArgb(15, 23, 42);
    private Color TextSecondary => _dark ? Color.FromArgb(158, 172, 190) : Color.FromArgb(51, 65, 85);
    private Color CardBack => _dark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(246, 248, 250);
    private Color CardBorder => _dark ? Color.FromArgb(64, 64, 64) : Color.FromArgb(226, 230, 236);

    private Label MakeGroupHeader(string text, ref int y)
    {
        var lbl = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            ForeColor = _dark ? Color.FromArgb(96, 165, 250) : Color.FromArgb(0, 102, 184),
            Location = new Point(16, y)
        };
        y += 26;
        return lbl;
    }

    private Panel MakeCard(int y, int height)
    {
        var panel = new Panel
        {
            Location = new Point(16, y),
            Size = new Size(ClientSize.Width - 32, height),
            BackColor = CardBack
        };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(CardBorder);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };
        return panel;
    }

    private Label MakeLabel(string text, int x, int y, bool bold)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9f, bold ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = bold ? TextPrimary : TextSecondary,
            Location = new Point(x, y)
        };
    }

    private Label MakeScaleLabel(string text, int x, int y, bool rightAlign)
    {
        var lbl = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 8f),
            ForeColor = TextSecondary,
            Top = y
        };

        if (rightAlign)
        {
            // 右对齐依赖父容器宽度，加入父容器后再定位；
            // Top 必须立刻设置，否则会停在 y=0 并压到标题行的徽章上。
            lbl.HandleCreated += (_, _) =>
            {
                var parent = lbl.Parent;
                if (parent != null)
                {
                    lbl.Left = parent.Width - lbl.Width - x;
                }
            };
        }
        else
        {
            lbl.Left = x;
        }

        return lbl;
    }

    private Label MakeBadge(Panel parent, int y)
    {
        var lbl = new Label
        {
            Text = "20%",
            AutoSize = false,
            Size = new Size(50, 20),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(0, 102, 184),
            BackColor = _dark ? Color.FromArgb(30, 58, 90) : Color.FromArgb(224, 238, 252),
            Location = new Point(parent.Width - 62, y)
        };
        return lbl;
    }

    private TrackBar MakeSlider(Panel parent, int x, int y, int min, int max, int value)
    {
        var tb = new TrackBar
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            TickStyle = TickStyle.None,
            SmallChange = 1,
            LargeChange = 5,
            Location = new Point(x, y),
            Width = parent.Width - 20,
            Height = 26,
            BackColor = CardBack
        };
        return tb;
    }

    private void StyleToggle(CheckBox toggle)
    {
        // 用外观为按钮的 CheckBox 模拟开关：自己画圆角轨道 + 圆形滑块。
        // Button 外观在圆角之外仍会露出直角背景，必须把控件底色设为卡片色，
        // 并在绘制时先用卡片色铺满，否则会看到一圈浅灰方框。
        toggle.FlatAppearance.BorderSize = 0;
        toggle.BackColor = CardBack;
        toggle.FlatAppearance.MouseOverBackColor = CardBack;
        toggle.FlatAppearance.MouseDownBackColor = CardBack;

        // 触发重绘时连同父容器一起刷新，避免圆角边缘留下脏像素
        if (toggle.Parent != null)
        {
            toggle.Parent.BackColor = CardBack;
        }

        toggle.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.Clear(CardBack);

            bool on = toggle.Checked;
            var track = on
                ? Color.FromArgb(0, 120, 212)
                : (_dark ? Color.FromArgb(80, 80, 80) : Color.FromArgb(200, 205, 212));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = RoundedRect(new Rectangle(0, 0, toggle.Width - 1, toggle.Height - 1), toggle.Height / 2);
            using var brush = new SolidBrush(track);
            g.FillPath(brush, path);

            int knob = toggle.Height - 6;
            int kx = on ? toggle.Width - knob - 3 : 3;
            using var knobBrush = new SolidBrush(Color.White);
            g.FillEllipse(knobBrush, kx, 3, knob, knob);
        };
        toggle.CheckedChanged += (_, _) => toggle.Invalidate();
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>保证「严重」阈值不高于「低电量」阈值。</summary>
    private void EnforceThresholdOrder()
    {
        if (_critSlider.Value > _lowSlider.Value)
        {
            _critSlider.Value = _lowSlider.Value;
            _critValue.Text = $"{_critSlider.Value}%";
        }
    }

    private void UpdateIntervalButtons()
    {
        foreach (var btn in _intervalButtons)
        {
            bool selected = btn.Tag is int seconds && seconds == _selectedInterval;
            btn.BackColor = selected
                ? Color.FromArgb(0, 120, 212)
                : (_dark ? Color.FromArgb(58, 58, 58) : Color.White);
            btn.ForeColor = selected
                ? Color.White
                : TextPrimary;
            btn.FlatAppearance.BorderColor = selected
                ? Color.FromArgb(0, 120, 212)
                : CardBorder;
        }
    }

    private void StyleSecondaryButtons()
    {
        foreach (Control c in Controls)
        {
            if (c is Button b && b.Text is "关于" or "取消")
            {
                b.BackColor = _dark ? Color.FromArgb(58, 58, 58) : Color.White;
                b.ForeColor = TextPrimary;
                b.FlatAppearance.BorderColor = CardBorder;
                b.FlatAppearance.MouseOverBackColor =
                    _dark ? Color.FromArgb(72, 72, 72) : Color.FromArgb(240, 243, 247);
            }
        }
    }

    private void OpenAbout()
    {
        using var about = new AboutForm();
        about.ShowDialog(this);
    }

    private void SaveAndClose()
    {
        _config.LowThreshold = _lowSlider.Value;
        _config.CriticalThreshold = _critSlider.Value;
        _config.NotifyEnabled = _notifyToggle.Checked;
        _config.Interval = _selectedInterval;

        // 自启以界面开关为准
        bool desired = _autoStartToggle.Checked;
        AutoStartService.Apply(desired);
        _config.Autostart = desired;

        try { _config.Save(); } catch { }

        _onSaved(_config);
        Close();
    }
}
