using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: DMM — 有码核心源（HTML + GraphQL 三端点，日文原题、高清图）。
// 参考 amane（github.com/sqzw-x/amane）crawlers/sites/dmm.py + dmm_api.py 的同源设计重写。
// 番号规范化: 前缀+去零数字；搜索构造零填充 5 位变体与原始番号两个都试
//   （GET {base}/search/=/searchstr={变体}/sort=ranking/）。
// 搜索命中: 从页面 script 内嵌 JSON 提取 detailUrl（先试 detailUrl\":\ 转义形态，回退非转义），
//   unicode 解码后 MatchesNumber 精确校验，按类别优先级（Digital > Fanza TV > DMM TV > Mono）取最佳。
// 详情按 URL 路由三通道: /digital/（取 cid）→ POST api.video.dmm.co.jp/graphql；
//   tv.dmm.co.jp（content=）→ api.tv.dmm.co.jp/graphql；tv.dmm.com（seasonId=）→ api.tv.dmm.com/graphql；
//   HTML 通道（/mono/ 等）AngleSharp 解析 dl/dt/dd（メーカー=Studio、監督=Director、
//   ジャンル=Tags、出演者=Actors）+ JSON-LD 回退。GraphQL 失败/未命中自动回退 HTML 通道。
// 年龄墙: 需 cookie age_check_done=1；基类 GetHtmlAsync 不支持 Cookie 头，依赖抓取栈
//   Cookie 罐或 sourceConfigs.dmm.Cookie 携带（留待联网核实）。
// 图片增强: 封面 ps.jpg→pl.jpg 取大图；样例图 -(\d+).jpg→jp-\1.jpg。
// ---------------------------------------------------------------------------

public sealed class DmmSource : HtmlScrapeSourceBase
{
    private const string BaseUrl = "https://www.dmm.co.jp";

    // 置信度低：GraphQL payload 为最简猜测形态（字段名按 amane dmm_api.py 语义推断），需联网冒烟核实
    private const string DigitalQuery =
        "query($id: String!) { ppvContent(id: $id) { title description releaseDate runtime " +
        "maker { name } label { name } director { name } series { name } " +
        "actresses { name } genres { name } packageImage { url } sampleImages { url } } }";
    private const string FanzaTvQuery =
        "query($id: String!) { content(id: $id) { title description releaseDate runtime " +
        "maker { name } director { name } actresses { name } genres { name } " +
        "packageImage { url } sampleImages { url } } }";
    private const string DmmTvQuery =
        "query($id: String!) { season(id: $id) { title description releaseDate " +
        "staffs { roleName name } genres { name } packageImage { url } sampleImages { url } } }";

    // 搜索页 script 内嵌 JSON 的 detailUrl 提取：转义形态（detailUrl\":\）优先，非转义回退
    private static readonly Regex EscapedDetailUrlRegex = new("detailUrl\\\\\":\\\\\"([^\"]+)");
    private static readonly Regex PlainDetailUrlRegex = new("detailUrl\":\"([^\"]+)");

    public DmmSource(IResilientFetcher fetcher, ILogger? logger = null) : base(fetcher, logger) { }

    public override string Id => "dmm";
    public override string DisplayName => "DMM";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([
            VideoContentKind.Censored, VideoContentKind.Amateur, VideoContentKind.Hentai,
        ]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        var (prefix, digits) = SplitNumber(request.Number);
        if (digits.Length == 0) return null;

        // 零填充 5 位变体（对齐 DMM cid 形态，如 SNOS-001 → SNOS00001）优先，原始番号其次，两个都试
        var padded = prefix + digits.PadLeft(5, '0');
        var variants = padded == request.Number ? new[] { request.Number } : new[] { padded, request.Number };

        Exception? lastError = null;
        foreach (var variant in variants)
        {
            try
            {
                var searchUrl = $"{BaseUrl}/search/=/searchstr={Uri.EscapeDataString(variant)}/sort=ranking/";
                var searchHtml = await GetHtmlAsync(searchUrl, referer: BaseUrl + "/", ct: ct);
                var detailUrl = ExtractDetailUrl(searchHtml, request.Number);
                if (detailUrl is null) continue;
                return await FetchDetailAsync(ResolveUrl(BaseUrl, detailUrl), request, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                Logger?.Warn($"[DMM] 搜索变体失败: {variant} ({ex.Message})");
            }
        }
        if (lastError is not null) throw lastError;
        return null;
    }

    /// <summary>按详情页 URL 路由：GraphQL 三端点（失败回退 HTML）或纯 HTML 通道。</summary>
    private async Task<VideoScrapeMetadata?> FetchDetailAsync(string detailUrl, VideoSourceRequest request, CancellationToken ct)
    {
        var metadata = NewMetadata(Id, detailUrl);

        // 置信度低：GraphQL 端点与参数形态按 amane 语义推断，需联网冒烟核实
        string? apiId; string endpoint; string query;
        if (detailUrl.Contains("/digital/"))
        {
            (apiId, endpoint, query) = (ExtractParam(detailUrl, "cid"), "https://api.video.dmm.co.jp/graphql", DigitalQuery);
        }
        else if (detailUrl.Contains("tv.dmm.co.jp"))
        {
            (apiId, endpoint, query) = (ExtractParam(detailUrl, "content"), "https://api.tv.dmm.co.jp/graphql", FanzaTvQuery);
        }
        else if (detailUrl.Contains("tv.dmm.com"))
        {
            (apiId, endpoint, query) = (ExtractParam(detailUrl, "seasonId"), "https://api.tv.dmm.com/graphql", DmmTvQuery);
        }
        else
        {
            (apiId, endpoint, query) = (null, "", "");
        }

        if (apiId is not null)
        {
            try
            {
                if (await TryGraphQLAsync(endpoint, query, apiId, metadata, ct)) return metadata;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger?.Warn($"[DMM] GraphQL 通道失败，回退 HTML: {ex.Message}");
            }
        }

        // HTML 通道（/mono/ 等）或 GraphQL 未命中时的回退
        var html = await GetHtmlAsync(detailUrl, referer: BaseUrl + "/", ct: ct);
        ParseHtmlDetail(html, request.Number, metadata);
        return HasData(metadata) ? metadata : null;
    }

    private async Task<bool> TryGraphQLAsync(string endpoint, string query, string id, VideoScrapeMetadata metadata, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["query"] = query,
            ["variables"] = new Dictionary<string, string> { ["id"] = id },
        });
        var body = await PostAsync(endpoint, "application/json", payload, referer: BaseUrl + "/", ct: ct);
        using var doc = JsonDocument.Parse(body);
        MapGraphQL(doc.RootElement, metadata);
        if (metadata.CoverUrl.Length > 0) metadata.CoverUrl = EnhanceImage(metadata.CoverUrl);
        return HasData(metadata);
    }

    // ── 搜索页 detailUrl 提取 ──

    private static string? ExtractDetailUrl(string html, string number)
    {
        var candidates = new List<string>();
        foreach (var regex in new[] { EscapedDetailUrlRegex, PlainDetailUrlRegex })
            foreach (Match m in regex.Matches(html))
                candidates.Add(DecodeUrl(m.Groups[1].Value));

        string? best = null;
        var bestScore = -1;
        foreach (var url in candidates.Distinct())
        {
            if (!MatchesNumber(url, number)) continue;
            // 类别优先级：Digital 100 > Fanza TV 90 > DMM TV 80 > Mono 等 60
            var score = url.Contains("/digital/") ? 100
                : url.Contains("tv.dmm.co.jp") ? 90
                : url.Contains("tv.dmm.com") ? 80
                : 60;
            if (score > bestScore) { bestScore = score; best = url; }
        }
        return best;
    }

    /// <summary>还原 script JSON 里的 URL：剥转义尾斜杠、\/ 还原、\uXXXX unicode 解码。</summary>
    private static string DecodeUrl(string raw)
    {
        var url = raw.TrimEnd('\\').Replace("\\/", "/");
        try { return Regex.Unescape(url); }
        catch (ArgumentException) { return url; }
    }

    private static string? ExtractParam(string url, string name)
    {
        var m = Regex.Match(url, $@"{name}=([0-9A-Za-z_.-]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    // ── GraphQL 响应 → 元数据（容错式字段映射） ──

    private static bool HasData(VideoScrapeMetadata m) => m.Title.Length > 0 || m.CoverUrl.Length > 0;

    private static void MapGraphQL(JsonElement element, VideoScrapeMetadata m)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject()) ApplyGraphQLField(prop.Name, prop.Value, m);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) MapGraphQL(item, m);
                break;
        }
    }

    private static void ApplyGraphQLField(string key, JsonElement value, VideoScrapeMetadata m)
    {
        switch (key.ToLowerInvariant())
        {
            case "title" when IsNonEmptyString(value) && m.Title.Length == 0:
                m.Title = value.GetString()!; break;
            case "description" or "comment" when IsNonEmptyString(value) && m.Description.Length == 0:
                m.Description = value.GetString()!; break;
            case "maker" or "studio" when m.Studio.Length == 0:
                m.Studio = ToName(value) ?? m.Studio; break;
            case "label" or "publisher" when m.Publisher.Length == 0:
                m.Publisher = ToName(value) ?? m.Publisher; break;
            case "series" when m.Series.Length == 0:
                m.Series = ToName(value) ?? m.Series; break;
            case "director" when m.Director.Length == 0:
                m.Director = ToName(value) ?? m.Director; break;
            case "actresses" or "actors" or "casts" or "cast" or "performers":
                CollectNames(value, m.Actors); break;
            case "genres" or "categories" or "tags":
                CollectNames(value, m.Tags); break;
            case "releasedate" or "daterelease" or "date" when value.ValueKind == JsonValueKind.String && m.ReleaseDate is null:
                m.ReleaseDate = ParseDate(value.GetString()); break;
            case "runtime" or "durationminutes" or "runningtime" when m.RuntimeMinutes == 0:
                m.RuntimeMinutes = ParseMinutes(value.ToString()); break;
            case "packageimage" or "jacketimage" or "packageimageurl" or "jacketimageurl" or "imagelarge" when m.CoverUrl.Length == 0:
                m.CoverUrl = ToUrl(value) ?? m.CoverUrl; break;
            case "sampleimages" or "sampleimageurls" or "sampleimageurl" or "previewimages":
                CollectUrls(value, m.PreviewImageUrls); break;
            case "staffs":
                ApplyStaffs(value, m); break;   // DMM TV：导演取 roleName == "監督" 的 staff
        }
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) MapGraphQL(value, m);
    }

    private static void ApplyStaffs(JsonElement value, VideoScrapeMetadata m)
    {
        if (value.ValueKind != JsonValueKind.Array || m.Director.Length > 0) return;
        foreach (var staff in value.EnumerateArray())
        {
            var role = staff.ValueKind == JsonValueKind.Object
                       && staff.TryGetProperty("roleName", out var r)
                       && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (role != "監督") continue;
            var name = ToName(staff);
            if (!string.IsNullOrEmpty(name)) { m.Director = name!; break; }
        }
    }

    private static bool IsNonEmptyString(JsonElement v)
        => v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 };

    private static string? ToName(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Object
            && v.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            return n.GetString();
        if (v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray()) { var name = ToName(item); if (!string.IsNullOrEmpty(name)) return name; }
        return null;
    }

    private static void CollectNames(JsonElement v, List<string> target)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.String: Add(v.GetString()); break;
            case JsonValueKind.Object: Add(ToName(v)); break;
            case JsonValueKind.Array:
                foreach (var item in v.EnumerateArray()) CollectNames(item, target);
                break;
        }
        void Add(string? name)
        {
            var text = name?.Trim();
            if (!string.IsNullOrEmpty(text) && text != "-" && !target.Contains(text)) target.Add(text);
        }
    }

    private static string? ToUrl(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "largeUrl", "url", "large", "src" })
                if (v.TryGetProperty(key, out var u) && u.ValueKind == JsonValueKind.String) return u.GetString();
        if (v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray()) { var url = ToUrl(item); if (!string.IsNullOrEmpty(url)) return url; }
        return null;
    }

    private static void CollectUrls(JsonElement v, List<string> target)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.String: Add(v.GetString()); break;
            case JsonValueKind.Object: Add(ToUrl(v)); break;
            case JsonValueKind.Array:
                foreach (var item in v.EnumerateArray()) CollectUrls(item, target);
                break;
        }
        void Add(string? url)
        {
            if (!string.IsNullOrEmpty(url) && !target.Contains(url)) target.Add(url);
        }
    }

    // ── HTML 通道解析（新旧两代布局兼容 + JSON-LD 回退） ──

    private static void ParseHtmlDetail(string html, string number, VideoScrapeMetadata metadata)
    {
        var root = new HtmlParser().ParseDocument(html);

        if (metadata.Title.Length == 0)
        {
            var rawTitle = (root.QuerySelector("h1#title")?.TextContent
                            ?? root.QuerySelector("meta[property='og:title']")?.GetAttribute("content")
                            ?? root.QuerySelector("h1")?.TextContent ?? "").Trim();
            metadata.Title = Regex.Replace(rawTitle, $"^{Regex.Escape(number)}\\s*", "", RegexOptions.IgnoreCase).Trim();
        }

        if (metadata.CoverUrl.Length == 0)
        {
            var cover = root.QuerySelector("img#package-img")?.GetAttribute("src")
                        ?? root.QuerySelector("img[src*='ps.jpg']")?.GetAttribute("src")
                        ?? root.QuerySelector("meta[property='og:image']")?.GetAttribute("content") ?? "";
            metadata.CoverUrl = EnhanceImage(cover);
        }

        if (metadata.ReleaseDate is null) metadata.ReleaseDate = ParseDate(TextByLabel(root, "発売日"));
        if (metadata.RuntimeMinutes == 0) metadata.RuntimeMinutes = ParseMinutes(TextByLabel(root, "収録時間"));
        if (metadata.Studio.Length == 0) metadata.Studio = WebUtility.HtmlDecode(TextByLabel(root, "メーカー") ?? "");
        if (metadata.Publisher.Length == 0) metadata.Publisher = WebUtility.HtmlDecode(TextByLabel(root, "レーベル") ?? "");
        if (metadata.Series.Length == 0) metadata.Series = WebUtility.HtmlDecode(TextByLabel(root, "シリーズ") ?? "");
        if (metadata.Director.Length == 0) metadata.Director = WebUtility.HtmlDecode(TextByLabel(root, "監督") ?? "");

        if (metadata.Tags.Count == 0 && ElementByLabel(root, "ジャンル") is { } tagCell)
            AddLinksOrSplit(tagCell, metadata.Tags);
        if (metadata.Actors.Count == 0 && ElementByLabel(root, "出演者", "出演") is { } actorCell)
            AddLinksOrSplit(actorCell, metadata.Actors);

        // JSON-LD 回退（新旧布局均可能内嵌）
        if (metadata.Title.Length == 0 || metadata.CoverUrl.Length == 0 || metadata.Actors.Count == 0)
        {
            foreach (var script in root.QuerySelectorAll("script[type='application/ld+json']"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(script.TextContent);
                    MapJsonLd(doc.RootElement, metadata);
                }
                catch (JsonException) { /* 非法 JSON-LD 忽略 */ }
            }
        }
    }

    private static void MapJsonLd(JsonElement el, VideoScrapeMetadata m)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray()) MapJsonLd(item, m);
            return;
        }
        if (el.ValueKind != JsonValueKind.Object) return;

        if (m.Title.Length == 0 && el.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            m.Title = name.GetString() ?? "";
        if (m.CoverUrl.Length == 0 && el.TryGetProperty("image", out var img))
        {
            var url = ToUrl(img);
            if (!string.IsNullOrEmpty(url)) m.CoverUrl = EnhanceImage(url!);
        }
        if (m.ReleaseDate is null && el.TryGetProperty("datePublished", out var date) && date.ValueKind == JsonValueKind.String)
            m.ReleaseDate = ParseDate(date.GetString());
        if (m.Director.Length == 0 && el.TryGetProperty("director", out var director))
        {
            var n = ToName(director);
            if (!string.IsNullOrEmpty(n)) m.Director = n!;
        }
        if (m.Studio.Length == 0 && el.TryGetProperty("productionCompany", out var company))
        {
            var n = ToName(company);
            if (!string.IsNullOrEmpty(n)) m.Studio = n!;
        }
        if (el.TryGetProperty("actor", out var actors)) CollectNames(actors, m.Actors);
        if (el.TryGetProperty("genre", out var genre)) CollectNames(genre, m.Tags);
    }

    /// <summary>图片增强：封面 ps.jpg→pl.jpg 取大图；样例图 xxx-1.jpg→xxxjp-1.jpg。</summary>
    private static string EnhanceImage(string url)
    {
        if (url.Length == 0) return url;
        if (url.EndsWith("ps.jpg", StringComparison.OrdinalIgnoreCase))
            return url[..^"ps.jpg".Length] + "pl.jpg";
        return Regex.Replace(url, @"-(\d+)\.jpg$", "jp-$1.jpg", RegexOptions.IgnoreCase);
    }

    /// <summary>按标签扫描 th/dt/td 行（旧布局 table、新布局 dl/dt/dd 均覆盖），返回值单元格。</summary>
    private static string? TextByLabel(IDocument root, params string[] labels)
        => ElementByLabel(root, labels)?.TextContent.Trim();

    private static IElement? ElementByLabel(IDocument root, params string[] labels)
    {
        foreach (var cell in root.QuerySelectorAll("th,dt,td"))
        {
            var label = cell.TextContent.Trim().TrimEnd('：', ':').Trim();
            // 标签单元格应为短文本，避免把内容列误当标签
            if (label.Length == 0 || label.Length > 12 || !labels.Contains(label)) continue;
            var value = cell.NextElementSibling;
            if (value is not null) return value;
        }
        return null;
    }

    /// <summary>值单元格优先取链接文本，无链接时按分隔符拆分纯文本。</summary>
    private static void AddLinksOrSplit(IElement cell, List<string> target)
    {
        var links = cell.QuerySelectorAll("a");
        if (links.Any())
        {
            foreach (var a in links)
            {
                var text = WebUtility.HtmlDecode(a.TextContent.Trim());
                if (text.Length > 0 && text != "-") target.Add(text);
            }
            return;
        }
        foreach (var part in WebUtility.HtmlDecode(cell.TextContent).Split('　', ' ', '、', ',', '/'))
        {
            var text = part.Trim();
            if (text.Length > 0 && text != "-") target.Add(text);
        }
    }
}
