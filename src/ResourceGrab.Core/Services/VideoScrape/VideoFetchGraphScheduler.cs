using System.Collections.Concurrent;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M7: 抓取节点定义
// ---------------------------------------------------------------------------

public sealed class VideoFetchNode
{
    public required string SourceId { get; init; }
    public string? Language { get; init; }

    /// <summary>
    /// 源性能分级（越小越先抓）：1=快且稳（missav/javbus/javdb），2=中等，3=慢或受限。
    /// 调度器按 Tier 分波执行，命中即停；不影响节点相等性（仍按 SourceId+Language）。
    /// </summary>
    public int Tier { get; init; } = 2;

    public override bool Equals(object? obj) =>
        obj is VideoFetchNode other &&
        string.Equals(SourceId, other.SourceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Language, other.Language, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(SourceId.ToLowerInvariant(), Language?.ToLowerInvariant());

    public override string ToString() =>
        string.IsNullOrEmpty(Language) ? SourceId : $"{SourceId}/{Language}";
}

// ---------------------------------------------------------------------------
// M7: 抓取图构建器 — 从字段优先级和路由生成节点链
// ---------------------------------------------------------------------------

public static class VideoFetchGraphBuilder
{
    /// <summary>
    /// 构建字段到节点链的映射。
    /// 每个字段对应一条来源优先链。
    /// </summary>
    public static Dictionary<string, List<VideoFetchNode>> Build(
        VideoScrapeAdvancedSettings config,
        VideoContentKind contentKind,
        IReadOnlyList<VideoFetchNode> availableNodes)
    {
        var availableSet = new HashSet<VideoFetchNode>(availableNodes);
        var fieldChains = new Dictionary<string, List<VideoFetchNode>>(StringComparer.OrdinalIgnoreCase);

        // 收集所有字段优先级配置
        foreach (var fp in config.FieldPriorities)
        {
            var priority = config.GetFieldPriority(fp.Field, contentKind);
            var chain = new List<VideoFetchNode>();

            foreach (var srcId in priority)
            {
                // 尝试匹配默认语言节点
                var node = availableNodes.FirstOrDefault(n =>
                    string.Equals(n.SourceId, srcId, StringComparison.OrdinalIgnoreCase) &&
                    n.Language is null);

                if (node is not null && !chain.Contains(node))
                    chain.Add(node);

                // 检查多语言节点
                var langNodes = availableNodes.Where(n =>
                    string.Equals(n.SourceId, srcId, StringComparison.OrdinalIgnoreCase) &&
                    n.Language is not null);

                foreach (var langNode in langNodes)
                {
                    if (!chain.Contains(langNode))
                        chain.Add(langNode);
                }
            }

            // 补充不在优先链中的节点（收集型字段需要）
            foreach (var node in availableNodes)
            {
                if (!chain.Contains(node))
                    chain.Add(node);
            }

            if (chain.Count > 0)
                fieldChains[fp.Field] = chain;
        }

        return fieldChains;
    }
}

// ---------------------------------------------------------------------------
// M7: 波次调度器 — 按字段链逐步执行，支持剪枝和部分结果注入
// ---------------------------------------------------------------------------

public sealed class VideoFetchGraphScheduler
{
    private readonly IVideoScrapeSourceRegistry _registry;
    private readonly IVideoSourceSnapshotCache? _cache;
    private readonly ILogger? _logger;

    public VideoFetchGraphScheduler(
        IVideoScrapeSourceRegistry registry,
        IVideoSourceSnapshotCache? cache = null,
        ILogger? logger = null)
    {
        _registry = registry;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// 执行抓取图调度：缓存注入 + 按 Tier 分波执行。
    /// 波次语义：先抓 Tier 最小的首批（2-3 个高性能源，missav 必在），
    /// 任一源命中即停；全部未命中/失败才继续下一波。避免全源并发浪费配额。
    /// </summary>
    public async Task<IReadOnlyList<VideoSourceFetchResult>> ExecuteAsync(
        VideoScrapeRequest request,
        VideoScrapeCacheMode cacheMode,
        CancellationToken ct)
    {
        var results = new List<VideoSourceFetchResult>();
        var nodesToFetch = new List<VideoFetchNode>();
        var cachedResults = new List<VideoSourceFetchResult>();

        foreach (var node in request.AvailableNodes)
        {
            if (cacheMode == VideoScrapeCacheMode.ForceRefreshAll)
            {
                nodesToFetch.Add(node);
                continue;
            }

            // 尝试读缓存
            if (_cache is not null && cacheMode is not VideoScrapeCacheMode.ForceRefreshAll)
            {
                var snapshot = await _cache.GetAsync(request.Number, node.SourceId, node.Language);
                if (snapshot is not null)
                {
                    if (cacheMode == VideoScrapeCacheMode.CacheOnly)
                    {
                        cachedResults.Add(VideoSourceFetchResult.Success(
                            node.SourceId, snapshot.Metadata, language: node.Language));
                        continue;
                    }

                    // Auto / RefreshMissing / BypassExpired: 缓存命中
                    cachedResults.Add(VideoSourceFetchResult.Success(
                        node.SourceId, snapshot.Metadata, language: node.Language));
                    _logger?.Info($"[FetchGraph] 缓存命中: {node}");
                    continue;
                }
            }

            nodesToFetch.Add(node);
        }

        _logger?.Info($"[FetchGraph] 需要联网: {nodesToFetch.Count}, 缓存命中: {cachedResults.Count}");
        results.AddRange(cachedResults);

        // 缓存已命中有效数据则不再联网；否则按 Tier 分波执行
        var found = cachedResults.Count > 0;
        foreach (var wave in nodesToFetch.GroupBy(n => n.Tier).OrderBy(g => g.Key))
        {
            if (found) break;

            var waveNodes = wave.ToList();
            _logger?.Info($"[FetchGraph] 波次 Tier={wave.Key}: {string.Join("+", waveNodes.Select(n => n.SourceId))}");

            var fetchedResults = await Task.WhenAll(waveNodes.Select(node => FetchNodeAsync(node, request, cacheMode, ct)));
            results.AddRange(fetchedResults);
            found = fetchedResults.Any(r => r.Outcome == VideoSourceOutcome.Success);
        }

        return results;
    }

    /// <summary>抓取单个节点：解析源、构造请求、联网、成功后写缓存。</summary>
    private async Task<VideoSourceFetchResult> FetchNodeAsync(
        VideoFetchNode node, VideoScrapeRequest request, VideoScrapeCacheMode cacheMode, CancellationToken ct)
    {
        var source = _registry.Get(node.SourceId);
        if (source is null)
        {
            _logger?.Warn($"[FetchGraph] 来源未注册: {node.SourceId}");
            return VideoSourceFetchResult.Failure(
                node.SourceId, VideoSourceOutcome.Disabled, "Source not registered");
        }

        var sourceRequest = new VideoSourceRequest(
            request.Number,
            request.ContentKind,
            node.Language,
            request.FilePath,
            new VideoSourceFetchOptions { ForceRefresh = cacheMode == VideoScrapeCacheMode.ForceRefreshAll });

        var result = await source.FetchAsync(sourceRequest, ct);
        _logger?.Info($"[FetchGraph] {node}: {result.Outcome} ({result.ElapsedMs}ms)");

        // 写入缓存
        if (result.Outcome == VideoSourceOutcome.Success && result.Metadata is not null && _cache is not null)
        {
            var snapshot = new VideoSourceSnapshot
            {
                Number = request.Number,
                SourceId = node.SourceId,
                Language = node.Language,
                SchemaVersion = "v1",
                Metadata = result.Metadata
            };
            await _cache.SetAsync(snapshot);
        }

        return result;
    }
}

// ---------------------------------------------------------------------------
// M7: 刮削请求
// ---------------------------------------------------------------------------

public sealed class VideoScrapeRequest
{
    public required string Number { get; init; }
    public required VideoContentKind ContentKind { get; init; }
    public string? FilePath { get; init; }
    public required IReadOnlyList<VideoFetchNode> AvailableNodes { get; init; }
}
