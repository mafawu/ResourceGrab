using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Logging;
using AngleSharp.Html.Parser;

namespace ResourceGrab.Core.Services;

public sealed class VideoScrapeProgress(int total)
{
    public int Total { get; set; } = total;
    public int Completed { get; set; }
    public int SuccessCount { get; set; }
    public int NoMatchCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> Logs { get; } = [];
    public event Action<VideoScrapeProgress>? Changed;
    public void Publish() => Changed?.Invoke(this);
    public void Log(string message)
    {
        Logs.Insert(0, $"{DateTime.Now:T} {message}");
        if (Logs.Count > 20) Logs.RemoveAt(Logs.Count - 1);
        Publish();
    }
}

public sealed class VideoScrapeHttpClient : IDisposable
{
    private readonly HttpClient _client;

    internal HttpClient Client => _client;
    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly int _concurrency;
    private readonly int _intervalMs;
    private DateTime _nextRequestAt = DateTime.MinValue;

    public VideoScrapeHttpClient(VideoScrapeSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = string.IsNullOrWhiteSpace(settings.Proxy) ? null : new WebProxy(settings.Proxy),
            AllowAutoRedirect = true,
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 3, 60)) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36");
        _client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _client.DefaultRequestHeaders.AcceptLanguage.TryParseAdd("zh-CN,zh;q=0.9,en-US;q=0.8,ja;q=0.7");
        _client.DefaultRequestHeaders.Referrer = new Uri("https://www.javbus.com/");
        if (!string.IsNullOrWhiteSpace(settings.JavBusCookie))
            _client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", settings.JavBusCookie);
        _concurrency = Math.Clamp(settings.Concurrency, 1, 8);
        _gate = new SemaphoreSlim(_concurrency, _concurrency);
        _intervalMs = Math.Clamp(settings.RequestIntervalMs, 0, 10_000);
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await DelayAsync(ct);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await _client.GetStringAsync(url, ct);
                }
                catch when (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<byte[]> GetByteArrayAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await DelayAsync(ct);
            for (var attempt = 0; ; attempt++)
            {
                try { return await _client.GetByteArrayAsync(url, ct); }
                catch when (attempt < 2) { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct); }
            }
        }
        finally { _gate.Release(); }

    }

    private async Task DelayAsync(CancellationToken ct)
    {
        var waitAt = DateTime.UtcNow;
        var remaining = _nextRequestAt - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
        _nextRequestAt = waitAt.AddMilliseconds(_intervalMs);
    }

    public void Dispose() => _client.Dispose();
}

public sealed class JavBusScraper : IVideoScraper
{
    private readonly VideoScrapeHttpClient _http;
    private readonly string _baseUrl;
    public string Id => "javbus";
    public string DisplayName { get; set; } = "JavBus";

    public JavBusScraper(VideoScrapeHttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public bool Supports(VideoNumberKind kind) => kind != VideoNumberKind.Unknown;

    public async Task<VideoScrapeMetadata?> SearchAsync(string number, CancellationToken ct)
    {
        var searchHtml = await _http.GetStringAsync($"{_baseUrl}/search/{Uri.EscapeDataString(number)}&type=1", ct);
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(searchHtml);
        var first = doc.QuerySelector("a.movie-box");
        if (first is null) return null;
        var detailUrl = first.GetAttribute("href") ?? "";
        if (string.IsNullOrEmpty(detailUrl)) return null;
        var html = await _http.GetStringAsync(detailUrl, ct);
        var root = parser.ParseDocument(html);
        string Text(string selector)
        {
            var node = root.QuerySelector(selector);
            return WebUtility.HtmlDecode(node?.TextContent.Trim() ?? "");
        }
        var metadata = new VideoScrapeMetadata
        {
            Source = Id,
            Title = Text("h3"),
            SourceUrls =
            {
                [Id] = detailUrl,
            },
            CoverUrl = ResolveUrl(_baseUrl,
                root.QuerySelector("a.bigImage")?.GetAttribute("href")
                ?? root.QuerySelector("img.movie-cover")?.GetAttribute("src")
                ?? root.QuerySelector(".photo-frame img")?.GetAttribute("src")
                ?? ""),
        };
        foreach (var node in root.QuerySelectorAll("a.avatar-box span"))
            metadata.Actors.Add(WebUtility.HtmlDecode(node.TextContent.Trim()));
        foreach (var genre in root.QuerySelectorAll("span.genre a"))
            metadata.Tags.Add(WebUtility.HtmlDecode(genre.TextContent.Trim()));

        // 相关推荐：从详情页底部「同類影片」区块提取番号
        foreach (var card in root.QuerySelectorAll("#related-waterfall a.movie-box, #related-waterfall .item"))
        {
            var href = card.GetAttribute("href") ?? "";
            var num = System.Text.RegularExpressions.Regex.Match(href, @"([A-Z]{2,8})-?(\d{2,6})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (num.Success)
            {
                var related = $"{num.Groups[1].Value.ToUpperInvariant()}-{num.Groups[2].Value}";
                if (related != number && !metadata.RelatedNumbers.Contains(related))
                    metadata.RelatedNumbers.Add(related);
            }
        }
        // 详情页演员名也可能出现在 ul 下方的 p 里（无 avatar-box 时兜底）
        if (metadata.Actors.Count == 0)
        {
            var starShow = root.QuerySelector("p.star-show");
            var actorUl = starShow?.NextElementSibling;
            if (actorUl?.TagName == "UL")
                foreach (var li in actorUl.QuerySelectorAll("li a"))
                    metadata.Actors.Add(WebUtility.HtmlDecode(li.TextContent.Trim()));
        }

        foreach (var p in root.QuerySelectorAll("div.col-md-3.info > p, div.row.movie > p, p"))
        {
            var raw = WebUtility.HtmlDecode(p.TextContent.Replace("&nbsp;", " ").Trim());
            var label = raw.Split(':').First().Trim();
            var value = raw.Contains(':') ? raw[(raw.IndexOf(':') + 1)..].Trim() : "";
            switch (label)
            {
                case "識別碼": break;
                case "發行日期" when DateTime.TryParse(value, out var date): metadata.ReleaseDate = date; break;
                case "長度" when double.TryParse(value, out var minutes): metadata.RuntimeMinutes = (int)minutes; break;
                case "導演": metadata.Director = value; break;
                case "製作商": metadata.Studio = value; break;
                case "發行商": metadata.Publisher = value; break;
                case "系列":
                    var seriesLink = p.QuerySelector("a");
                    metadata.Series = WebUtility.HtmlDecode(seriesLink?.TextContent.Trim() ?? "");
                    if (!string.IsNullOrEmpty(seriesLink?.GetAttribute("href")))
                    {
                        metadata.SeriesUrl = ResolveUrl(_baseUrl, seriesLink.GetAttribute("href"));
                        metadata.SourceUrls[$"{Id}-series"] = metadata.SeriesUrl;
                    }
                    break;
            }
        }
        return string.IsNullOrEmpty(metadata.Title) ? null : metadata;
    }

    internal static string ResolveUrl(string baseUrl, string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        if (url.StartsWith("//")) return "https:" + url;
        return baseUrl + (url.StartsWith("/") ? "" : "/") + url;
    }
}

/// <summary>AirAv 中文页刮削源：补充中文标题与剧情简介。</summary>
public sealed class AiravScraper : IVideoScraper
{
    private readonly VideoScrapeHttpClient _http;
    private readonly string _baseUrl;
    private readonly string _origin;
    private bool _preferCurl;
    public string Id => "airav";
    public string DisplayName { get; set; } = "AirAv";

    public AiravScraper(VideoScrapeHttpClient http, string baseUrl = "https://airav.io/cn")
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _origin = new Uri(_baseUrl).GetLeftPart(UriPartial.Authority);
    }

    public bool Supports(VideoNumberKind kind) => kind != VideoNumberKind.Unknown;

    public async Task<VideoScrapeMetadata?> SearchAsync(string number, CancellationToken ct)
    {
        var searchHtml = await GetStringAsync($"{_baseUrl}/search_result?kw={Uri.EscapeDataString(number)}", ct);
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(searchHtml);
        var detailPath = SelectSearchResult(doc, number);
        if (string.IsNullOrEmpty(detailPath)) return null;

        var detailUrl = detailPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? detailPath
            : JavBusScraper.ResolveUrl(detailPath.StartsWith("/") ? _origin : _baseUrl, detailPath);
        var html = await GetStringAsync(detailUrl, ct);
        var metadata = ParseDetail(html, number);
        if (metadata is not null)
        {
            metadata.SourceUrls[Id] = detailUrl;
        }
        return metadata;
    }

    /// <summary>AirAv 的防护按 TLS 指纹拦截 .NET HttpClient；系统 curl 可通过，因此仅对该源兜底。</summary>
    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        if (_preferCurl) return await GetWithCurlAsync(url, ct);
        try
        {
            return await _http.GetStringAsync(url, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            _preferCurl = true;
            return await GetWithCurlAsync(url, ct);
        }
        catch (Exception)
        {
            return await GetWithCurlAsync(url, ct);
        }
    }

    private static async Task<string> GetWithCurlAsync(string url, CancellationToken ct)
    {
        try
        {
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
            psi.ArgumentList.Add("--fail");
            psi.ArgumentList.Add("--silent");
            psi.ArgumentList.Add("--show-error");
            psi.ArgumentList.Add("--compressed");
            psi.ArgumentList.Add("--retry");
            psi.ArgumentList.Add("3");
            psi.ArgumentList.Add("--retry-all-errors");
            psi.ArgumentList.Add("-A");
            psi.ArgumentList.Add("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36");
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add("Accept-Language: zh-CN,zh;q=0.9,en-US;q=0.8,ja;q=0.7");
            psi.ArgumentList.Add(url);

            using var process = System.Diagnostics.Process.Start(psi)!;
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"AirAv curl request failed ({process.ExitCode}): {errorTask.Result.Trim()}");
            }
            return await outputTask;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new HttpRequestException("无法通过系统 curl 访问 AirAv。", ex);
        }
    }

    internal static string? SelectSearchResult(AngleSharp.Dom.IDocument doc, string number)
    {
        foreach (var item in doc.QuerySelectorAll("div.col.oneVideo"))
        {
            var title = item.QuerySelector("h5")?.TextContent.Trim() ?? "";
            if (title.Contains(number, StringComparison.OrdinalIgnoreCase) && !title.Contains("克破"))
            {
                return item.QuerySelector("a[href]")?.GetAttribute("href");
            }
        }
        return null;
    }

    internal static VideoScrapeMetadata? ParseDetail(string html, string fallbackNumber)
    {
        var root = new HtmlParser().ParseDocument(html);
        var rawTitle = root.QuerySelector(".video-title.my-3 h1")?.TextContent.Trim() ?? "";
        var title = Regex.Replace(rawTitle, $"^{Regex.Escape(fallbackNumber)}\\s*", "", RegexOptions.IgnoreCase).Trim();
        if (title.Length == 0) return null;

        string Text(string selector)
        {
            var node = root.QuerySelector(selector);
            return System.Net.WebUtility.HtmlDecode(node?.TextContent.Trim() ?? "");
        }

        var metadata = new VideoScrapeMetadata
        {
            Source = "airav",
            Title = title,
            Description = root.QuerySelectorAll(".video-info > p")
                .Select(node => System.Net.WebUtility.HtmlDecode(node.TextContent.Trim()))
                .FirstOrDefault(value => value.Length > 0) ?? "",
        };

        foreach (var li in root.QuerySelectorAll(".video-info ul li"))
        {
            var label = li.TextContent.Split(':', '：').First().Trim();
            if (label is "女優" or "女优")
            {
                metadata.Actors.AddRange(li.QuerySelectorAll("a")
                    .Select(a => System.Net.WebUtility.HtmlDecode(a.TextContent.Trim()))
                    .Where(value => value.Length > 0));
            }
            else if (label is "標籤" or "标籤" or "标签")
            {
                metadata.Tags.AddRange(li.QuerySelectorAll("a")
                    .Select(a => System.Net.WebUtility.HtmlDecode(a.TextContent.Trim()))
                    .Where(value => value.Length > 0));
            }
            else if (label is "廠商" or "厂商")
            {
                metadata.Studio = li.QuerySelector("a")?.TextContent.Trim() ?? "";
            }
            else if (label is "系列")
            {
                metadata.Series = li.QuerySelector("a")?.TextContent.Trim() ?? "";
            }
        }

        var dateMatch = Regex.Match(Text(".video-item .me-4"), @"\d{4}-\d{2}-\d{2}");
        if (DateTime.TryParse(dateMatch.Value, out var date)) metadata.ReleaseDate = date;

        foreach (var script in root.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(script.TextContent);
                if (json.RootElement.TryGetProperty("thumbnailUrl", out var covers)
                    && covers.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var cover = covers.EnumerateArray().FirstOrDefault().GetString();
                    if (!string.IsNullOrEmpty(cover))
                    {
                        metadata.CoverUrl = cover;
                        break;
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 页面上可能有非 VideoObject 的 JSON-LD；跳过继续找。
            }
        }

        return metadata;
    }
}

public sealed class VideoScrapeService : IDisposable
{
    private readonly VideoLibraryService _library;
    private readonly ConfigService? _configService;
    private readonly VideoScrapeHttpClient _http;
    private readonly JavBusScraper _scraper;
    private readonly JavDbScraper _javDb;
    private readonly AiravScraper _airav;
    private readonly ILogger? _logger;
    public event Action<VideoItem>? ItemChanged;
    public void Dispose() => _http.Dispose();

    public VideoScrapeService(VideoLibraryService library, VideoScrapeSettings settings, ILogger? logger = null)
    {
        _library = library;
        _logger = logger;
        _http = new VideoScrapeHttpClient(settings);
        _scraper = new JavBusScraper(_http, settings.JavBusBaseUrl ?? "");
        _javDb = new JavDbScraper(_http, settings.JavDbBaseUrl ?? "");
        _airav = new AiravScraper(_http);
    }

    public VideoScrapeService(VideoLibraryService library, ConfigService configService, ILogger? logger)
    {
        _library = library;
        _configService = configService;
        _logger = logger;
        var initial = Settings;
        _http = new VideoScrapeHttpClient(initial);
        _scraper = new JavBusScraper(_http, initial.JavBusBaseUrl);
        _javDb = new JavDbScraper(_http, initial.JavDbBaseUrl);
        _airav = new AiravScraper(_http);
    }

    private VideoScrapeSettings Settings => _configService?.Current.VideoScraping ?? new VideoScrapeSettings();

    public async Task ScrapeAsync(IEnumerable<string> ids, Action<VideoScrapeProgress>? progressCallback, CancellationToken ct)
    {
        var items = ids.Select(_library.GetById).Where(i => i != null).Cast<VideoItem>().ToList();
        var progress = new VideoScrapeProgress(items.Count);
        if (progressCallback != null) progress.Changed += progressCallback;
        progress.Publish();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Number.Length == 0)
            {
                var reparsed = VideoNumberParser.Parse(item.FilePath, _logger);
                if (reparsed.Number.Length > 0)
                {
                    item.Number = reparsed.Number;
                    item.Part = reparsed.Part;
                    _library.Update(item);
                    progress.Log($"{item.FileName}: 重新解析番号 {item.Number}");
                    _logger?.Info($"[Scrape] {item.FileName} 番号为空，已重新解析为 {item.Number}");
                }
            }
            if (item.Number.Length == 0)
            {
                item.ScrapeStatus = Models.ScrapeStatus.Skipped;
                progress.Completed++; progress.FailedCount++;
                progress.Log($"{item.FileName}: 未识别番号");
                _logger?.Warn($"[Scrape] 跳过 {item.FileName}: 未识别番号 (path={item.FilePath})");
                continue;
            }
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var meta = await _scraper.SearchAsync(item.Number, ct);
                _logger?.Info($"[Scrape] {item.Number} 搜索耗时 {sw.ElapsedMilliseconds}ms 命中={meta is not null}");

                // 第二源：JavDB 补充中文标题、评分、短评、剧照（失败不影响主数据）
                if (meta is not null)
                {
                    try
                    {
                        var dbMeta = await _javDb.SearchAsync(item.Number, ct);
                        if (dbMeta is not null)
                        {
                            MergeJavDb(item.Number, meta, dbMeta);
                            _logger?.Info($"[Scrape] {item.Number} JavDB 补充: 评分={dbMeta.Score} 中文标题={!LooksJapanese(dbMeta.Title)} 短评={dbMeta.Reviews.Count}");
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception dbEx)
                    {
                        _logger?.Warn($"[Scrape] {item.Number} JavDB 获取失败: {dbEx.Message}");
                    }
                }

                // JavDB 很多条目没有中文译名；两个源当前也都不提供简介。
                // AirAv 的 /cn 页面能补这两项，失败时保留 JavBus/JavDB 主数据。
                if (Settings.AiravEnabled && meta is not null && (LooksJapanese(meta.Title) || meta.Description.Length == 0))
                {
                    try
                    {
                        var airavMeta = await _airav.SearchAsync(item.Number, ct);
                        if (airavMeta is not null)
                        {
                            MergeAirav(meta, airavMeta);
                            _logger?.Info($"[Scrape] {item.Number} AirAv 补充: 中文标题={!LooksJapanese(meta.Title)} 简介={meta.Description.Length > 0}");
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception airavEx)
                    {
                        _logger?.Warn($"[Scrape] {item.Number} AirAv 获取失败: {airavEx.Message}");
                    }
                }

                if (meta is null)
                {
                    item.ScrapeStatus = Models.ScrapeStatus.NoMatch;
                    item.ScrapedAt = DateTime.UtcNow;
                    progress.Completed++; progress.NoMatchCount++;
                    progress.Log($"{item.Number}: 未匹配");
                }
                else
                {
                    ApplyMetadata(item, meta);
                    await DownloadImagesAsync(item, meta, ct);
                if (Settings.WriteNfo) WriteNfo(item);
                    item.ScrapeStatus = Models.ScrapeStatus.Success;
                    item.ScrapeSource = meta.Source;
                    item.ScrapedAt = DateTime.UtcNow;
                    progress.Completed++; progress.SuccessCount++;
                    progress.Log($"{item.Number}: 命中 {meta.Source}");
                    _logger?.Info($"[Scrape] {item.Number} 成功 source={meta.Source} actors={meta.Actors.Count} tags={meta.Tags.Count}");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                item.ScrapeStatus = Models.ScrapeStatus.Failed;
                progress.Completed++; progress.FailedCount++;
                progress.Log($"{item.Number}: {ex.Message}");
                _logger?.Error($"[Scrape] {item.Number} 刮削失败", ex);
            }
            _library.Update(item);
            ItemChanged?.Invoke(item);
            progress.Publish();
        }
    }

    /// <summary>用 JavDB 数据补齐 JavBus 缺失的字段；不覆盖已有值。</summary>
    internal static void MergeJavDb(string number, VideoScrapeMetadata primary, VideoScrapeMetadata javDb)
    {
        // JavDB 未翻译时 current-title 也是日文；只有无假名的标题才能当中文名合并。
        if (javDb.Title.Length > 0 && !LooksJapanese(javDb.Title))
        {
            if (primary.Title.Length > 0 && primary.OriginalTitle.Length == 0)
                primary.OriginalTitle = primary.Title;
            primary.Title = javDb.Title;
        }
        if (primary.OriginalTitle.Length == 0 && javDb.OriginalTitle.Length > 0) primary.OriginalTitle = javDb.OriginalTitle;
        if (javDb.Score > 0) primary.Score = javDb.Score;
        if (primary.Director.Length == 0) primary.Director = javDb.Director;
        if (primary.Studio.Length == 0) primary.Studio = javDb.Studio;
        if (primary.ReleaseDate is null) primary.ReleaseDate = javDb.ReleaseDate;
        if (primary.RuntimeMinutes == 0) primary.RuntimeMinutes = javDb.RuntimeMinutes;
        if (primary.ScoreVotes == 0) primary.ScoreVotes = javDb.ScoreVotes;
        if (!primary.HasMagnet) primary.HasMagnet = javDb.HasMagnet;
        if (!primary.HasChineseSubtitle) primary.HasChineseSubtitle = javDb.HasChineseSubtitle;
        if (primary.Actors.Count == 0 && javDb.Actors.Count > 0) primary.Actors = javDb.Actors;
        if (primary.Tags.Count == 0 && javDb.Tags.Count > 0) primary.Tags = javDb.Tags;
        foreach (var preview in javDb.PreviewImageUrls)
            if (!primary.PreviewImageUrls.Contains(preview)) primary.PreviewImageUrls.Add(preview);
        foreach (var review in javDb.Reviews)
            if (!primary.Reviews.Contains(review)) primary.Reviews.Add(review);
        foreach (var related in javDb.RelatedNumbers)
            if (related != number && !primary.RelatedNumbers.Contains(related)) primary.RelatedNumbers.Add(related);
        foreach (var pair in javDb.SourceUrls)
            primary.SourceUrls.TryAdd(pair.Key, pair.Value);
        if (primary.SourceUrls.TryGetValue("javbus-series", out var seriesUrl) && primary.SeriesNumbers.Count == 0)
        {
            // JavBus 系列页结构在后续补抓；当前先保留系列页地址，避免丢失来源。
            primary.SourceUrls["series-page"] = seriesUrl;
        }
    }

    /// <summary>用 AirAv 中文页补齐 JavBus/JavDB 缺失的展示字段；不覆盖已有值。</summary>
    internal static void MergeAirav(VideoScrapeMetadata primary, VideoScrapeMetadata airav)
    {
        if (airav.Title.Length > 0 && (primary.Title.Length == 0 || LooksJapanese(primary.Title)))
        {
            if (primary.OriginalTitle.Length == 0 && primary.Title.Length > 0)
                primary.OriginalTitle = primary.Title;
            primary.Title = airav.Title;
        }
        if (primary.OriginalTitle.Length == 0 && airav.OriginalTitle.Length > 0) primary.OriginalTitle = airav.OriginalTitle;
        if (primary.Description.Length == 0) primary.Description = airav.Description;
        if (primary.Director.Length == 0) primary.Director = airav.Director;
        if (primary.Studio.Length == 0) primary.Studio = airav.Studio;
        if (primary.Series.Length == 0) primary.Series = airav.Series;
        if (primary.ReleaseDate is null) primary.ReleaseDate = airav.ReleaseDate;
        if (primary.Actors.Count == 0 && airav.Actors.Count > 0) primary.Actors = airav.Actors;
        if (primary.Tags.Count == 0 && airav.Tags.Count > 0) primary.Tags = airav.Tags;
        if (string.IsNullOrEmpty(primary.CoverUrl)) primary.CoverUrl = airav.CoverUrl;
    }

    internal static bool LooksJapanese(string value) =>
        value.Any(ch => ch is >= '\u3041' and <= '\u3096' or >= '\u30A1' and <= '\u30FF');

    internal static void ApplyMetadata(VideoItem item, VideoScrapeMetadata meta)
    {
        item.Title = Pick(meta.Title, item.Title);
        item.OriginalTitle = Pick(meta.OriginalTitle, item.OriginalTitle);
        item.Description = Pick(meta.Description, item.Description);
        if (meta.Actors.Count > 0) item.Actors = meta.Actors;
        if (meta.Tags.Count > 0) item.Tags = meta.Tags;
        item.Series = Pick(meta.Series, item.Series);
        item.Studio = Pick(meta.Studio, item.Studio);
        item.Publisher = Pick(meta.Publisher, item.Publisher);
        item.Director = Pick(meta.Director, item.Director);
        item.ReleaseDate = meta.ReleaseDate ?? item.ReleaseDate;
        if (meta.RuntimeMinutes > 0) item.DurationSeconds = meta.RuntimeMinutes * 60.0;
        item.Score = meta.Score > 0 ? meta.Score : item.Score;
        item.CensorType = meta.CensorType == CensorType.Unknown ? item.CensorType : meta.CensorType;
        item.ScoreVotes = meta.ScoreVotes > 0 ? meta.ScoreVotes : item.ScoreVotes;
        item.HasMagnet = meta.HasMagnet || item.HasMagnet;
        item.HasChineseSubtitle = meta.HasChineseSubtitle || item.HasChineseSubtitle;
        if (meta.RelatedNumbers.Count > 0) item.RelatedNumbers = meta.RelatedNumbers;
        if (meta.Reviews.Count > 0) item.Reviews = meta.Reviews;
        if (meta.PreviewSourceUrls.Count > 0) item.PreviewSourceUrls = meta.PreviewSourceUrls;
        item.PreviewTotalCount = Math.Max(item.PreviewTotalCount, meta.PreviewSourceUrls.Count);
        if (meta.SeriesNumbers.Count > 0) item.SeriesNumbers = meta.SeriesNumbers;
        foreach (var pair in meta.SourceUrls)
            item.SourceUrls.TryAdd(pair.Key, pair.Value);
    }

    private static string Pick(string scraped, string current) => string.IsNullOrWhiteSpace(scraped) ? current : scraped.Trim();

    private async Task DownloadImagesAsync(VideoItem item, VideoScrapeMetadata meta, CancellationToken ct)
    {
        var dir = AppPaths.VideoArtworkDir;
        Directory.CreateDirectory(dir);

        if (!string.IsNullOrEmpty(meta.CoverUrl))
        {
            var fanartPath = Path.Combine(dir, $"{SanitizeFileName(item.Number)}-fanart.jpg");
            if (!File.Exists(fanartPath))
            {
                var bytes = await _http.GetByteArrayAsync(meta.CoverUrl, ct);
                await File.WriteAllBytesAsync(fanartPath, bytes, ct);
            }
            item.CoverPath = fanartPath;
            item.PosterPath = await VideoImageService.CreatePosterAsync(fanartPath, Path.Combine(dir, $"{SanitizeFileName(item.Number)}-poster.jpg"), ct);
        }

        var existing = new HashSet<string>(item.PreviewImages, StringComparer.OrdinalIgnoreCase);
        var downloadedCount = 0;
        foreach (var url in meta.PreviewSourceUrls.Count > 0 ? meta.PreviewSourceUrls : meta.PreviewImageUrls)
        {
            if (existing.Contains(url)) continue;
            if (downloadedCount >= Math.Max(0, 10 - item.PreviewImages.Count)) break;
            try
            {
                using var response = await _http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                var outputPath = Path.Combine(dir, $"{SanitizeFileName(item.Number)}-preview-{downloadedCount + 1:D2}.jpg");
                await using var output = File.Create(outputPath);
                await source.CopyToAsync(output, ct);
                if (!item.PreviewImages.Contains(outputPath)) item.PreviewImages.Add(outputPath);
                downloadedCount++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn($"[Scrape] {item.Number} 剧照下载失败: {ex.Message}");
            }
        }
    }

    internal static void WriteNfo(VideoItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FilePath)) return;
        var dir = Path.GetDirectoryName(item.FilePath);
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement("movie",
            new XElement("title", $"{item.Number} {item.Title}".Trim()),
            new XElement("originaltitle", item.OriginalTitle),
            new XElement("plot", new XCData(item.Description)),
            new XElement("rating", item.Score > 0 ? item.Score.ToString("0.#") : ""),
            item.ScoreVotes > 0 ? new XElement("votes", item.ScoreVotes) : null,
            new XElement("uniqueid", new XAttribute("type", "javbus"), new XAttribute("default", false), item.Number),
            new XElement("uniqueid", new XAttribute("type", "javdb"), new XAttribute("default", false), item.Number),
            item.SourceUrls.TryGetValue("javbus", out var sourceUrl) ? new XElement("sourceurl", sourceUrl) : null,
            item.HasChineseSubtitle ? new XElement("chinesesubtitled", true) : null,
            new XElement("runtime", (int)Math.Round(item.DurationSeconds / 60)),
            item.ReleaseDate is { } date ? new XElement("premiered", date.ToString("yyyy-MM-dd")) : null,
            new XElement("num", item.Number),
            new XElement("uniqueid", new XAttribute("type", "num"), new XAttribute("default", "true"), item.Number),
            item.Actors.Select(actor => new XElement("actor", new XElement("name", actor))),
            new XElement("director", item.Director),
            item.Tags.Select(tag => new XElement("genre", tag)),
            item.Tags.Select(tag => new XElement("tag", tag)),
            string.IsNullOrEmpty(item.Series) ? null : new XElement("set", new XElement("name", item.Series)),
            new XElement("studio", item.Studio),
            new XElement("publisher", item.Publisher),
            new XElement("mpaa", "NC-17"),
            new XElement("countrycode", "JP")
        )).Save(Path.ChangeExtension(item.FilePath, ".nfo"));
    }

    private static string SanitizeFileName(string name) => string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
}

/// <summary>JavDB 刮削源：提供中文标题、评分、短评、剧照。</summary>
public sealed class JavDbScraper : IVideoScraper
{
    private readonly VideoScrapeHttpClient _http;
    private readonly string _baseUrl;
    public string Id => "javdb";
    public string DisplayName { get; set; } = "JavDB";

    public JavDbScraper(VideoScrapeHttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public bool Supports(VideoNumberKind kind) => kind != VideoNumberKind.Unknown;

    public async Task<VideoScrapeMetadata?> SearchAsync(string number, CancellationToken ct)
    {
        var searchHtml = await _http.GetStringAsync($"{_baseUrl}/search?q={Uri.EscapeDataString(number)}&f=all", ct);
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(searchHtml);
        var first = doc.QuerySelector("div.item a.box");
        if (first is null) return null;

        var detailPath = first.GetAttribute("href") ?? "";
        if (detailPath.Length == 0) return null;
        var detailUrl = detailPath.StartsWith("http") ? detailPath : _baseUrl + detailPath;
        var html = await _http.GetStringAsync(detailUrl, ct);
        var root = parser.ParseDocument(html);

        string Text(string selector)
        {
            var node = root.QuerySelector(selector);
            return WebUtility.HtmlDecode(node?.TextContent.Trim() ?? "");
        }

        // 标题结构：<h2><strong>番号</strong><strong class="current-title">中文标题</strong>
        //          <span class="origin-title" style="display:none">日文原题</span></h2>
        var cnTitle = Text("strong.current-title");
        var origTitle = Text("span.origin-title");

        string Field(string label)
        {
            foreach (var p in root.QuerySelectorAll("nav.panel > div.panel-block"))
            {
                var strong = p.QuerySelector("strong");
                if (strong is null || !strong.TextContent.Replace(":", "").Replace("：", "").Trim().Equals(label, StringComparison.Ordinal)) continue;
                var valueNode = p.QuerySelector("span.value");
                var raw = valueNode?.InnerHtml ?? "";
                return WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(raw, "<[^>]+>", " ")).Trim();
            }
            return "";
        }

        var metadata = new VideoScrapeMetadata
        {
            Source = Id,
            Title = cnTitle.Length > 0 ? cnTitle : origTitle,
            OriginalTitle = origTitle,
            SourceUrls =
            {
                [Id] = detailUrl,
                [$"{Id}-magnets"] = $"{detailUrl.TrimEnd('/')}/magnets",
            },
            CoverUrl = JavBusScraper.ResolveUrl(_baseUrl,
                root.QuerySelector("img.video-cover")?.GetAttribute("src")
                ?? root.QuerySelector("img[data-src]")?.GetAttribute("data-src")
                ?? ""),
        };

        foreach (var a in root.QuerySelectorAll("nav.panel span.value a[href*=\"/actors/\"]"))
            metadata.Actors.Add(WebUtility.HtmlDecode(a.TextContent.Trim()));

        foreach (var a in root.QuerySelectorAll("nav.panel span.value a[href*=\"/tags?\"]"))
            metadata.Tags.Add(WebUtility.HtmlDecode(a.TextContent.Trim()));

        var dateText = Field("日期");
        if (DateTime.TryParse(dateText, out var date)) metadata.ReleaseDate = date;
        var runtimeText = Field("時長");
        var rtMatch = System.Text.RegularExpressions.Regex.Match(runtimeText, @"(\d+)");
        if (rtMatch.Success && double.TryParse(rtMatch.Groups[1].Value, out var minutes))
            metadata.RuntimeMinutes = (int)minutes;
        metadata.Director = Field("導演");
        metadata.Studio = Field("片商");

        // 评分在「評分」字段块里（搜索结果页才是 span.score 结构）
        var scoreBlock = Field("評分");
        var scoreMatch = System.Text.RegularExpressions.Regex.Match(scoreBlock, @"(\d+(?:\.\d+)?)分,\s*由(\d+)人");
        if (scoreMatch.Success)
        {
            if (double.TryParse(scoreMatch.Groups[1].Value, out var score))
                metadata.Score = score / 2.0;  // 5 分制转 10 分制
            if (int.TryParse(scoreMatch.Groups[2].Value, out var votes))
                metadata.ScoreVotes = votes;
        }

        metadata.HasMagnet = true;
        metadata.HasChineseSubtitle =
            root.QuerySelector(".tag-can-play.cnsub, .tag-cnsub") is not null
            || root.TextContent.Contains("含中字", StringComparison.OrdinalIgnoreCase);

        foreach (var a in root.QuerySelectorAll("a.tile-item[href*=\"c0.jdbstatic.com/samples\"], a.tile-item"))
        {
            var previewUrl = a.GetAttribute("href") ?? "";
            if (previewUrl.Contains("/samples/")) metadata.PreviewImageUrls.Add(previewUrl);
        }

        metadata.PreviewSourceUrls.AddRange(metadata.PreviewImageUrls);

        // 相关推荐：「TA(們)還出演過」区块的番号
        foreach (var tile in root.QuerySelectorAll("div.tile-images.tile-small a.tile-item .video-number"))
        {
            var relNum = WebUtility.HtmlDecode(tile.TextContent.Trim());
            var m = System.Text.RegularExpressions.Regex.Match(relNum, @"([A-Z]{2,8})-(\d{2,6})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var related = $"{m.Groups[1].Value.ToUpperInvariant()}-{m.Groups[2].Value}";
                if (!related.Equals(number, StringComparison.OrdinalIgnoreCase) && !metadata.RelatedNumbers.Contains(related))
                    metadata.RelatedNumbers.Add(related);
            }
        }

        // 短评：详情页内嵌的 review-tab data-url 指向 AJAX 接口
        try
        {
            var reviewTab = root.QuerySelector("a.review-tab");
            var reviewPath = reviewTab?.GetAttribute("data-url");
            if (!string.IsNullOrEmpty(reviewPath))
            {
                var js = await _http.GetStringAsync(detailUrl.StartsWith("http") ? new Uri(new Uri(detailUrl), reviewPath).ToString() : _baseUrl + reviewPath, ct);
                // 返回的是 JS 片段，HTML 属性被 \" 转义
                var fixedJs = js.Replace("\\\"", "\"").Replace("<\\/", "</").Replace("\\n", "\n");
                var reviewDoc = parser.ParseDocument($"<html><body>{fixedJs}</body></html>");
                foreach (var c in reviewDoc.QuerySelectorAll("dt.review-item div.content p"))
                    metadata.Reviews.Add(WebUtility.HtmlDecode(c.TextContent.Trim()));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 短评获取失败不影响主数据 */ }

        return string.IsNullOrEmpty(metadata.Title) ? null : metadata;
    }
}
