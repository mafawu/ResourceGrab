using System.Net;
using System.Text.Json;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: ThePornDB — Western 内容 REST API 源。
// 参考 amane（github.com/sqzw-x/amane）crawlers/sites/theporndb.py 的同源设计重写。
// 端点: 搜索 GET https://api.theporndb.net/scenes?q={番号}，详情 GET /scenes/{id}；
// 请求头 Authorization: Bearer <apiKey> + Accept: application/json。apiKey 由 DI 注册时
// 传入第三个构造参数（来自 sourceConfigs.theporndb.apiKey）；为空时本源直接返回 NoMatch。
// 基类 GetHtmlAsync 不支持自定义请求头，故直接走 protected Fetcher.GetAsync 并复刻基类
// 的错误语义（BlockDetector 判定 + 状态码检查 → Blocked/HttpError）。
// 响应用 System.Text.Json 解析；番号匹配用基类 MatchesNumber 校验 title/slug。
// ---------------------------------------------------------------------------

public sealed class ThePornDbSource : HtmlScrapeSourceBase
{
    private const string ApiBase = "https://api.theporndb.net";

    private readonly string? _apiKey;

    public ThePornDbSource(IResilientFetcher fetcher, ILogger? logger = null, string? apiKey = null)
        : base(fetcher, logger)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    public override string Id => "theporndb";
    public override string DisplayName => "ThePornDB";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Western]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        // 未配置 apiKey 时无法调用 API：直接按 NoMatch 处理（不抛错，避免污染聚合统计）。
        // 需在 sourceConfigs.theporndb.apiKey 配置并经 DI 第三个构造参数传入。
        if (_apiKey is null) return null;

        // ── 搜索 ──
        var searchUrl = $"{ApiBase}/scenes?q={Uri.EscapeDataString(request.Number)}";
        var searchBody = await GetJsonAsync(searchUrl, ct);
        var matchId = FindMatchId(searchBody, request.Number);
        if (matchId is null) return null;

        // ── 详情 ──
        var detailUrl = $"{ApiBase}/scenes/{matchId}";
        var detailBody = await GetJsonAsync(detailUrl, ct);
        using var doc = JsonDocument.Parse(detailBody);
        // v2 详情响应可能包一层 {"data":{...}}，也可能是裸对象，两者兼容
        var scene = doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
            ? data
            : doc.RootElement;

        var metadata = NewMetadata(Id, detailUrl);
        metadata.Title = Str(scene, "title") ?? "";
        metadata.Description = Str(scene, "description") ?? "";
        metadata.ReleaseDate = ParseDate(Str(scene, "date"));
        // 置信度低：duration 的单位（分/秒）需联网冒烟核实
        metadata.RuntimeMinutes = ParseMinutes(Num(scene, "duration"));
        metadata.CoverUrl = GetCover(scene) ?? "";
        metadata.Studio = ObjectName(scene, "site") ?? ObjectName(scene, "studio") ?? "";

        AddNames(scene, "performers", metadata.Actors);
        AddNames(scene, "actors", metadata.Actors);
        AddNames(scene, "tags", metadata.Tags);

        return metadata.Title.Length == 0 && metadata.CoverUrl.Length == 0 ? null : metadata;
    }

    /// <summary>
    /// GET JSON API：基类 GetHtmlAsync 不支持自定义请求头，直接走 Fetcher 并复刻
    /// 其错误语义（Blocked → HttpRequestException(403)，4xx/5xx → 带 StatusCode 抛出）。
    /// </summary>
    private async Task<string> GetJsonAsync(string url, CancellationToken ct)
    {
        var result = await Fetcher.GetAsync(url, new FetchOptions
        {
            SourceId = Id,
            ExtraHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {_apiKey}",
                ["Accept"] = "application/json",
            },
        }, ct);
        if (BlockDetector.IsBlocked(result.StatusCode, result.Body))
            throw new HttpRequestException("被反爬拦截", null, HttpStatusCode.Forbidden);
        if (result.StatusCode is < 200 or >= 400)
            throw new HttpRequestException($"HTTP {result.StatusCode}", null, (HttpStatusCode)result.StatusCode);
        return result.Body;
    }

    /// <summary>遍历搜索结果，用 MatchesNumber 对 title+slug 精确校验，返回首个命中场景的 id。</summary>
    private static string? FindMatchId(string body, string number)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in data.EnumerateArray())
        {
            var title = Str(item, "title") ?? "";
            var slug = Str(item, "slug") ?? "";
            if (!MatchesNumber($"{title} {slug}", number)) continue;
            var id = Str(item, "id");
            if (id is not null) return id;
        }
        return null;
    }

    /// <summary>cover_url 可能是字符串或 {url:...} 对象，两者兼容。</summary>
    private static string? GetCover(JsonElement scene)
    {
        if (scene.ValueKind != JsonValueKind.Object) return null;
        if (!scene.TryGetProperty("cover_url", out var cover)
            && !scene.TryGetProperty("cover", out cover)
            && !scene.TryGetProperty("background", out cover))
            return null;
        if (cover.ValueKind == JsonValueKind.String) return cover.GetString();
        if (cover.ValueKind == JsonValueKind.Object
            && cover.TryGetProperty("url", out var url)
            && url.ValueKind == JsonValueKind.String)
            return url.GetString();
        return null;
    }

    private static string? Str(JsonElement el, params string[] keys)
    {
        foreach (var key in keys)
            if (el.ValueKind == JsonValueKind.Object
                && el.TryGetProperty(key, out var v)
                && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    /// <summary>取数值/字符串字段的文本（时长等可能是数字）。</summary>
    private static string? Num(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v)) return null;
        return v.ValueKind is JsonValueKind.Number or JsonValueKind.String ? v.ToString() : null;
    }

    private static string? ObjectName(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Object
            && v.TryGetProperty("name", out var n)
            && n.ValueKind == JsonValueKind.String)
            return n.GetString();
        return null;
    }

    /// <summary>数组字段（元素为字符串或 {name:...}）→ 字符串列表，去重、去占位符。</summary>
    private static void AddNames(JsonElement el, string key, List<string> target)
    {
        if (el.ValueKind != JsonValueKind.Object
            || !el.TryGetProperty(key, out var v)
            || v.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in v.EnumerateArray())
        {
            var name = item.ValueKind == JsonValueKind.String ? item.GetString()
                : item.ValueKind == JsonValueKind.Object
                  && item.TryGetProperty("name", out var n)
                  && n.ValueKind == JsonValueKind.String ? n.GetString()
                : null;
            var text = name?.Trim();
            if (!string.IsNullOrEmpty(text) && text != "-" && !target.Contains(text)) target.Add(text);
        }
    }
}
