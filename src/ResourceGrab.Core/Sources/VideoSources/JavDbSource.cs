using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;

namespace ResourceGrab.Core.Sources.VideoSources;

/// <summary>
/// JavDB 在线视频源。
/// 搜索：GET {base}/search?q={keyword}&amp;f=all[&amp;page={n}]，解析 .movie-list .item 卡片。
/// 详情：GET 视频详情页，解析中文标题（strong.current-title）/日文原题（span.origin-title）、
///       演员、标签、评分、剧照、简介、同系列番号。
/// 账号：可选 Cookie（浏览器登录 javdb.com 后复制粘贴到设置）。带上 Cookie 获得登录态
///       （搜索限流放宽、VIP 磁力链接）；未配置时匿名访问——JavDB 公开搜索页匿名可用
///       （参考 javdb_tool 的做法，其本身不登录）。
/// 反爬：HttpClient 403 / 命中 Cloudflare 挑战页时通过系统 curl 子进程绕过。
/// </summary>
public sealed class JavDbSource : IVideoSource
{
    private const string DefaultBaseUrl = "https://javdb.com";
    private const string BrowserUserAgent = VideoConstants.UserAgent;

    private readonly HttpClient _http;
    private readonly ConfigService _config;
    private readonly ILogger? _logger;

    public JavDbSource(HttpClient http, ConfigService config, ILogger? logger = null)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    private VideoScrapeSettings Settings => _config.Current.VideoScraping ?? new VideoScrapeSettings();

    /// <summary>实时读配置，改设置无需重启。</summary>
    private string BaseUrl
    {
        get
        {
            var url = Settings.JavDbBaseUrl;
            return string.IsNullOrWhiteSpace(url) ? DefaultBaseUrl : url.TrimEnd('/');
        }
    }

    private string Cookie => Settings.JavDbCookie ?? "";

    public VideoSourceInfo Info { get; } = new()
    {
        Id = "javdb",
        DisplayName = "JavDB",
        SupportsSearchSort = false,
        RequiresCookieWarmup = true,
        RequiresCurlFallback = true,
    };

    public string? GetDetailUrl(string videoId)
        => videoId.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? videoId : $"{BaseUrl}{videoId}";

    // ── 搜索 ──────────────────────────────────────────────────────────

    public async Task<OnlineVideoSearchResult> SearchAsync(string keyword, int page, CancellationToken ct = default)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0) return new OnlineVideoSearchResult { Page = page };

        var encoded = Uri.EscapeDataString(keyword);
        // f=all 命中全部类型（视频/演员/导演/系列）；结果页通过 &page={n} 翻页
        var url = $"{BaseUrl}/search?q={encoded}&f=all";
        if (page > 1) url += $"&page={page}";

        _logger?.Info($"[JavDB] SearchAsync 开始: keyword=\"{keyword}\" page={page}");
        var html = await GetStringAsync(url, ct);
        var items = ParseSearchCards(html);
        _logger?.Info($"[JavDB] SearchAsync 完成: 返回 {items.Count} 条结果");
        return new OnlineVideoSearchResult { Page = page, Items = items };
    }

    /// <summary>
    /// 解析搜索页卡片。JavDB 搜索结果结构：
    /// div.movie-list &gt; div.item &gt; a.box（整卡可点），卡片内 .video-title（标题）、
    /// .uid（番号）、img（封面，懒加载取 data-src）、.score .value（评分）。
    /// 兼容历史版 div.item a.box 结构（无 .uid / .score 时仅取标题+链接）。
    /// </summary>
    internal List<OnlineVideoSummary> ParseSearchCards(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var cards = doc.QuerySelectorAll(".movie-list .item");
        if (cards.Length == 0) cards = doc.QuerySelectorAll("div.item");

        var items = new List<OnlineVideoSummary>();
        foreach (var card in cards)
        {
            var link = card.QuerySelector("a.box, a[href]");
            if (link is null) continue;
            var href = (link.GetAttribute("href") ?? "").Split('#')[0].Split('?')[0];
            if (string.IsNullOrEmpty(href) || href == "/") continue;

            var title = WebUtility.HtmlDecode(card.QuerySelector(".video-title")?.TextContent.Trim() ?? "");
            var uid = WebUtility.HtmlDecode(card.QuerySelector(".uid")?.TextContent.Trim() ?? "");

            var img = card.QuerySelector("img");
            var cover = img?.GetAttribute("data-src") ?? img?.GetAttribute("src") ?? "";
            if (cover.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) cover = "";
            if (cover.StartsWith("//")) cover = "https:" + cover;
            if (cover.Length > 0 && !cover.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                cover = BaseUrl + (cover.StartsWith("/") ? "" : "/") + cover;

            var number = uid;
            var ratingText = "";
            var scoreNode = card.QuerySelector(".score .value");
            if (scoreNode is not null)
            {
                var scoreText = scoreNode.TextContent.Trim();
                // JavDB 5 分制 → 转 10 分制展示（与刮削器一致）
                if (double.TryParse(scoreText, out var score5) && score5 > 0)
                    ratingText = (score5 * 2).ToString("0.#");
                else
                    ratingText = scoreText;
            }

            items.Add(new OnlineVideoSummary
            {
                SourceId = Info.Id,
                Id = href,
                Title = title.Length > 0 ? title : (number.Length > 0 ? number : href),
                CoverUrl = cover,
                Number = number,
                RatingText = ratingText,
            });
        }

        // 同番号去重（一视频可能同时命中多条）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<OnlineVideoSummary>(items.Count);
        foreach (var item in items)
        {
            var key = item.Number.Length > 0 ? item.Number : item.Id;
            if (seen.Add(key)) deduped.Add(item);
        }
        return deduped;
    }

    // ── 详情 ──────────────────────────────────────────────────────────

    public async Task<OnlineVideoDetail?> GetDetailAsync(string videoUrl, CancellationToken ct = default)
    {
        var url = videoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? videoUrl
            : BaseUrl + (videoUrl.StartsWith("/") ? "" : "/") + videoUrl;

        var html = await GetStringAsync(url, ct);
        var root = new HtmlParser().ParseDocument(html);

        string Text(string selector)
        {
            var node = root.QuerySelector(selector);
            return WebUtility.HtmlDecode(node?.TextContent.Trim() ?? "");
        }

        // 标题结构：<h2><strong>番号</strong><strong class="current-title">中文标题</strong>
        //          <span class="origin-title" style="display:none">日文原题</span></h2>
        var cnTitle = Text("strong.current-title");
        var origTitle = Text("span.origin-title");
        var title = cnTitle.Length > 0 ? cnTitle : origTitle;
        if (title.Length == 0)
        {
            _logger?.Warn($"[JavDB] 详情页标题解析失败: {url}");
            return null;
        }

        string Field(string label)
        {
            foreach (var p in root.QuerySelectorAll("nav.panel > div.panel-block"))
            {
                var strong = p.QuerySelector("strong");
                if (strong is null || !strong.TextContent.Replace(":", "").Replace("：", "").Trim().Equals(label, StringComparison.Ordinal)) continue;
                var valueNode = p.QuerySelector("span.value");
                var raw = valueNode?.InnerHtml ?? "";
                return WebUtility.HtmlDecode(Regex.Replace(raw, "<[^>]+>", " ")).Trim();
            }
            return "";
        }

        // 番号取 h2 内第一个 strong（"SSIS-405"），多余文本截断
        var number = Text("h2 strong").Split(' ')[0];

        var actors = new List<string>();
        foreach (var a in root.QuerySelectorAll("nav.panel span.value a[href*=\"/actors/\"]"))
        {
            var name = WebUtility.HtmlDecode(a.TextContent.Trim());
            if (name.Length > 0 && !actors.Contains(name, StringComparer.OrdinalIgnoreCase)) actors.Add(name);
        }

        var tags = new List<string>();
        foreach (var a in root.QuerySelectorAll("nav.panel span.value a[href*=\"/tags?\"]"))
        {
            var tag = WebUtility.HtmlDecode(a.TextContent.Trim());
            if (tag.Length > 0 && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
        }

        var ratingText = "";
        var scoreBlock = Field("評分");
        var scoreMatch = Regex.Match(scoreBlock, @"(\d+(?:\.\d+)?)分");
        if (scoreMatch.Success && double.TryParse(scoreMatch.Groups[1].Value, out var score5))
            ratingText = (score5 * 2).ToString("0.#"); // 5 分制转 10 分制

        var coverUrl = ResolveUrl(
            root.QuerySelector("img.video-cover")?.GetAttribute("src")
            ?? root.QuerySelector("img[data-src]")?.GetAttribute("data-src")
            ?? "");

        // 简介：JavDB 详情页「簡介」panel 的第一段文本
        var description = "";
        var introBlock = root.QuerySelector("div.intro, div.panel-block:has(strong:contains(簡介))");
        if (introBlock is null)
        {
            foreach (var p in root.QuerySelectorAll("nav.panel > div.panel-block"))
            {
                var strong = p.QuerySelector("strong");
                if (strong is null || strong.TextContent.Trim() != "簡介") continue;
                introBlock = p;
                break;
            }
        }
        if (introBlock is not null)
            description = WebUtility.HtmlDecode(Regex.Replace(introBlock.TextContent ?? "", @"\s+", " ").Trim());

        // 剧照：c0.jdbstatic.com/samples 链接
        var previewImages = new List<string>();
        foreach (var a in root.QuerySelectorAll("a.tile-item[href*=\"/samples/\"]"))
        {
            var url2 = a.GetAttribute("href") ?? "";
            if (url2.Length == 0) continue;
            if (url2.StartsWith("//")) url2 = "https:" + url2;
            if (!previewImages.Contains(url2)) previewImages.Add(url2);
        }

        // 同系列推荐：div.tile-images.tile-small 里的番号
        var related = new List<string>();
        foreach (var tile in root.QuerySelectorAll("div.tile-images.tile-small a.tile-item .video-number"))
        {
            var relNum = WebUtility.HtmlDecode(tile.TextContent.Trim());
            if (relNum.Length > 0 && !related.Contains(relNum, StringComparer.OrdinalIgnoreCase))
                related.Add(relNum);
        }

        var magnets = new List<OnlineMagnetLink>();
        var magnetUri = "";
        var hasCnSub = root.QuerySelector(".tag-can-play.cnsub, .tag-cnsub") is not null
            || (root.TextContent ?? "").Contains("含中字", StringComparison.OrdinalIgnoreCase);

        // 磁力页（{detail}/magnets）需要登录态：仅配置了 Cookie 时尝试，VIP 权益
        if (Cookie.Length > 0)
        {
            try
            {
                var magnetsHtml = await GetStringAsync($"{url.TrimEnd('/')}/magnets", ct);
                magnets = ParseMagnets(magnetsHtml);
                if (magnets.Count > 0) magnetUri = magnets[0].Url;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn($"[JavDB] 磁力页获取失败（可能非 VIP）: {ex.Message}");
            }
        }

        return new OnlineVideoDetail
        {
            SourceId = Info.Id,
            VideoUrl = url,
            Title = title,
            OriginalTitle = origTitle.Length > 0 ? origTitle : cnTitle,
            CoverUrl = coverUrl,
            Number = number,
            DurationText = Field("時長"),
            ReleaseDateText = Field("日期"),
            Director = Field("導演"),
            Maker = Field("片商"),
            RatingText = ratingText,
            Actors = actors,
            Tags = tags,
            Description = description,
            PreviewImages = previewImages,
            RelatedVideos = related,
            Magnets = magnets,
            MagnetUri = magnetUri,
            // JavDB 无 HLS 直链，播放交给本地库/外部；StreamUrl 置空由 UI 降级
            StreamUrl = null,
            Referer = BaseUrl + "/",
        };
    }

    /// <summary>
    /// 解析磁力页表格。JavDB /magnets 页结构：table &gt; tbody &gt; tr，
    /// 每行：名称（含磁力链接的 a[href^="magnet:"] 或复制按钮 data-clipboard-text）、
    /// 体积、日期。
    /// </summary>
    internal List<OnlineMagnetLink> ParseMagnets(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var result = new List<OnlineMagnetLink>();
        foreach (var row in doc.QuerySelectorAll("table tbody tr"))
        {
            var name = WebUtility.HtmlDecode(row.QuerySelector("td.name, td:nth-child(1)")?.TextContent.Trim() ?? "");
            var magnetLink = row.QuerySelector("a[href^=\"magnet:\"]");
            var copyBtn = row.QuerySelector("[data-clipboard-text]");
            var url = magnetLink?.GetAttribute("href") ?? copyBtn?.GetAttribute("data-clipboard-text") ?? "";
            if (url.Length == 0) continue;

            var cells = row.QuerySelectorAll("td").ToList();
            var size = cells.Count > 1 ? WebUtility.HtmlDecode(cells[1].TextContent.Trim()) : "";
            var date = cells.Count > 2 ? WebUtility.HtmlDecode(cells[2].TextContent.Trim()) : "";

            result.Add(new OnlineMagnetLink { Name = name, Size = size, Date = date, Url = url });
        }
        return result;
    }

    // ── HTTP 层（Cookie 透传 + curl 兜底）────────────────────────────

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        _logger?.Info($"[JavDB] HttpClient 请求: {url}");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            if (Cookie.Length > 0)
                request.Headers.TryAddWithoutValidation("Cookie", Cookie);

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.SendAsync(request, attemptCts.Token);
            var html = await response.Content.ReadAsStringAsync(attemptCts.Token);

            // 200 但 body 是 Cloudflare 挑战页：按被拦处理，交给 curl 兜底
            if (IsChallengePage(html))
            {
                _logger?.Warn($"[JavDB] 疑似 Cloudflare 挑战页 (HTTP {(int)response.StatusCode}): {url}");
                throw new HttpRequestException("JavDB 返回 Cloudflare 挑战页", null, HttpStatusCode.Forbidden);
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"JavDB HTTP {(int)response.StatusCode}: {url}", null, response.StatusCode);
            }
            return html;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            // Cloudflare 拦截：curl 子进程 TLS 指纹更接近浏览器
            _logger?.Warn($"[JavDB] HttpClient 403，切换 curl 兜底: {ex.Message}");
            var viaCurl = await GetWithCurlAsync(url, ct);
            if (IsChallengePage(viaCurl))
                throw new HttpRequestException("JavDB curl 兜底仍返回挑战页", null, HttpStatusCode.Forbidden);
            return viaCurl;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger?.Warn($"[JavDB] 请求超时（15s）: {url}");
            throw new HttpRequestException($"JavDB 请求超时: {url}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Error("[JavDB] HttpClient 异常", ex);
            throw;
        }
    }

    /// <summary>通过系统 curl 子进程请求（绕过部分 Cloudflare 验证）。</summary>
    private async Task<string> GetWithCurlAsync(string url, CancellationToken ct)
    {
        try
        {
            _logger?.Info($"[JavDB] curl 子进程请求: {url}");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.SystemDirectory, "curl.exe")
                    : "curl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add("-k");
            psi.ArgumentList.Add("--fail");
            psi.ArgumentList.Add("--silent");
            psi.ArgumentList.Add("--show-error");
            psi.ArgumentList.Add("--compressed");
            psi.ArgumentList.Add("--max-time");
            psi.ArgumentList.Add("30");
            psi.ArgumentList.Add("-A");
            psi.ArgumentList.Add(BrowserUserAgent);
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add($"Referer: {BaseUrl}/");
            if (Cookie.Length > 0)
            {
                psi.ArgumentList.Add("-H");
                psi.ArgumentList.Add($"Cookie: {Cookie}");
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
            throw new HttpRequestException("无法访问 JavDB（curl 兜底也失败）。", ex);
        }
    }

    /// <summary>Cloudflare 挑战页 / 登录重定向判定。</summary>
    private static bool IsChallengePage(string html)
    {
        if (string.IsNullOrEmpty(html)) return true;
        return html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
            || html.Contains("challenge", StringComparison.OrdinalIgnoreCase)
            || (html.Contains("cf_chl", StringComparison.OrdinalIgnoreCase) && html.Length < 200_000);
    }

    private string ResolveUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        if (url.StartsWith("//")) return "https:" + url;
        return BaseUrl + (url.StartsWith("/") ? "" : "/") + url;
    }
}
