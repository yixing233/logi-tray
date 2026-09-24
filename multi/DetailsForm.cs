using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MouseBatteryTray;

namespace MultiTray;

/// <summary>
/// 设备列表窗口：每台设备一行，显示电量、状态与来源。
///
/// 刻意不画历史曲线、不做续航预测 —— 本工具只回答两个问题：
/// 「现在各设备还剩多少电」和「有没有谁快没电了」。
/// </summary>
internal sealed class DetailsForm : Form
{
    private const int CardWidth = 340;
    private readonly bool _dark;
    private readonly MultiSettings _settings;

    /// <summary>当前展示的读数。刷新时就地替换，不重建窗口。</summary>
    private IReadOnlyList<DeviceReading> _readings;

    private readonly Font _titleFont = new("Microsoft YaHei UI", 11f, FontStyle.Bold);
    private readonly Font _nameFont = new("Microsoft YaHei UI", 9.5f);
    private readonly Font _pctFont = new("Microsoft YaHei UI", 16f, FontStyle.Bold);
    private readonly Font _metaFont = new("Microsoft YaHei UI", 8f);

    /// <summary>承载全部内容的滚动面板；刷新时清空并重建其子控件。</summary>
    private readonly Panel _panel;

    public DetailsForm(IReadOnlyList<DeviceReading> readings, bool dark,
                       MultiSettings settings)
    {
        _readings = readings;
        _dark = dark;
        _settings = settings;

        Text = "设备电量";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        Width = CardWidth + 16;
        BackColor = dark ? Color.FromArgb(32, 32, 32) : Color.White;
        Font = _nameFont;

        _panel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(8),
            BackColor = BackColor,
        };
        Controls.Add(_panel);

        Build();
    }

    private Color TextPrimary => _dark
        ? Color.FromArgb(248, 250, 252)
        : Color.FromArgb(15, 23, 42);

    private Color TextSecondary => _dark
        ? Color.FromArgb(158, 172, 190)
        : Color.FromArgb(51, 65, 85);

    private Color CardBack => _dark
        ? Color.FromArgb(45, 45, 45)
        : Color.FromArgb(247, 248, 250);

    private void Build()
    {
        var panel = _panel;
        panel.SuspendLayout();

        // 清空旧内容（刷新时会重新进入这里）
        foreach (Control c in panel.Controls.Cast<Control>().ToList())
        {
            panel.Controls.Remove(c);
            c.Dispose();
        }

        int y = 8;

        var header = new Label
        {
            Text = "设备电量",
            Font = _titleFont,
            ForeColor = TextPrimary,
            AutoSize = true,
            Location = new Point(8, y),
        };
        panel.Controls.Add(header);
        y += header.PreferredHeight + 8;

        if (_readings.Count == 0)
        {
            var empty = new Label
            {
                Text = "未检测到受支持的设备。\n\n" +
                       "支持罗技（HID++）、迈从 MCHOSE、ATK/VXE/VGN。",
                Font = _nameFont,
                ForeColor = TextSecondary,
                AutoSize = true,
                MaximumSize = new Size(CardWidth - 24, 0),
                Location = new Point(8, y),
            };
            panel.Controls.Add(empty);
            y += empty.PreferredHeight + 12;
        }
        else
        {
            foreach (var r in _readings)
            {
                var card = BuildCard(r);
                card.Location = new Point(8, y);
                card.Width = CardWidth;
                panel.Controls.Add(card);
                y += card.Height + 8;
            }
        }

        // 底部：当前阈值提示，让用户知道为什么会（或不会）收到提醒
        var hint = new Label
        {
            Text = _settings.NotifyEnabled
                ? $"低电量提醒：≤{_settings.LowThreshold}% 提醒，≤{_settings.CriticalThreshold}% 严重提醒"
                : "低电量提醒已关闭",
            Font = _metaFont,
            ForeColor = TextSecondary,
            AutoSize = true,
            MaximumSize = new Size(CardWidth - 16, 0),
            Location = new Point(8, y + 4),
        };
        panel.Controls.Add(hint);
        y += hint.PreferredHeight + 16;

        var refresh = new Button
        {
            Text = "立即刷新",
            Font = _nameFont,
            Width = 96,
            Height = 30,
            Location = new Point(8, y),
            FlatStyle = FlatStyle.Flat,
        };
        refresh.FlatAppearance.BorderSize = 1;
        refresh.Click += (_, _) =>
        {
            // 就地刷新：重新读取并重建本窗口内容。
            //
            // 早先的写法是 Close() 之后 new 一个窗口再 ShowDialog()，
            // 这是错的：Close() 会结束外层的模态消息循环，进程随即退出
            // （实测点击「立即刷新」后进程直接消失）。
            _readings = DeviceReader.ReadAll(_settings);
            Build();
        };
        panel.Controls.Add(refresh);

        var close = new Button
        {
            Text = "关闭",
            Font = _nameFont,
            Width = 96,
            Height = 30,
            Location = new Point(CardWidth - 88, y),
            FlatStyle = FlatStyle.Flat,
        };
        close.FlatAppearance.BorderSize = 1;
        close.Click += (_, _) => Close();
        panel.Controls.Add(close);

        y += 44;
        ClientSize = new Size(CardWidth + 16, Math.Min(y, 620));

        panel.ResumeLayout(true);
        panel.PerformLayout();
    }

    private Panel BuildCard(DeviceReading r)
    {
        int height = 62;
        var card = new Panel
        {
            Height = height,
            BackColor = CardBack,
        };

        // 左侧：电量数字（读不到时显示 --）
        var rgb = AppPalette.GetBatteryColor(r.Percent, r.IsCharging);
        var pctColor = r.Percent >= 0
            ? Color.FromArgb(rgb.R, rgb.G, rgb.B)
            : TextSecondary;

        var pct = new Label
        {
            Text = r.Percent >= 0 ? r.Percent.ToString() : "--",
            Font = _pctFont,
            ForeColor = pctColor,
            AutoSize = false,
            Width = 76,
            Height = 40,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(8, 10),
            BackColor = Color.Transparent,
        };
        card.Controls.Add(pct);

        var unit = new Label
        {
            Text = r.Percent >= 0 ? "%" : "",
            Font = _metaFont,
            ForeColor = pctColor,
            AutoSize = true,
            Location = new Point(62, 30),
            BackColor = Color.Transparent,
        };
        card.Controls.Add(unit);

        // 右侧：名称 + 状态 + 来源
        var name = new Label
        {
            Text = r.Name,
            Font = _nameFont,
            ForeColor = TextPrimary,
            AutoSize = false,
            Width = CardWidth - 104,
            Height = 20,
            Location = new Point(88, 10),
            BackColor = Color.Transparent,
        };
        card.Controls.Add(name);

        string state = r.IsOnline
            ? (r.IsCharging ? r.StatusText : $"{r.StatusText} · {Protocols.LevelText(r.Percent)}")
            : "已休眠 / 离线";
        var status = new Label
        {
            Text = state,
            Font = _metaFont,
            ForeColor = TextSecondary,
            AutoSize = false,
            Width = CardWidth - 104,
            Height = 18,
            Location = new Point(88, 30),
            BackColor = Color.Transparent,
        };
        card.Controls.Add(status);

        var source = new Label
        {
            Text = $"来源：{r.Source}",
            Font = _metaFont,
            ForeColor = TextSecondary,
            AutoSize = false,
            Width = CardWidth - 104,
            Height = 16,
            Location = new Point(88, 46),
            BackColor = Color.Transparent,
        };
        card.Controls.Add(source);

        return card;
    }
}
