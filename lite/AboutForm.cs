using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MouseBatteryTray.Lite;

/// <summary>
/// 关于页面：应用图标、名称、版本、作者、开源协议、仓库地址与检查更新。
///
/// 检查更新走 {RepoUrl}/releases/latest 的 302 跳转解析，
/// 不调用 api.github.com —— 未认证时该接口每小时仅 60 次，很容易被限流。
/// </summary>
internal sealed class AboutForm : Form
{
    private const string RepoOwner = "yixing233";
    private const string RepoName = "logi-tray";
    private const string RepoUrl = $"https://github.com/{RepoOwner}/{RepoName}";
    private const string AuthorName = "yixing233";
    private const string LicenseName = "GPL-3.0";

    private readonly bool _dark;

    private readonly Button _checkButton;
    private readonly Label _statusLabel;
    private bool _checking;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public AboutForm()
    {
        _dark = AppPalette.IsSystemDark();

        Text = "关于 logi-tray-lite";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(340, 372);
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = _dark ? Color.FromArgb(32, 32, 32) : Color.White;
        ForeColor = TextPrimary;

        try
        {
            string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(ico))
            {
                Icon = new Icon(ico);
            }
        }
        catch { }

        // ---- 应用图标 ----
        var logo = new PictureBox
        {
            Size = new Size(72, 72),
            Location = new Point((ClientSize.Width - 72) / 2, 22),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent
        };
        var image = LoadLogo();
        if (image != null)
        {
            logo.Image = image;
        }
        Controls.Add(logo);

        // ---- 名称 ----
        Controls.Add(new Label
        {
            Text = "logi-tray-lite",
            AutoSize = false,
            Size = new Size(ClientSize.Width, 24),
            Location = new Point(0, 102),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
            ForeColor = TextPrimary
        });

        // ---- 版本 ----
        Controls.Add(new Label
        {
            Text = $"{AppVersion()} · 轻量版",
            AutoSize = false,
            Size = new Size(ClientSize.Width, 20),
            Location = new Point(0, 126),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = TextSecondary
        });

        // ---- 信息卡片 ----
        int y = 156;
        var infoCard = new Panel
        {
            Location = new Point(20, y),
            Size = new Size(ClientSize.Width - 40, 116),
            BackColor = CardBack
        };
        infoCard.Paint += (_, e) =>
        {
            using var pen = new Pen(CardBorder);
            e.Graphics.DrawRectangle(pen, 0, 0, infoCard.Width - 1, infoCard.Height - 1);
        };

        AddInfoRow(infoCard, "作者", AuthorName, 10, false);
        AddInfoRow(infoCard, "开源协议", LicenseName, 40, false);

        // 仓库地址做成可点击链接
        infoCard.Controls.Add(MakeText("仓库地址", 12, 70, TextSecondary));
        var repoLink = new LinkLabel
        {
            Text = $"{RepoOwner}/{RepoName}  ↗",
            AutoSize = true,
            Location = new Point(infoCard.Width - 150, 70),
            LinkColor = _dark ? Color.FromArgb(96, 165, 250) : Color.FromArgb(0, 102, 184),
            ActiveLinkColor = _dark ? Color.FromArgb(147, 197, 253) : Color.FromArgb(0, 120, 212),
            LinkBehavior = LinkBehavior.HoverUnderline
        };
        repoLink.Click += (_, _) => OpenUrl(RepoUrl);
        infoCard.Controls.Add(repoLink);
        Controls.Add(infoCard);
        y += 128;

        // ---- 检查更新 ----
        var updateCard = new Panel
        {
            Location = new Point(20, y),
            Size = new Size(ClientSize.Width - 40, 82),
            BackColor = CardBack
        };
        updateCard.Paint += (_, e) =>
        {
            using var pen = new Pen(CardBorder);
            e.Graphics.DrawRectangle(pen, 0, 0, updateCard.Width - 1, updateCard.Height - 1);
        };

        _checkButton = new Button
        {
            Text = "检查更新",
            Size = new Size(96, 30),
            Location = new Point(12, 12),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _checkButton.FlatAppearance.BorderSize = 0;
        _checkButton.BackColor = Color.FromArgb(0, 120, 212);
        _checkButton.ForeColor = Color.White;
        _checkButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(16, 110, 190);
        _checkButton.Click += async (_, _) => await CheckUpdateAsync();
        updateCard.Controls.Add(_checkButton);

        _statusLabel = new Label
        {
            Text = "点击左侧按钮检查是否有新版本",
            AutoSize = false,
            Size = new Size(updateCard.Width - 24, 32),
            Location = new Point(12, 46),
            Font = new Font("Microsoft YaHei UI", 8f),
            ForeColor = TextSecondary
        };
        updateCard.Controls.Add(_statusLabel);
        Controls.Add(updateCard);

        // ---- 关闭 ----
        var closeBtn = new Button
        {
            Text = "关闭",
            Size = new Size(84, 30),
            Location = new Point((ClientSize.Width - 84) / 2, y + 94),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        closeBtn.FlatAppearance.BorderSize = 1;
        closeBtn.FlatAppearance.BorderColor = CardBorder;
        closeBtn.BackColor = _dark ? Color.FromArgb(58, 58, 58) : Color.White;
        closeBtn.ForeColor = TextPrimary;
        closeBtn.Click += (_, _) => Close();
        Controls.Add(closeBtn);
    }

    private Color TextPrimary => _dark ? Color.FromArgb(248, 250, 252) : Color.FromArgb(15, 23, 42);
    private Color TextSecondary => _dark ? Color.FromArgb(158, 172, 190) : Color.FromArgb(51, 65, 85);
    private Color CardBack => _dark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(246, 248, 250);
    private Color CardBorder => _dark ? Color.FromArgb(64, 64, 64) : Color.FromArgb(226, 230, 236);

    private static Image? LoadLogo()
    {
        // 优先用 512×512 的 PNG，退化到 ico 的首帧
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith("app.png", StringComparison.OrdinalIgnoreCase))
                {
                    using var s = asm.GetManifestResourceStream(name);
                    if (s != null)
                    {
                        return Image.FromStream(s);
                    }
                }
            }
        }
        catch { }

        try
        {
            string png = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.png");
            if (File.Exists(png))
            {
                return Image.FromFile(png);
            }
        }
        catch { }

        try
        {
            string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(ico))
            {
                using var icon = new Icon(ico, 64, 64);
                return icon.ToBitmap();
            }
        }
        catch { }

        return null;
    }

    private Label MakeText(string text, int x, int y, Color color) => new()
    {
        Text = text,
        AutoSize = true,
        Location = new Point(x, y),
        ForeColor = color,
        Font = new Font("Microsoft YaHei UI", 9f)
    };

    private void AddInfoRow(Panel parent, string label, string value, int y, bool link)
    {
        parent.Controls.Add(MakeText(label, 12, y, TextSecondary));
        if (link)
        {
            return;
        }

        var val = new Label
        {
            Text = value,
            AutoSize = true,
            Location = new Point(parent.Width - 100, y),
            ForeColor = TextPrimary,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
        };
        parent.Controls.Add(val);
    }

    private static string AppVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }

            var v = asm.GetName().Version;
            if (v != null)
            {
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }
        catch { }

        return "1.0.0";
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private async Task CheckUpdateAsync()
    {
        if (_checking)
        {
            return;
        }

        _checking = true;
        _checkButton.Enabled = false;
        _checkButton.Text = "检查中...";
        _statusLabel.Text = "正在查询最新版本...";

        try
        {
            string? latestTag = await FetchLatestTagAsync();
            if (string.IsNullOrWhiteSpace(latestTag))
            {
                _statusLabel.Text = "未能获取版本信息，请稍后重试";
                return;
            }

            string current = AppVersion();
            if (CompareVersions(latestTag.TrimStart('v', 'V'), current) > 0)
            {
                _statusLabel.Text = $"发现新版本 {latestTag}（当前 {current}）";
                var result = MessageBox.Show(this,
                    $"发现新版本 {latestTag}（当前 {current}）。\n\n是否前往下载页面？",
                    "logi-tray-lite 检查更新",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information);

                if (result == DialogResult.Yes)
                {
                    OpenUrl($"{RepoUrl}/releases/latest");
                }
            }
            else
            {
                _statusLabel.Text = $"已是最新版本 {current}";
            }
        }
        catch
        {
            _statusLabel.Text = "检查更新失败，请确认网络连接";
        }
        finally
        {
            _checkButton.Text = "检查更新";
            _checkButton.Enabled = true;
            _checking = false;
        }
    }

    /// <summary>
    /// 通过 /releases/latest 的 302 跳转解析最新 tag。
    /// 规避 api.github.com 未认证时的 60 次/小时限流（会直接返回 403）。
    /// </summary>
    private static async Task<string?> FetchLatestTagAsync()
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

            using var resp = await client.GetAsync($"{RepoUrl}/releases/latest");
            var location = resp.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(location))
            {
                return null;
            }

            // 形如 https://github.com/owner/repo/releases/tag/v1.0.2
            const string marker = "/releases/tag/";
            int idx = location.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return idx < 0 ? null : location[(idx + marker.Length)..].Trim('/');
        }
        catch
        {
            return null;
        }
    }

    private static int CompareVersions(string a, string b)
    {
        static int[] Parts(string s) => s.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out int v) ? v : 0)
            .ToArray();

        int[] pa = Parts(a);
        int[] pb = Parts(b);
        int len = Math.Max(pa.Length, pb.Length);

        for (int i = 0; i < len; i++)
        {
            int x = i < pa.Length ? pa[i] : 0;
            int y = i < pb.Length ? pb[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }
}
