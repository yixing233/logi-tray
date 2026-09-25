using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MouseBatteryTray;

/// <summary>
/// 检查更新：查询 GitHub 上的最新发布，并判断**本版本自己**是否有新包。
///
/// 为什么不能直接拿 release 的 tag 和本程序版本比较：
/// 三个版本（完整版 / 轻量版 / 多品牌版）刻意使用**各自独立**的版本号，
/// 一次发布里它们的资产版本号往往互不相同（例如 tag 是 v1.4.0，而资产分别是
/// logi-tray-v1.2.1.zip / logi-tray-lite-v1.2.0.zip / multi-tray-v1.2.0.zip）。
/// 若拿 tag 比较，1.4.0 永远大于 1.2.0，于是用户从该 release 下载到的包
/// 点「检查更新」仍会被告知有新版本，点「是」又下载到同一个包 —— 无限提示。
///
/// 因此这里改为：解析最新发布的资产列表，**按本版自己的资产名前缀**
/// （<see cref="AppIdentity.Name"/>，即 logi-tray / logi-tray-lite / multi-tray）
/// 取出属于本版的版本号，再用它和本程序版本比较。
///
/// 刻意不用 REST API（api.github.com）：它对未认证 IP 限 60 次/小时，很容易 403。
/// 全部走网页：先从 releases/latest 的 302 拿到 tag，再取该 tag 的资产列表片段。
/// </summary>
public static class UpdateChecker
{
    public const string RepoOwner = "yixing233";
    public const string RepoName = "logi-tray";
    public const string RepoUrl = "https://github.com/" + RepoOwner + "/" + RepoName;

    private const string TagMarker = "/releases/tag/";

    /// <summary>资产文件名：形如 multi-tray-v1.2.0.zip。</summary>
    private static readonly Regex ZipNamePattern =
        new(@">([A-Za-z0-9][A-Za-z0-9._\-]*\.zip)<", RegexOptions.Compiled);

    /// <summary>一次查询的结果。</summary>
    public sealed class ReleaseLookup
    {
        /// <summary>最新发布的 tag，如 v1.4.0。</summary>
        public string Tag { get; init; } = "";

        /// <summary>本版对应的资产版本号（如 1.2.0）；该发布未包含本版资产时为 null。</summary>
        public string? AssetVersion { get; init; }

        /// <summary>本版对应的资产文件名（如 multi-tray-v1.2.0.zip）。</summary>
        public string? AssetName { get; init; }
    }

    /// <summary>
    /// 查询最新发布，并挑出属于 <paramref name="assetPrefix"/> 那一版的资产版本号。
    /// 网络失败或该发布没有本版资产时返回的对象里 AssetVersion 为 null。
    /// </summary>
    public static async Task<ReleaseLookup?> LookupAsync(
        string assetPrefix,
        string userAgent,
        double timeoutSeconds = 12)
    {
        string? tag;
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

            using var resp = await http.GetAsync($"{RepoUrl}/releases/latest").ConfigureAwait(false);
            int code = (int)resp.StatusCode;

            // 没有重定向到 /releases/tag/... 说明该仓库还没有任何发布
            if (code is not (>= 300 and < 400))
            {
                return null;
            }

            tag = ParseTagFromLocation(resp.Headers.Location?.ToString());
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var lookup = new ReleaseLookup { Tag = tag! };

        // 资产列表：这一步要跟随重定向，因此用默认 handler 的独立客户端
        try
        {
            using var assetsHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            assetsHttp.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

            using var assets = await assetsHttp
                .GetAsync($"{RepoUrl}/releases/expanded_assets/{Uri.EscapeDataString(tag!)}")
                .ConfigureAwait(false);

            if (!assets.IsSuccessStatusCode)
            {
                return lookup;
            }

            string html = await assets.Content.ReadAsStringAsync().ConfigureAwait(false);
            var names = ParseAssetNames(html);
            string? version = ParseAssetVersion(names, assetPrefix);

            return new ReleaseLookup
            {
                Tag = tag!,
                AssetVersion = version,
                AssetName = version == null
                    ? null
                    : names.FirstOrDefault(n => ParseAssetVersion(new[] { n }, assetPrefix) == version)
            };
        }
        catch
        {
            return lookup;
        }
    }

    /// <summary>从资产列表 HTML 片段里取出所有 .zip 资产名。</summary>
    public static List<string> ParseAssetNames(string? html)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(html))
        {
            return result;
        }

        foreach (Match m in ZipNamePattern.Matches(html))
        {
            string name = m.Groups[1].Value;
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>
    /// 在资产名里找属于 <paramref name="assetPrefix"/> 那一版的版本号。
    ///
    /// 必须整名匹配（形如 {prefix}-v1.2.3.zip），否则完整版的 logi-tray 会命中
    /// 轻量版的 logi-tray-lite-v1.2.0.zip，反过来把新版判成旧版。
    /// </summary>
    public static string? ParseAssetVersion(IEnumerable<string>? assetNames, string? assetPrefix)
    {
        if (assetNames == null || string.IsNullOrWhiteSpace(assetPrefix))
        {
            return null;
        }

        var pattern = new Regex(
            "^" + Regex.Escape(assetPrefix!) + @"-v(\d+\.\d+(?:\.\d+)?)\.zip$",
            RegexOptions.IgnoreCase);

        foreach (string? raw in assetNames)
        {
            var m = pattern.Match((raw ?? "").Trim());
            if (m.Success)
            {
                return m.Groups[1].Value;
            }
        }

        return null;
    }

    /// <summary>从 releases/latest 的 302 Location 里取出 tag，如 v1.4.0。</summary>
    public static string? ParseTagFromLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        string text = location!;
        int idx = text.IndexOf(TagMarker, StringComparison.OrdinalIgnoreCase);
        string tail;
        if (idx >= 0)
        {
            tail = text.Substring(idx + TagMarker.Length);
        }
        else
        {
            // 兜底：Location 可能是绝对路径形式，取最后一段
            tail = text;
        }

        int cut = tail.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0)
        {
            tail = tail.Substring(0, cut);
        }

        tail = tail.Trim('/');
        if (idx < 0)
        {
            int slash = tail.LastIndexOf('/');
            if (slash >= 0)
            {
                tail = tail.Substring(slash + 1);
            }
        }

        return tail.Length == 0 ? null : tail;
    }

    /// <summary>比较点分版本号，返回 a 相对 b 的大小（&gt;0 表示 a 更新）。</summary>
    public static int CompareVersions(string? a, string? b)
    {
        static int[] Parts(string? s) => (s ?? "")
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out int n) ? n : 0)
            .ToArray();

        int[] pa = Parts(a);
        int[] pb = Parts(b);
        int len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int va = i < pa.Length ? pa[i] : 0;
            int vb = i < pb.Length ? pb[i] : 0;
            if (va != vb)
            {
                return va - vb;
            }
        }

        return 0;
    }
}
