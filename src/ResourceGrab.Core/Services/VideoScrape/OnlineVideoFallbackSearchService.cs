using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Utils;

namespace ResourceGrab.Core.Services.VideoScrape;

/// <summary>兜底命中：一张搜索摘要卡片 + 已取到的完整详情（供详情侧栏同步渲染）。</summary>
public sealed record OnlineVideoFallbackHit(OnlineVideoSummary Summary, OnlineVideoDetail Detail);

/// <summary>
/// 在线搜索兜底：MissAV 关键词搜索无结果时，若关键词可解析为番号，
/// 按 config advanced.contentRoutes 的来源顺序（跳过 missav 本身、仅取已启用且支持该内容类型的源）
/// 逐源按番号取详情，首个命中的源即返回。来源顺序可在配置中调整（"根据源进行的排序"），
/// 此前刮削过同一番号的直接吃快照缓存，零网络。
/// </summary>
public sealed class OnlineVideoFallbackSearchService
{
    private readonly IVideoScrapeSourceRegistry _registry;
    private readonly VideoScrapeAdvancedSettings _advanced;
    private readonly IVideoSourceSnapshotCache? _cache;
    private readonly ILogger? _logger;

    public OnlineVideoFallbackSearchService(
        IVideoScrapeSourceRegistry registry,
        VideoScrapeAdvancedSettings advanced,
        IVideoSourceSnapshotCache? cache = null,
        ILogger? logger = null)
    {
        _registry = registry;
        _advanced = advanced;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>兜底摘要的合成 Id 规则（App 侧用它拼详情缓存键）：{sourceId}/{番号小写}。</summary>
    public static string MakeSummaryId(string sourceId, string number)
        => $"{sourceId}/{number.ToLowerInvariant()}";

    public async Task<OnlineVideoFallbackHit?> SearchAsync(string keyword, CancellationToken ct = default)
    {
        var parsed = VideoNumberParser.Parse(keyword);
        if (parsed.Number.Length == 0)
        {
            _logger?.Info("[FallbackSearch] 关键词无法解析为番号，跳过换源兜底");
            return null;
        }
        var kind = parsed.Kind.ToContentKind();
        if (kind is VideoContentKind.Unknown) kind = VideoContentKind.Censored;

        foreach (var sourceId in _advanced.GetSourcesForKind(kind))
        {
            ct.ThrowIfCancellationRequested();
            // missav 就是刚才没搜到的那个源，不重复尝试
            if (sourceId.Equals("missav", StringComparison.OrdinalIgnoreCase)) continue;
            if (!_advanced.IsSourceEnabled(sourceId)) continue;
            var source = _registry.Get(sourceId);
            if (source is null || !source.SupportedKinds.Contains(kind)) continue;

            var meta = await FetchMetadataAsync(source, parsed.Number, kind, ct);
            if (meta is null) continue;

            _logger?.Info($"[FallbackSearch] {sourceId} 命中番号 {parsed.Number}");
            return BuildHit(source, parsed.Number, meta);
        }

        _logger?.Info($"[FallbackSearch] 所有兜底源均未命中番号 {parsed.Number}");
        return null;
    }

    /// <summary>先查快照缓存（此前刮削过同一番号），未命中再联网取。</summary>
    private async Task<VideoScrapeMetadata?> FetchMetadataAsync(
        IVideoScrapeSource source, string number, VideoContentKind kind, CancellationToken ct)
    {
        if (_cache is not null)
        {
            var snapshot = await _cache.GetAsync(number, source.Id);
            if (snapshot?.Metadata is { } cached)
            {
                _logger?.Info($"[FallbackSearch] {source.Id} 快照缓存命中 {number}");
                return cached;
            }
        }

        VideoSourceFetchResult result;
        try
        {
            result = await source.FetchAsync(
                new VideoSourceRequest(number, kind, null, null, new VideoSourceFetchOptions()), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.Warn($"[FallbackSearch] {source.Id} 请求异常: {ex.Message}");
            return null;
        }

        if (result.Outcome != VideoSourceOutcome.Success || result.Metadata is null)
        {
            _logger?.Info($"[FallbackSearch] {source.Id}: {result.Outcome}");
            return null;
        }

        if (_cache is not null)
        {
            await _cache.SetAsync(new VideoSourceSnapshot
            {
                Number = number,
                SourceId = source.Id,
                Language = result.Language,
                SchemaVersion = "v1",
                Metadata = result.Metadata,
            });
        }
        return result.Metadata;
    }

    private OnlineVideoFallbackHit BuildHit(IVideoScrapeSource source, string number, VideoScrapeMetadata meta)
    {
        // 离线词典后处理：标题/简介繁转简、演员译名、标签归一化
        OfflineLexicon.ProcessMetadata(meta);
        var summary = new OnlineVideoSummary
        {
            SourceId = source.Id,
            Id = MakeSummaryId(source.Id, number),
            Title = string.IsNullOrEmpty(meta.Title) ? number : meta.Title,
            CoverUrl = meta.CoverUrl,
            Number = number,
            // 角标标明结果来自哪个兜底源
            KindLabel = source.DisplayName,
            Tags = meta.Actors.Where(a => !string.IsNullOrWhiteSpace(a)).ToList(),
        };

        var detail = new OnlineVideoDetail
        {
            SourceId = source.Id,
            Number = number,
            Title = summary.Title,
            OriginalTitle = meta.OriginalTitle,
            CoverUrl = meta.CoverUrl,
            ReleaseDateText = meta.ReleaseDate?.ToString("yyyy-MM-dd") ?? "",
            DurationText = meta.RuntimeMinutes > 0 ? $"{meta.RuntimeMinutes} 分钟" : "",
            RatingText = meta.Score > 0 ? meta.Score.ToString("0.0") : "",
            Actors = meta.Actors,
            Tags = meta.Tags,
            Genres = meta.Tags,
            Maker = meta.Publisher,
            Label = meta.Studio,
            Director = meta.Director,
            Description = meta.Description,
            // 真实来源页地址：详情侧栏"打开详情页"直达兜底源站
            VideoUrl = meta.SourceUrls.TryGetValue(source.Id, out var url) ? url : "",
            Referer = meta.SourceUrls.TryGetValue(source.Id, out var referer) ? referer : "",
        };
        return new OnlineVideoFallbackHit(summary, detail);
    }
}
