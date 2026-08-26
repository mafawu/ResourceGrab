using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Services;

namespace ResourceGrab.Core.Sources.VideoSources;

/// <summary>
/// MissAV 在线视频源。
/// 搜索：GET /search/{keyword}?page={n}，解析 div.thumbnail 卡片。
/// 详情：GET 视频页，从 packed JS (Dean Edwards packer) 中提取 m3u8 直链。
/// 反爬：Cloudflare 全站拦截时通过系统 curl 子进程绕过（复用 AiravScraper 的模式）。
/// </summary>
public sealed class MissAvSource : IVideoSource
{
    private static readonly string BaseUrl = "https://missav.ws";

    private static readonly VideoSourceInfo SourceInfo = new()
    {
        Id = "missav",
        DisplayName = "MissAV",
        SupportsSearchSort = false,
        MirrorUrls =
        [
            "missav.ai", "missav.ws", "missav.live", "missav123.com",
        ],
        RequiresCookieWarmup = true,
        RequiresCurlFallback = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private string? _cachedCookieHeader;

    public MissAvSource(HttpClient http, ILogger? logger = null)
    {
        _http = http;
        _logger = logger;
    }

    public VideoSourceInfo Info => SourceInfo;

    // ── 搜索 ──────────────────────────────────────────────────────────

    public async Task<OnlineVideoSearchResult> SearchAsync(string keyword, int page, CancellationToken ct = default)
    {
        var encoded = Uri.EscapeDataString(keyword);
        var url = page <= 1
            ? $"{BaseUrl}/search/{encoded}"
            : $"{BaseUrl}/search/{encoded}?page={page}";

        _logger?.Info($"[MissAV] SearchAsync 开始: keyword=\"{keyword}\" page={page} url={url}");

        string html;
        try
        {
            html = await GetStringAsync(url, ct);
            _logger?.Info($"[MissAV] 搜索请求成功: HTML 长度={html.Length}");
        }
        catch (HttpRequestException)
        {
            _logger?.Warn("[MissAV] 搜索接口不可达，降级为最近更新列表");
            url = page <= 1
                ? $"{BaseUrl}/dm539/cn/new"
                : $"{BaseUrl}/dm539/cn/new?page={page}";
            html = await GetStringAsync(url, ct);
            _logger?.Info($"[MissAV] 降级请求成功: {url}, HTML 长度={html.Length}");
        }

        var doc = new HtmlParser().ParseDocument(html);
        var items = new List<OnlineVideoSummary>();

        var cards = doc.QuerySelectorAll("[class*='thumbnail']").ToList();
        _logger?.Info($"[MissAV] 解析到 thumbnail 卡片数: {cards.Count}");

        foreach (var card in cards)
        {
            var link = card.QuerySelector("a[href]");
            if (link is null) continue;

            var href = link.GetAttribute("href") ?? "";
            if (string.IsNullOrEmpty(href) || href.Contains("/search/") || href.Contains("/actresses") || href.Contains("/genres") || href.Contains("/makers") || href.Contains("/vip") || href.Contains("/saved")) continue;

            // 只保留视频详情页链接（含番号格式 abc-123）
            if (!Regex.IsMatch(href, @"[a-zA-Z]{2,8}-\d{2,6}", RegexOptions.IgnoreCase)) continue;

            _logger?.Info($"[MissAV] 有效视频链接: {href}");

            var img = card.QuerySelector("img");
            var cover = img?.GetAttribute("data-src") ?? img?.GetAttribute("src") ?? "";
            if (cover.StartsWith("data:image")) cover = "";
            var title = img?.GetAttribute("alt") ?? "";
            if (string.IsNullOrEmpty(title))
                title = ExtractNumberFromPath(href);

            var durationSpan = card.QuerySelector("span.absolute.bottom-1.right-1");
            var duration = durationSpan?.TextContent.Trim() ?? "";

            items.Add(new OnlineVideoSummary
            {
                SourceId = Info.Id,
                Id = ExtractVideoId(href),
                Title = title,
                CoverUrl = cover.StartsWith("//") ? "https:" + cover : cover,
                DurationText = duration,
                Tags = [],
            });
        }

        _logger?.Info($"[MissAV] SearchAsync 完成: 返回 {items.Count} 条结果");

        return new OnlineVideoSearchResult { Page = page, Items = items };
    }

    // ── 详情 ──────────────────────────────────────────────────────────

    public async Task<OnlineVideoDetail?> GetDetailAsync(string videoUrl, CancellationToken ct = default)
    {
        var html = await GetStringAsync(videoUrl, ct);

        // og:title / og:image
        var ogTitleMatch = Regex.Match(html, @"og:title""\s+content=""([^""]+)""");
        var title = ogTitleMatch.Success ? WebUtility.HtmlDecode(ogTitleMatch.Groups[1].Value) : "";
        if (string.IsNullOrEmpty(title))
        {
            _logger?.Warn($"[MissAV] 详情页标题解析失败: {videoUrl}");
            return null;
        }

        var ogImageMatch = Regex.Match(html, @"og:image""\s+content=""([^""]+)""");
        var coverUrl = ogImageMatch.Success ? ogImageMatch.Groups[1].Value : "";

        // 从 packed JS 提取 m3u8 URL
        var streamUrl = ExtractM3u8FromPackedJs(html) ?? ExtractAnyM3u8(html);
        if (streamUrl is null)
        {
            _logger?.Warn($"[MissAV] m3u8 提取失败: {videoUrl}");
        }

        // 番号：从 URL 路径提取
        var number = ExtractNumberFromPath(videoUrl);

        return new OnlineVideoDetail
        {
            SourceId = Info.Id,
            VideoUrl = videoUrl,
            Title = CleanTitle(title),
            OriginalTitle = title,
            CoverUrl = coverUrl.StartsWith("//") ? "https:" + coverUrl : coverUrl,
            Number = number,
            StreamUrl = streamUrl,
            Referer = videoUrl,
            MagnetUri = null,  // MissAV 不提供磁力链接
            Actors = [],
            Tags = ParseTags(html),
            Description = "",
        };
    }

    // ── HTTP 层（带 curl 兜底）───────────────────────────────────────

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        _logger?.Info($"[MissAV] HttpClient 请求: {url}");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
            request.Headers.TryAddWithoutValidation("Origin", new Uri(BaseUrl).GetLeftPart(UriPartial.Authority));

            using var response = await _http.SendAsync(request, ct);
            _logger?.Info($"[MissAV] HttpClient 响应: {(int)response.StatusCode} {response.StatusCode}");

            response.EnsureSuccessStatusCode();

            // 缓存 Cookie 头供后续请求使用
            if (_cachedCookieHeader is null && response.Headers.TryGetValues("Set-Cookie", out var cookies))
                _cachedCookieHeader = string.Join("; ", cookies.Select(c => c.Split(';')[0]));

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger?.Warn($"[MissAV] HttpClient 403，切换 curl 兜底: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error("[MissAV] HttpClient 异常", ex);
            throw;
        }
    }

    private static bool IsCloudflareBlock(HttpStatusCode status, byte[] body)
    {
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable
            or (HttpStatusCode)429)
            return true;

        if (body.Length < 100) return false;
        var head = System.Text.Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 3000)).ToLowerInvariant();
        return head.Contains("just a moment")
            || head.Contains("cf-browser-verification")
            || head.Contains("cf_chl_");
    }

    /// <summary>通过系统 curl 子进程请求（TLS 指纹更接近浏览器，可绕过部分 Cloudflare 验证）。</summary>
    internal async Task<string> GetWithCurlAsync(string url, CancellationToken ct)
    {
        try
        {
            _logger?.Info($"[MissAV] curl 子进程请求: {url}");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.SystemDirectory, "curl.exe")
                    : "curl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add("-4");
            psi.ArgumentList.Add("-k");
            psi.ArgumentList.Add("--fail");
            psi.ArgumentList.Add("--silent");
            psi.ArgumentList.Add("--show-error");
            psi.ArgumentList.Add("--compressed");
            psi.ArgumentList.Add("--max-time");
            psi.ArgumentList.Add("30");
            psi.ArgumentList.Add("-A");
            psi.ArgumentList.Add("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36");
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add($"Referer: {BaseUrl}/");
            if (!string.IsNullOrEmpty(_cachedCookieHeader))
            {
                psi.ArgumentList.Add("-H");
                psi.ArgumentList.Add($"Cookie: {_cachedCookieHeader}");
            }
            psi.ArgumentList.Add(url);

            using var process = System.Diagnostics.Process.Start(psi)!;
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"curl 请求失败 ({process.ExitCode}): {errorTask.Result[..Math.Min(errorTask.Result.Length, 200)]}");
            return await outputTask;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new HttpRequestException("无法访问 MissAV（curl 兜底也失败）。", ex);
        }
    }

    // ── HTML 解析辅助 ────────────────────────────────────────────────

    /// <summary>Dean Edwards p,a,c,k,e,d JS 解包器，提取 m3u8 URL。</summary>
    internal static string? ExtractM3u8FromPackedJs(string html)
    {
        var scriptMatches = Regex.Matches(html, @"<script[^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match script in scriptMatches)
        {
            var text = script.Groups[1].Value;
            if (!text.Contains("eval(function") || !text.Contains("m3u8")) continue;

            var unpacked = UnpackDeanEdwards(text);
            if (unpacked is null) continue;

            // 匹配 source= 或任意 m3u8 URL
            var main = Regex.Match(unpacked, @"source\s*=\s*[\\']*(https?://[^'\\;\s]+\.m3u8)");
            if (main.Success) return main.Groups[1].Value;

            var any = Regex.Match(unpacked, @"(https?://[^'\\;\s]+\.m3u8)");
            if (any.Success) return any.Groups[1].Value;
        }
        return null;
    }

    /// <summary>兜底：直接在原始 HTML 里找任何 m3u8 URL。</summary>
    internal static string? ExtractAnyM3u8(string html) =>
        Regex.Match(html, @"https?://[^\s'""<>]+\.m3u8[^\s'""<>]*").Value is { Length: > 0 } v ? v : null;

    /// <summary>Dean Edwards packer 解包算法。</summary>
    internal static string? UnpackDeanEdwards(string scriptText)
    {
        var match = Regex.Match(scriptText,
            @"eval\(function\(p,a,c,k,e,d\)\{.*?\}\('(.*?)',\s*(\d+),\s*(\d+),\s*'([^']*)'\s*\.split\('\|'\)",
            RegexOptions.Singleline);
        if (!match.Success) return null;

        var packed = match.Groups[1].Value;
        if (!int.TryParse(match.Groups[2].Value, out var radix)) return null;
        if (!int.TryParse(match.Groups[3].Value, out var count)) return null;
        var keys = match.Groups[4].Value.Split('|');

        if (radix <= 1 || count < 0 || count > 200_000) return null;

        var lookup = new Dictionary<string, string>(count);
        for (var i = 0; i < count; i++)
        {
            lookup[TryToBaseString(i, radix)] = i < keys.Length && keys[i].Length > 0 ? keys[i] : TryToBaseString(i, radix);
        }

        return Regex.Replace(packed, @"\b(\w+)\b", m =>
            lookup.TryGetValue(m.Groups[1].Value, out var val) ? val : m.Groups[1].Value);
    }

    private static string TryToBaseString(int value, int radix)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value == 0) return "0";
        var sb = new System.Text.StringBuilder();
        while (value > 0)
        {
            sb.Insert(0, digits[value % radix]);
            value /= radix;
        }
        return sb.ToString();
    }

    internal static List<string> ParseTags(string html)
    {
        var tags = new List<string>();
        foreach (Match m in Regex.Matches(html, @"href=""/tags/([^""]+)"""))
        {
            var tag = WebUtility.HtmlDecode(m.Groups[1].Value.Replace('-', ' ').Trim());
            if (tag.Length > 0 && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                tags.Add(tag);
        }
        return tags;
    }

    internal static string CleanTitle(string rawTitle)
    {
        // 去掉 "| MissAV" 后缀和首尾空白
        return Regex.Replace(rawTitle, @"\s*\|\s*MissAV\s*$", "").Trim();
    }

    internal static string ExtractNumberFromPath(string url)
    {
        var match = Regex.Match(url, @"([a-zA-Z]{2,8})-(\d{2,6})", RegexOptions.IgnoreCase);
        if (match.Success)
            return $"{match.Groups[1].Value.ToUpperInvariant()}-{match.Groups[2].Value}";
        // FC2 格式
        var fc2 = Regex.Match(url, @"FC2[-_]?(?:PPV[-_]?)?(\d{6,})", RegexOptions.IgnoreCase);
        if (fc2.Success)
            return $"FC2-{fc2.Groups[1].Value}";
        return "";
    }

    internal static string ExtractVideoId(string href)
    {
        try
        {
            var uri = new Uri(href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : BaseUrl + href);
            return uri.AbsolutePath.TrimEnd('/').Split('/').Last();
        }
        catch
        {
            return href.GetHashCode(System.StringComparison.Ordinal).ToString();
        }
    }
}
