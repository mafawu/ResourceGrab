using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Sources.VideoSources;

/// <summary>
/// MissAV 在线视频源。
/// 搜索：GET /search/{keyword}?page={n}，解析 div.thumbnail 卡片；#标签 语法走 /tags/{tag} 标签页。
/// 详情：GET 视频页，从 packed JS (Dean Edwards packer) 中提取 m3u8 直链，
///       并解析 JSON-LD / og 元信息（时长、发行日期、简介）与演员、标签链接。
/// 反爬：HttpClient 403 时通过系统 curl 子进程绕过；请求失败按镜像域名列表轮换。
/// </summary>
public sealed class MissAvSource : IVideoSource
{
    private static readonly string BaseUrl = "https://missav.ws";

    private static readonly VideoSourceInfo SourceInfo = new()
    {
        Id = "missav",
        DisplayName = "MissAV",
        SupportsSearchSort = false,
        // 2026-08-30 实测：missav.ws/missav.ai 有 Cloudflare 挑战，missav.live/missav123.com 可直取；
        // 主域 BaseUrl 单独放在候选首位，镜像列表里不再重复收录。
        MirrorUrls =
        [
            "missav.live", "missav123.com", "missav.ai",
        ],
        RequiresCookieWarmup = true,
        RequiresCurlFallback = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private readonly string? _proxy;
    private string? _cachedCookieHeader;

    public MissAvSource(HttpClient http, ILogger? logger = null, string? proxy = null)
    {
        _http = http;
        _logger = logger;
        _proxy = proxy;
    }

    public VideoSourceInfo Info => SourceInfo;

    /// <summary>搜索摘要的 Id 是详情页路径的最后一段，拼回站点 URL。</summary>
    public string? GetDetailUrl(string videoId)
        => videoId.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? videoId : $"{BaseUrl}/{videoId}";

    // ── 搜索 ──────────────────────────────────────────────────────────

    public async Task<OnlineVideoSearchResult> SearchAsync(string keyword, int page, CancellationToken ct = default)
    {
        keyword = keyword.Trim();
        // #标签 语法 → 站内标签页（失败自动退回普通搜索）
        var isTagSearch = keyword.StartsWith('#');
        var term = isTagSearch ? keyword.TrimStart('#').Trim() : keyword;
        if (term.Length == 0) return new OnlineVideoSearchResult { Page = page };

        var encoded = Uri.EscapeDataString(term);
        var path = isTagSearch ? $"/tags/{encoded}" : $"/search/{encoded}";
        var pageSuffix = page <= 1 ? "" : $"?page={page}";

        _logger?.Info($"[MissAV] SearchAsync 开始: keyword=\"{keyword}\" tag={isTagSearch} page={page}");

        string html;
        try
        {
            html = await GetStringWithMirrorsAsync(path + pageSuffix, ct);
            _logger?.Info($"[MissAV] 搜索请求成功: HTML 长度={html.Length}");
        }
        catch (HttpRequestException ex)
        {
            if (isTagSearch)
            {
                // 标签页不存在/被拦截时退回普通关键词搜索
                _logger?.Warn($"[MissAV] 标签页不可达，退回普通搜索: {ex.Message}");
                html = await GetStringWithMirrorsAsync($"/search/{encoded}{pageSuffix}", ct);
            }
            else
            {
                _logger?.Warn("[MissAV] 搜索接口不可达，降级为最近更新列表");
                html = await GetStringWithMirrorsAsync(
                    page <= 1 ? "/dm539/cn/new" : $"/dm539/cn/new?page={page}", ct);
            }
            _logger?.Info($"[MissAV] 兜底请求成功: HTML 长度={html.Length}");
        }

        var items = ParseSearchCards(html);
        _logger?.Info($"[MissAV] SearchAsync 完成: 返回 {items.Count} 条结果");

        return new OnlineVideoSearchResult { Page = page, Items = items };
    }

    /// <summary>
    /// 解析搜索/标签页的 thumbnail 卡片。
    /// 视频链接判定放宽为「末段含连字符 + 多位数字」（覆盖普通番号、FC2、纯数字番号等），
    /// 避免番号正则过严导致命中率低；按 Id 去重；卡片内演员链接并入 Tags。
    /// </summary>
    internal List<OnlineVideoSummary> ParseSearchCards(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var items = new List<OnlineVideoSummary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var cards = doc.QuerySelectorAll("[class*='thumbnail']");
        _logger?.Info($"[MissAV] 解析到 thumbnail 卡片数: {cards.Count()}");

        foreach (var card in cards)
        {
            var link = card.QuerySelector("a[href]");
            if (link is null) continue;

            var href = (link.GetAttribute("href") ?? "").Split('?')[0].Split('#')[0];
            if (string.IsNullOrEmpty(href) || ContainsExcludedPath(href)) continue;

            // 视频页判定：末段路径含连字符且含至少两位数字（如 ssis-001、fc2-ppv-3175496、010115-001）
            var segment = ExtractVideoId(href);
            if (!Regex.IsMatch(segment, @"-\d{2,}")) continue;

            if (!seen.Add(segment)) continue;

            var img = card.QuerySelector("img");
            var cover = img?.GetAttribute("data-src") ?? img?.GetAttribute("src") ?? "";
            if (cover.StartsWith("data:image")) cover = "";
            var title = img?.GetAttribute("alt") ?? "";
            if (string.IsNullOrEmpty(title))
                title = ExtractNumberFromPath(href);

            var durationSpan = card.QuerySelector("span.absolute.bottom-1.right-1");
            var duration = durationSpan?.TextContent.Trim() ?? "";

            // 左下角的内容类型角标（如 "無碼影片"、"中文字幕"）
            var kindLabel = card.QuerySelector("span.absolute.bottom-1.left-1")?.TextContent.Trim() ?? "";

            // 卡片内的演员链接（/actresses/xxx）作为标签展示，并可用于点击搜索
            var tags = new List<string>();
            foreach (var actressLink in card.QuerySelectorAll("a[href*='/actresses/']"))
            {
                var slug = ExtractVideoId(actressLink.GetAttribute("href") ?? "");
                if (slug.Length == 0) continue;
                var name = WebUtility.HtmlDecode(slug.Replace('-', ' ')).Trim();
                if (name.Length > 0 && !tags.Contains(name, StringComparer.OrdinalIgnoreCase))
                    tags.Add(name);
            }

            // 标题链接文本形如 "SSIS-960 标题…… - 演員名"：末段 " - " 后是日文演员名，
            // 补进标签（部分卡片没有 /actresses/ 链接，只能从这里拿）
            var titleLinkText = WebUtility.HtmlDecode(
                card.QuerySelector("div.my-2 a")?.TextContent.Trim() ?? "");
            var dash = titleLinkText.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0)
            {
                var actress = titleLinkText[(dash + 3)..].Trim();
                if (actress.Length > 0 && actress.Length <= 40
                    && !tags.Contains(actress, StringComparer.OrdinalIgnoreCase))
                    tags.Insert(0, actress);
            }

            items.Add(new OnlineVideoSummary
            {
                SourceId = Info.Id,
                Id = segment,
                Title = title,
                CoverUrl = cover.StartsWith("//") ? "https:" + cover : cover,
                DurationText = duration,
                Number = ExtractNumberFromPath(href),
                KindLabel = kindLabel,
                Tags = tags,
            });
        }

        return items;
    }

    // 注意：视频详情 URL 现在带 /dmXX/ 路径前缀（如 /dm118/ssis-971），
    // 不能按 "/dm数字" 排除；是否为视频卡片统一由末段 "xxx-数字" 正则把关，
    // 推广链接（/dm1004/maan、/dm15/furuke）与列表页（/dm539/cn/new）末段不含
    // 番号形式，天然被该正则挡掉。
    private static bool ContainsExcludedPath(string href) =>
        href.Contains("/search/") || href.Contains("/tags/") || href.Contains("/actresses")
        || href.Contains("/genres") || href.Contains("/makers") || href.Contains("/series")
        || href.Contains("/download") || href.Contains("/list/") || href.Contains("/vip")
        || href.Contains("/saved");

    // ── 详情 ──────────────────────────────────────────────────────────

    public async Task<OnlineVideoDetail?> GetDetailAsync(string videoUrl, CancellationToken ct = default)
    {
        string html = await GetStringWithMirrorsAsync(videoUrl, ct);

        // og:title / og:image / og:description
        var ogTitleMatch = Regex.Match(html, @"og:title""\s+content=""([^""]+)""");
        var title = ogTitleMatch.Success ? WebUtility.HtmlDecode(ogTitleMatch.Groups[1].Value) : "";
        if (string.IsNullOrEmpty(title))
        {
            _logger?.Warn($"[MissAV] 详情页标题解析失败: {videoUrl}");
            return null;
        }

        var ogImageMatch = Regex.Match(html, @"og:image""\s+content=""([^""]+)""");
        var coverUrl = ogImageMatch.Success ? ogImageMatch.Groups[1].Value : "";

        var ogDescMatch = Regex.Match(html, @"og:description""\s+content=""([^""]+)""");
        var description = ogDescMatch.Success ? WebUtility.HtmlDecode(ogDescMatch.Groups[1].Value) : "";

        // 从 packed JS 提取 m3u8 URL
        var streamUrl = ExtractM3u8FromPackedJs(html) ?? ExtractAnyM3u8(html);
        if (streamUrl is null)
        {
            _logger?.Warn($"[MissAV] m3u8 提取失败: {videoUrl}");
        }

        // 番号：从 URL 路径提取
        var number = ExtractNumberFromPath(videoUrl);

        // JSON-LD（VideoObject）：时长、发行/上传日期
        var durationText = ParseJsonLdDuration(html) ?? "";
        var releaseDate = ParseJsonLdDate(html) ?? "";

        // 磁力链接：部分页面内嵌（如 /download 弹层脚本），有就带上
        var magnet = Regex.Match(html, @"(magnet:\?xt=urn:btih:[a-zA-Z0-9&=%.+-]+)").Groups[1].Value;

        return new OnlineVideoDetail
        {
            SourceId = Info.Id,
            VideoUrl = videoUrl,
            Title = CleanTitle(title),
            OriginalTitle = title,
            CoverUrl = coverUrl.StartsWith("//") ? "https:" + coverUrl : coverUrl,
            Number = number,
            DurationText = durationText,
            ReleaseDateText = releaseDate,
            StreamUrl = streamUrl,
            Referer = videoUrl,
            MagnetUri = magnet.Length > 0 ? magnet : null,
            Actors = ParseActors(html),
            Tags = ParseTags(html),
            Description = description,
        };
    }

    // ── HTTP 层（带镜像轮换 + curl 兜底）─────────────────────────────

    /// <summary>请求一个站内路径/URL：先走主域，失败时按镜像域名列表轮换重试。</summary>
    private async Task<string> GetStringWithMirrorsAsync(string pathOrUrl, CancellationToken ct)
    {
        var isFullPath = pathOrUrl.StartsWith("/", StringComparison.Ordinal);
        var candidates = new List<string>();
        if (isFullPath)
        {
            candidates.Add(BaseUrl + pathOrUrl);
            candidates.AddRange(SourceInfo.MirrorUrls.Select(m => $"https://{m}{pathOrUrl}"));
        }
        else
        {
            candidates.Add(pathOrUrl);
            if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri))
            {
                var pathAndQuery = uri.PathAndQuery;
                candidates.AddRange(SourceInfo.MirrorUrls.Select(m => $"https://{m}{pathAndQuery}"));
            }
        }

        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            try
            {
                return await GetStringAsync(candidate, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                _logger?.Warn($"[MissAV] 请求失败，尝试下一个镜像: {candidate} ({ex.Message})");
            }
        }
        throw lastError is HttpRequestException hre
            ? hre
            : new HttpRequestException("所有 MissAV 域名均请求失败。", lastError);
    }

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

            var html = await response.Content.ReadAsStringAsync(ct);

            // 200 但 body 是挑战页的情况（Cloudflare 软拦截）也要当作被拦，交给 curl/镜像降级链
            if (BlockDetector.IsBlocked((int)response.StatusCode, html))
            {
                _logger?.Warn($"[MissAV] 疑似反爬拦截页 (HTTP {(int)response.StatusCode}): {url}");
                throw new HttpRequestException("MissAV 返回反爬拦截页", null, HttpStatusCode.Forbidden);
            }
            if (!response.IsSuccessStatusCode)
            {
                // 非拦截类 4xx/5xx：如实抛出，交给镜像轮换/上层翻译，避免把 404 空页当搜索结果解析出 0 条
                throw new HttpRequestException($"MissAV HTTP {(int)response.StatusCode}: {url}", null, response.StatusCode);
            }

            // 缓存 Cookie 头供后续请求使用
            if (_cachedCookieHeader is null && response.Headers.TryGetValues("Set-Cookie", out var cookies))
                _cachedCookieHeader = string.Join("; ", cookies.Select(c => c.Split(';')[0]));

            return html;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            // Cloudflare 拦截：curl 子进程 TLS 指纹更接近浏览器，先兜底一次再抛出（外层换镜像重试）
            _logger?.Warn($"[MissAV] HttpClient 403，切换 curl 兜底: {ex.Message}");
            var html = await GetWithCurlAsync(url, ct);
            // curl 拿到 200 也可能是挑战页：必须继续走拦截检测并抛出，否则镜像轮换被提前短路
            if (BlockDetector.IsBlocked(null, html))
                throw new HttpRequestException("MissAV curl 兜底仍返回拦截页", null, HttpStatusCode.Forbidden);
            return html;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Error("[MissAV] HttpClient 异常", ex);
            throw;
        }
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
            if (!string.IsNullOrWhiteSpace(_proxy))
            {
                // 兜底必须与 HttpClient 同出口：直连被墙的网络下不带代理的兜底必然失败
                psi.ArgumentList.Add("-x");
                psi.ArgumentList.Add(_proxy);
            }
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

    /// <summary>解析页面内演员链接（/actresses/{slug}），解码为可读名字。</summary>
    internal static List<string> ParseActors(string html)
    {
        var actors = new List<string>();
        foreach (Match m in Regex.Matches(html, @"href=""/(?:[a-z]{2}/)?actresses/([^""/#?]+)"""))
        {
            var name = WebUtility.HtmlDecode(m.Groups[1].Value.Replace('-', ' ').Trim());
            if (name.Length > 0 && !actors.Contains(name, StringComparer.OrdinalIgnoreCase))
                actors.Add(name);
        }
        return actors;
    }

    /// <summary>解析 JSON-LD VideoObject 的 ISO8601 时长（PT#H#M#S）为 "H:mm:ss"/"mm:ss"。</summary>
    internal static string? ParseJsonLdDuration(string html)
    {
        var m = Regex.Match(html, @"""duration""\s*:\s*""(PT(?:\d+H)?(?:\d+M)?(?:\d+(?:\.\d+)?S)?)""");
        if (!m.Success) return null;
        var v = m.Groups[1].Value;
        var h = Regex.Match(v, @"(\d+)H");
        var min = Regex.Match(v, @"(\d+)M");
        var s = Regex.Match(v, @"(\d+(?:\.\d+)?)S");
        var hours = h.Success ? int.Parse(h.Groups[1].Value) : 0;
        var mins = min.Success ? int.Parse(min.Groups[1].Value) : 0;
        var secs = s.Success ? (int)double.Parse(s.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return hours > 0 ? $"{hours}:{mins:D2}:{secs:D2}" : $"{mins}:{secs:D2}";
    }

    /// <summary>解析 JSON-LD 的 uploadDate/publishDate 为 yyyy-MM-dd。</summary>
    internal static string? ParseJsonLdDate(string html)
    {
        var m = Regex.Match(html, @"""(?:uploadDate|publishDate|datePublished)""\s*:\s*""(\d{4}-\d{2}-\d{2})");
        return m.Success ? m.Groups[1].Value : null;
    }

    internal static List<string> ParseTags(string html)
    {
        var tags = new List<string>();
        // 标签页与类型页两种链接形态都收（/tags/xxx、/genres/xxx，可带语言前缀）
        foreach (Match m in Regex.Matches(html, @"href=""/(?:[a-z]{2}/)?(?:tags|genres)/([^""/#?]+)"""))
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
