using ResourceGrab.Core.Logging;

namespace ResourceGrab.Core.Sources.ShortDrama;

/// <summary>
/// 西瓜短剧源。站点是纯服务端渲染 HTML，所以本类只做「取页面 → 交解析器 → 组模型」。
/// <para>
/// 取流（<see cref="GetPlayFrameAsync"/>）单独一个方法而不是塞在详情里：
/// 签名地址约 2 小时过期，只有点播放/下载的那一刻取才有意义。
/// </para>
/// </summary>
public sealed class XiguaDramaSource : IShortDramaSource
{
    /// <summary>源标识（配置里的 key）。</summary>
    public const string SourceId = "xigua";

    private readonly XiguaDramaHttpClient _http;
    private readonly ILogger? _logger;

    public XiguaDramaSource(XiguaDramaHttpClient http, ILogger? logger = null)
    {
        _http = http;
        _logger = logger;
    }

    public string Id => SourceId;

    public string DisplayName => "西瓜短剧";

    public async Task<ShortDramaListing> GetFeedAsync(
        DramaFeedKind kind, int page, string? scope = null, CancellationToken ct = default)
    {
        var path = XiguaDramaConstants.FeedPath(kind, page, scope);
        var html = await _http.GetStringAsync(path, ct).ConfigureAwait(false);
        var doc = XiguaDramaParser.ParseDocument(html);
        var items = XiguaDramaParser.ParseCards(doc, _http.CurrentBaseUrl);
        _logger?.Info($"[XiguaDrama] 列表 {kind} page={page} → {items.Count} 条");
        return new ShortDramaListing
        {
            Page = Math.Max(1, page),
            TotalPages = XiguaDramaParser.ParseTotalPages(doc),
            Items = items,
        };
    }

    public async Task<ShortDramaListing> SearchAsync(string keyword, int page, CancellationToken ct = default)
    {
        var path = XiguaDramaConstants.SearchPath(keyword, page);
        var html = await _http.GetStringAsync(path, ct).ConfigureAwait(false);
        var doc = XiguaDramaParser.ParseDocument(html);
        var items = XiguaDramaParser.ParseCards(doc, _http.CurrentBaseUrl);
        _logger?.Info($"[XiguaDrama] 搜索「{keyword}」page={page} → {items.Count} 条");
        return new ShortDramaListing
        {
            Page = Math.Max(1, page),
            TotalPages = XiguaDramaParser.ParseTotalPages(doc),
            TotalCount = XiguaDramaParser.ParseSearchTotal(doc),
            Items = items,
        };
    }

    public async Task<ShortDramaDetail?> GetDetailAsync(string dramaId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dramaId)) return null;
        var html = await _http.GetStringAsync(XiguaDramaConstants.DetailPath(dramaId), ct).ConfigureAwait(false);
        var detail = XiguaDramaParser.ParseDetail(html, _http.CurrentBaseUrl);
        if (detail is null)
            _logger?.Warn($"[XiguaDrama] 详情解析为空，站点可能改版：id={dramaId}");
        else
            _logger?.Info($"[XiguaDrama] 详情 {detail.Id}「{detail.Title}」{detail.TotalEpisodes} 集");
        return detail;
    }

    public async Task<DramaPlayFrame?> GetPlayFrameAsync(
        string dramaId, int episode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dramaId)) return null;
        var html = await _http
            .GetStringAsync(XiguaDramaConstants.PlayFramePath(dramaId, episode), ct)
            .ConfigureAwait(false);
        var frame = XiguaDramaParser.ParsePlayFrame(html, dramaId, episode);
        if (frame is null)
            _logger?.Warn($"[XiguaDrama] 取流失败，播放帧无 data-play-src：id={dramaId} ep={episode}");
        return frame;
    }

    public async Task<IReadOnlyList<ShortDramaSummary>> GetRankAsync(
        DramaRankKind kind, CancellationToken ct = default)
    {
        var html = await _http.GetStringAsync(XiguaDramaConstants.RankPath(kind), ct).ConfigureAwait(false);
        var items = XiguaDramaParser.ParseRank(html, _http.CurrentBaseUrl);
        _logger?.Info($"[XiguaDrama] 榜单 {kind} → {items.Count} 条");
        return items;
    }

    public async Task<IReadOnlyList<ShortDramaTag>> GetTagsAsync(CancellationToken ct = default)
    {
        var html = await _http.GetStringAsync(XiguaDramaConstants.TagsPath, ct).ConfigureAwait(false);
        return XiguaDramaParser.ParseTags(html, _http.CurrentBaseUrl);
    }

    public string? LineHost => _http.LastGoodHost ?? _http.CurrentBaseUrl;

    public Task<IReadOnlyList<string>> RefreshLinesAsync(CancellationToken ct = default)
        => _http.DiscoverHostsAsync(ct);
}
