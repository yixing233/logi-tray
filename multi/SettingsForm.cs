using System;
using System.Drawing;
using System.Windows.Forms;
using MouseBatteryTray;

namespace MultiTray;

/// <summary>
/// 设置窗口：阈值、通知、刷新间隔、来源开关、开机自启。
///
/// 与罗技版一样，同一分组的项共用一张卡片作为背景，
/// 而不是每一项都单独套一张卡片。
/// </summary>
internal sealed class SettingsForm : Form
{
    private const int CardWidth = 380;

    private readonly MultiSettings _settings;
    private readonly bool _dark;

    private readonly NumericUpDown _low;
    private readonly NumericUpDown _critical;
    private readonly CheckBox _notify;
    private readonly CheckBox _autostart;
    private readonly CheckBox _srcLogitech;
    private readonly CheckBox _srcMchose;
    private readonly CheckBox _srcAtk;

    private readonly Font _groupFont = new("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
    private readonly Font _itemFont = new("Microsoft YaHei UI", 9f);

    private int _interval;

    public SettingsForm(MultiSettings settings)
    {
        _settings = settings;
        _dark = settings.ThemeMode switch
        {
            "dark" => true,
            "light" => false,
            _ => AppPalette.IsSystemDark(),
        };
        _interval = Math.Max(5, settings.Interval);

        Text = "multi-tray · 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        Font = _itemFont;
        BackColor = _dark ? Color.FromArgb(32, 32, 32) : Color.White;

        _low = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 99,
            Value = Math.Clamp(settings.LowThreshold, 1, 99),
            Width = 64,
            Font = _itemFont,
        };
        _critical = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 99,
            Value = Math.Clamp(settings.CriticalThreshold, 1, 99),
            Width = 64,
            Font = _itemFont,
        };
        _notify = new CheckBox
        {
            Text = "启用低电量提醒",
            Checked = settings.NotifyEnabled,
            AutoSize = true,
            Font = _itemFont,
        };
        _autostart = new CheckBox
        {
            Text = "开机自动启动",
            Checked = AutoStartService.IsEnabled(),
            AutoSize = true,
            Font = _itemFont,
        };
        _srcLogitech = new CheckBox
        {
            Text = "罗技（HID++）",
            Checked = settings.EnabledSources.Contains("logitech"),
            AutoSize = true,
            Font = _itemFont,
        };
        _srcMchose = new CheckBox
        {
            Text = "迈从 MCHOSE",
            Checked = settings.EnabledSources.Contains("mchose"),
            AutoSize = true,
            Font = _itemFont,
        };
        _srcAtk = new CheckBox
        {
            Text = "ATK / VXE / VGN",
            Checked = settings.EnabledSources.Contains("atk"),
            AutoSize = true,
            Font = _itemFont,
        };

        Build();
    }

    private Color TextPrimary => _dark
        ? Color.FromArgb(248, 250, 252)
        : Color.FromArgb(15, 23, 42);

    private Color TextSecondary => _dark
        ? Color.FromArgb(158, 172, 190)
        : Color.FromArgb(71, 85, 105);

    private Color CardBack => _dark
        ? Color.FromArgb(45, 45, 45)
        : Color.FromArgb(247, 248, 250);

    private void Build()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = BackColor,
            Padding = new Padding(12),
        };
        Controls.Add(host);

        int y = 12;

        // ── 分组：电量阈值（一张卡片容纳组内所有项）──
        var thresholdCard = NewCard(178);
        thresholdCard.Location = new Point(12, y);

        thresholdCard.Controls.Add(NewGroupLabel("电量阈值与提醒", 10, 8));
        thresholdCard.Controls.Add(NewItemLabel("低电量提醒阈值", 12, 38));
        _low.Location = new Point(thresholdCard.Width - 96, 36);
        thresholdCard.Controls.Add(_low);
        thresholdCard.Controls.Add(NewUnitLabel("%", thresholdCard.Width - 28, 38));

        thresholdCard.Controls.Add(NewItemLabel("严重低电量阈值", 12, 72));
        _critical.Location = new Point(thresholdCard.Width - 96, 70);
        thresholdCard.Controls.Add(_critical);
        thresholdCard.Controls.Add(NewUnitLabel("%", thresholdCard.Width - 28, 72));

        _notify.Location = new Point(12, 106);
        _notify.ForeColor = TextPrimary;
        _notify.BackColor = CardBack;
        thresholdCard.Controls.Add(_notify);

        var note = new Label
        {
            Text = "电量持续下降时会再次提醒，同一电量不会重复提醒。",
            Font = new Font("Microsoft YaHei UI", 7.5f),
            ForeColor = TextSecondary,
            AutoSize = false,
            Width = thresholdCard.Width - 24,
            Height = 16,
            Location = new Point(12, 132),
            BackColor = CardBack,
        };
        thresholdCard.Controls.Add(note);

        var note2 = new Label
        {
            Text = "充电中不会提醒。",
            Font = new Font("Microsoft YaHei UI", 7.5f),
            ForeColor = TextSecondary,
            AutoSize = false,
            Width = thresholdCard.Width - 24,
            Height = 16,
            Location = new Point(12, 150),
            BackColor = CardBack,
        };
        thresholdCard.Controls.Add(note2);

        host.Controls.Add(thresholdCard);
        y += thresholdCard.Height + 10;

        // ── 分组：后台刷新间隔 ──
        var intervalCard = NewCard(76);
        intervalCard.Location = new Point(12, y);
        intervalCard.Controls.Add(NewGroupLabel("后台刷新间隔", 10, 8));

        int bx = 12;
        foreach (int sec in new[] { 10, 15, 30, 60, 120 })
        {
            var b = new Button
            {
                Text = sec < 60 ? $"{sec} 秒" : $"{sec / 60} 分",
                Width = 62,
                Height = 28,
                Location = new Point(bx, 34),
                FlatStyle = FlatStyle.Flat,
                Font = _itemFont,
                Tag = sec,
            };
            bool active = sec == _interval;
            b.BackColor = active
                ? Color.FromArgb(0, 120, 212)
                : CardBack;
            b.ForeColor = active ? Color.White : TextPrimary;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = _dark
                ? Color.FromArgb(80, 80, 80)
                : Color.FromArgb(214, 219, 226);
            b.Click += (s, _) =>
            {
                if (s is Button btn && btn.Tag is int v)
                {
                    _interval = v;
                    RefreshIntervalButtons();
                }
            };
            intervalCard.Controls.Add(b);
            bx += 70;
        }
        _intervalCard = intervalCard;
        host.Controls.Add(intervalCard);
        y += intervalCard.Height + 10;

        // ── 分组：数据来源（可关闭以排查不兼容的型号）──
        // 高度按内容算：标题(8+约18) + 三个复选框(34/60/86，各 26 间距)
        // + 注释(112+2，高 16) + 底部内边距 10 = 140。
        // 原先写 126，注释底部（130）被裁掉一截 —— 截图核对时才发现。
        var sourceCard = NewCard(140);
        sourceCard.Location = new Point(12, y);
        sourceCard.Controls.Add(NewGroupLabel("启用的设备来源", 10, 8));

        int sy = 34;
        foreach (var cb in new[] { _srcLogitech, _srcMchose, _srcAtk })
        {
            cb.Location = new Point(12, sy);
            cb.ForeColor = TextPrimary;
            cb.BackColor = CardBack;
            sourceCard.Controls.Add(cb);
            sy += 26;
        }
        var srcNote = new Label
        {
            Text = "关闭某来源可避免对其反复探测。",
            Font = new Font("Microsoft YaHei UI", 7.5f),
            ForeColor = TextSecondary,
            AutoSize = false,
            Width = sourceCard.Width - 24,
            Height = 16,
            Location = new Point(12, sy + 2),
            BackColor = CardBack,
        };
        sourceCard.Controls.Add(srcNote);

        host.Controls.Add(sourceCard);
        y += sourceCard.Height + 10;

        // ── 分组：启动 ──
        var startupCard = NewCard(48);
        startupCard.Location = new Point(12, y);
        _autostart.Location = new Point(12, 13);
        _autostart.ForeColor = TextPrimary;
        _autostart.BackColor = CardBack;
        startupCard.Controls.Add(_autostart);
        host.Controls.Add(startupCard);
        y += startupCard.Height + 14;

        // ── 按钮行 ──
        var ok = new Button
        {
            Text = "保存设置",
            Width = 104,
            Height = 32,
            Font = _itemFont,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.Location = new Point(CardWidth - 104, y);
        ok.Click += (_, _) => Save();

        var cancel = new Button
        {
            Text = "取消",
            Width = 88,
            Height = 32,
            Font = _itemFont,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(CardWidth - 104 - 96, y),
        };
        cancel.FlatAppearance.BorderSize = 1;
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        host.Controls.Add(ok);
        host.Controls.Add(cancel);

        y += 44;
        ClientSize = new Size(CardWidth + 24, Math.Min(y, 700));
    }

    private Panel? _intervalCard;

    private void RefreshIntervalButtons()
    {
        if (_intervalCard == null) return;
        foreach (Control c in _intervalCard.Controls)
        {
            if (c is Button b && b.Tag is int v)
            {
                bool active = v == _interval;
                b.BackColor = active ? Color.FromArgb(0, 120, 212) : CardBack;
                b.ForeColor = active ? Color.White : TextPrimary;
            }
        }
    }

    private Panel NewCard(int height) => new()
    {
        Width = CardWidth,
        Height = height,
        BackColor = CardBack,
    };

    private Label NewGroupLabel(string text, int x, int y) => new()
    {
        Text = text,
        Font = _groupFont,
        ForeColor = TextPrimary,
        AutoSize = true,
        Location = new Point(x, y),
        BackColor = CardBack,
    };

    private Label NewItemLabel(string text, int x, int y) => new()
    {
        Text = text,
        Font = _itemFont,
        ForeColor = TextPrimary,
        AutoSize = true,
        Location = new Point(x, y),
        BackColor = CardBack,
    };

    private Label NewUnitLabel(string text, int x, int y) => new()
    {
        Text = text,
        Font = _itemFont,
        ForeColor = TextSecondary,
        AutoSize = true,
        Location = new Point(x, y),
        BackColor = CardBack,
    };

    private void Save()
    {
        int low = (int)_low.Value;
        int critical = (int)_critical.Value;

        // 严重阈值必须低于普通阈值，否则提醒层级会失去意义
        if (critical >= low)
        {
            MessageBox.Show(this,
                $"严重低电量阈值（{critical}%）必须小于低电量阈值（{low}%）。",
                "阈值设置有误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.LowThreshold = low;
        _settings.CriticalThreshold = critical;
        _settings.NotifyEnabled = _notify.Checked;
        _settings.Interval = _interval;
        _settings.EnabledSources.Clear();
        if (_srcLogitech.Checked) _settings.EnabledSources.Add("logitech");
        if (_srcMchose.Checked) _settings.EnabledSources.Add("mchose");
        if (_srcAtk.Checked) _settings.EnabledSources.Add("atk");
        if (_settings.EnabledSources.Count == 0)
        {
            _settings.EnabledSources.Add("logitech");
        }

        try
        {
            if (_autostart.Checked) AutoStartService.Enable();
            else AutoStartService.Disable();
            _settings.Autostart = _autostart.Checked;
        }
        catch
        {
            // 自启失败不应阻止其它设置保存
        }

        // 必须在这里落盘。
        //
        // 早先只有 TrayContext.ShowSettings() 在对话框返回 OK 后才调用 Save()，
        // 因此「从托盘打开」时碰巧能存，「直接打开设置窗口」时全部改动都会丢
        // （实测：点击保存后配置文件根本不存在）。持久化属于本窗体的职责，
        // 不能依赖调用方。
        _settings.Save();

        DialogResult = DialogResult.OK;
        Close();
    }
}
