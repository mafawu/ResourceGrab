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
    private static readonly string BaseUrl = "https://missav.live";

    private static readonly VideoSourceInfo SourceInfo = new()
    {
        Id = "missav",
        DisplayName = "MissAV",
        SupportsSearchSort = false,
        // 2026-08-30 实测：missav.ws/missav.ai 有 Cloudflare 挑战（HttpClient 403 → curl 兜底，
        // 单请求 4~5 秒），missav.live/missav123.com 可直取。主域用可直取的 missav.live，
        // 被挑战的旧主域 missav.ws 降为末位兜底镜像，避免每个请求都白付一次降级链耗时。
        MirrorUrls =
        [
            "missav123.com", "missav.ws", "missav.ai",
        ],
        RequiresCookieWarmup = true,
        RequiresCurlFallback = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private readonly string? _proxy;
    private string? _cachedCookieHeader;

    /// <summary>
    /// 最近一次成功请求的源站（如 https://missav.live）与是否经 curl 兜底。
    /// Cloudflare 拦截下每个请求都要走 主域403→curl→镜像403→curl 的完整降级链（约 4~5 秒），
    /// 记住上次成功的组合直接首选，可把单请求耗时从 4~5 秒降到 1 秒左右；失败时清零自愈。
    /// </summary>
    private string? _lastGoodOrigin;
    private bool _lastGoodViaCurl;

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
    /// 视频链接判定放宽为「末段含连字符 + 多位数字」（覆盖普通番号、FC2、纯数字番号等）；
    /// 卡片内演员链接并入 Tags。
    /// 去重：同一视频常以 主链接（/dmXX/ssis-960）+ 变体（ssis-960-uncensored-leak、
    /// ssis-960-chinese-subtitle）同时命中，末段不同但封面标题相同——按番号归一去重，
    /// 主链接优先于变体（变体和主链接在页面里交错出现，不能只保留先出现的）。
    /// </summary>
    internal List<OnlineVideoSummary> ParseSearchCards(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var items = new List<OnlineVideoSummary>();
        // 番号 → items 下标；无番号的（纯数字路径等）退回末段做键
        var cardIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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

            var number = ExtractNumberFromPath(href);
            var dedupeKey = number.Length > 0 ? number : segment;
            var isCanonical = number.Length > 0 && segment.Equals(number, StringComparison.OrdinalIgnoreCase);

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

            var summary = new OnlineVideoSummary
            {
                SourceId = Info.Id,
                Id = segment,
                Title = title,
                CoverUrl = cover.StartsWith("//") ? "https:" + cover : cover,
                DurationText = duration,
                Number = number,
                KindLabel = kindLabel,
                Tags = tags,
            };

            if (cardIndex.TryGetValue(dedupeKey, out var existingIndex))
            {
                // 同番号变体：主链接替换先到的变体卡片，其余丢弃
                if (isCanonical) items[existingIndex] = summary;
                continue;
            }
            cardIndex[dedupeKey] = items.Count;
            items.Add(summary);
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

        // 信息行（番號/發行日期/標題/女優/類型/發行商/導演/標籤…）：
        // 各站行名措辞不一，按同义词归一化后覆盖正则兜底值
        var info = ParseInfoRows(html);
        string Field(string key) => info.TryGetValue(key, out var v) && v.Count > 0 ? v[0] : "";

        if (Field("title") is { Length: > 0 } infoTitle) title = infoTitle;
        if (Field("number") is { Length: > 0 } infoNumber) number = infoNumber;
        if (Field("release") is { Length: > 0 } infoRelease) releaseDate = infoRelease;

        var genres = info.TryGetValue("genres", out var infoGenres) && infoGenres.Count > 0
            ? infoGenres : ParseGenres(html);
        var actors = info.TryGetValue("actress", out var infoActress) && infoActress.Count > 0
            ? infoActress : ParseActors(html);

        var magnets = ParseMagnets(html);
        if (magnets.Count == 0 && magnet.Length > 0)
            magnets = [new OnlineMagnetLink { Url = magnet }];

        var previewImages = ParsePreviewImages(html);

        // MissAV 页面内容为繁体中文：离线词典繁转简 + 演员名译中文（词典失败原样保留）。
        // OriginalTitle 保留原题。
        var cleanTitle = OfflineLexicon.ToSimplified(CleanTitle(title));
        var tags = OfflineLexicon.NormalizeTags(ParseTags(html));
        genres = OfflineLexicon.NormalizeTags(genres);
        actors = actors.Select(OfflineLexicon.TranslateActor)
            .Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        description = OfflineLexicon.ToSimplified(description);

        return new OnlineVideoDetail
        {
            SourceId = Info.Id,
            VideoUrl = videoUrl,
            Title = cleanTitle,
            OriginalTitle = title,
            CoverUrl = coverUrl.StartsWith("//") ? "https:" + coverUrl : coverUrl,
            Number = number,
            DurationText = durationText,
            ReleaseDateText = releaseDate,
            StreamUrl = streamUrl,
            Referer = videoUrl,
            MagnetUri = magnets.Count > 0 ? magnets[0].Url : null,
            Magnets = magnets,
            Actors = actors,
            Tags = tags,
            Genres = genres,
            Maker = Field("maker"),
            Label = Field("label"),
            Director = Field("director"),
            RatingText = Field("rating"),
            Description = description,
            PreviewImages = previewImages,
        };
    }

    /// <summary>
    /// 解析详情页"預覽圖"区块（简繁/英文措辞都收）下的剧照图片。
    /// 容错策略：锚定标题文本后收集 img，直到下一个区块级标签（h1-h4/hr）或窗口结束，
    /// 避免把后续"同系列/相關影片"的缩略图误收进来。懒加载图优先取 data-src。
    /// </summary>
    internal static List<string> ParsePreviewImages(string html)
    {
        var header = Regex.Match(html, @"預覽圖|预览图|[Pp]review [Ii]mages?");
        if (!header.Success) return [];

        var window = html[header.Index..];
        // 截断到下一个区块级标签（跳过标题文本本身），防止吃进后续推荐区的图
        var skip = Math.Min(20, window.Length);
        var nextSection = Regex.Match(window[skip..], @"<h[1-4][\s>]|<hr[\s>]");
        if (nextSection.Success) window = window[..(skip + nextSection.Index)];

        var images = new List<string>();
        foreach (Match m in Regex.Matches(window, @"<img\b[^>]*>"))
        {
            var tag = m.Value;
            // 懒加载图优先取 data-src
            var src = Regex.Match(tag, @"data-src=""([^""]+)""") is { Success: true } lazy
                ? lazy
                : Regex.Match(tag, @"src=""([^""]+)""");
            if (!src.Success) continue;
            var url = src.Groups[1].Value;
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            if (url.StartsWith("//")) url = "https:" + url;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            if (!images.Contains(url)) images.Add(url);
            if (images.Count >= 30) break;
        }
        return images;
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
        // 上次成功的镜像+传输方式直接首选；失败则清零并按原顺序轮换
        if (_lastGoodOrigin is { } preferred)
        {
            var viaCurl = _lastGoodViaCurl;
            var index = candidates.FindIndex(c =>
                Uri.TryCreate(c, UriKind.Absolute, out var u)
                && u.GetLeftPart(UriPartial.Authority) == preferred);
            if (index > 0)
            {
                var move = candidates[index];
                candidates.RemoveAt(index);
                candidates.Insert(0, move);
            }
            if (index < 0) { _lastGoodOrigin = null; }
            else try
            {
                return await GetStringAsync(candidates[0], ct, viaCurl);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _lastGoodOrigin = null;
                lastError = ex;
                _logger?.Warn($"[MissAV] 上次成功的镜像也失败了，恢复镜像轮换: {candidates[0]} ({ex.Message})");
            }
        }

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

    private async Task<string> GetStringAsync(string url, CancellationToken ct, bool preferCurl = false)
    {
        if (preferCurl)
        {
            // 上次该镜像就是靠 curl 兜底成功的：跳过注定 403 的 HttpClient 尝试
            var htmlViaCurl = await GetWithCurlAsync(url, ct);
            if (BlockDetector.IsBlocked(null, htmlViaCurl))
                throw new HttpRequestException("MissAV curl 兜底仍返回拦截页", null, HttpStatusCode.Forbidden);
            _lastGoodOrigin = new Uri(url).GetLeftPart(UriPartial.Authority);
            _lastGoodViaCurl = true;
            return htmlViaCurl;
        }

        _logger?.Info($"[MissAV] HttpClient 请求: {url}");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
            request.Headers.TryAddWithoutValidation("Origin", new Uri(BaseUrl).GetLeftPart(UriPartial.Authority));

            // 单次尝试限时：挂死的域名快速失败，把时间让给镜像轮换而不是无限等
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.SendAsync(request, attemptCts.Token);
            _logger?.Info($"[MissAV] HttpClient 响应: {(int)response.StatusCode} {response.StatusCode}");

            var html = await response.Content.ReadAsStringAsync(attemptCts.Token);

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

            _lastGoodOrigin = new Uri(url).GetLeftPart(UriPartial.Authority);
            _lastGoodViaCurl = false;
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
            _lastGoodOrigin = new Uri(url).GetLeftPart(UriPartial.Authority);
            _lastGoodViaCurl = true;
            return html;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 单次尝试超时（外层未取消）：转成请求异常，交给镜像轮换处理
            _logger?.Warn($"[MissAV] 请求超时（15s）: {url}");
            throw new HttpRequestException($"MissAV 请求超时: {url}");
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

    internal static List<string> ParseTags(string html) => ParseLinkSlugs(html, "tags");

    internal static List<string> ParseGenres(string html) => ParseLinkSlugs(html, "genres");

    private static List<string> ParseLinkSlugs(string html, string kind)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(html, $@"href=""/(?:[a-z]{{2}}/)?{kind}/([^""/#?]+)"""))
        {
            var tag = WebUtility.HtmlDecode(m.Groups[1].Value.Replace('-', ' ').Trim());
            if (tag.Length > 0 && !list.Contains(tag, StringComparer.OrdinalIgnoreCase))
                list.Add(tag);
        }
        return list;
    }

    // ── 详情页信息行归一化 ──────────────────────────────────────────

    /// <summary>统一字段 → 各站点行名同义词（繁/简/英文都收）。</summary>
    private static readonly Dictionary<string, string[]> InfoRowAliases = new()
    {
        ["number"] = ["番號", "番号", "品番", "识别码", "識別碼"],
        ["release"] = ["發行日期", "发行日期", "発売日", "发布日期", "上市日"],
        ["title"] = ["標題", "标题", "片名", "作品名"],
        ["actress"] = ["女優", "女优", "出演", "演员", "演員", "cast", "actor"],
        ["genres"] = ["類型", "类型", "分类", "分類", "ジャンル", "genre"],
        ["maker"] = ["發行商", "发行商", "制作商", "製作商", "studio", "maker"],
        ["label"] = ["標籤", "标签", "廠商", "厂商", "厂牌", "レーベル", "label"],
        ["director"] = ["導演", "导演", "director"],
        ["rating"] = ["評分", "评分", "rating"],
    };

    /// <summary>
    /// 解析详情页 "标签: 值" 信息行（&lt;div class="text-secondary"&gt;&lt;span&gt;類型:&lt;/span&gt; …&lt;/div&gt;），
    /// 按同义词映射为统一字段；同字段取首个出现。解析不出时由调用方的正则兜底。
    /// </summary>
    internal static Dictionary<string, List<string>> ParseInfoRows(string html)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (Match m in Regex.Matches(html,
                     @"<div[^>]*class=""text-secondary""[^>]*>\s*<span>([^<]+?)</span>([\s\S]*?)</div>"))
        {
            var rawLabel = WebUtility.HtmlDecode(m.Groups[1].Value).Trim().TrimEnd(':', '：');
            var key = MapInfoRow(rawLabel);
            if (key is null || result.ContainsKey(key)) continue;

            var values = WebUtility.HtmlDecode(Regex.Replace(m.Groups[2].Value, @"<[^>]+>", " "))
                .Split(',', '，')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            if (values.Count > 0) result[key] = values;
        }
        return result;
    }

    private static string? MapInfoRow(string rawLabel)
    {
        foreach (var (key, aliases) in InfoRowAliases)
            if (aliases.Contains(rawLabel, StringComparer.OrdinalIgnoreCase))
                return key;
        return null;
    }

    /// <summary>
    /// 解析磁力表格：每行 &lt;tr&gt; 含一条 magnet 链接（名称锚点）+ 体积 + 日期。
    /// 整页范围内扫描含 magnet 的行并按 btih 去重；无表格时返回空。
    /// </summary>
    internal static List<OnlineMagnetLink> ParseMagnets(string html)
    {
        var links = new List<OnlineMagnetLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match row in Regex.Matches(html, @"<tr>([\s\S]*?)</tr>"))
        {
            var body = row.Groups[1].Value;
            var url = Regex.Match(body, @"href=""(magnet:\?xt=urn:btih:[^""]+)""")
                .Groups[1].Value.Replace("&amp;", "&");
            if (url.Length == 0 || !seen.Add(url)) continue;

            var name = WebUtility.HtmlDecode(
                Regex.Match(body, @"href=""magnet:[^""]*""[^>]*>([^<]*)</a>").Groups[1].Value.Trim());
            var size = Regex.Match(body, @"(\d+(?:\.\d+)?\s*(?:TB|GB|MB|KB))", RegexOptions.IgnoreCase).Groups[1].Value;
            var date = Regex.Match(body, @"(\d{4}-\d{2}-\d{2})").Groups[1].Value;
            links.Add(new OnlineMagnetLink { Name = name, Size = size, Date = date, Url = url });
        }
        return links;
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
