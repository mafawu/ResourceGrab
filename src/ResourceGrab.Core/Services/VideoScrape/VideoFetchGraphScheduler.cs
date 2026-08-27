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
    /// 执行完整抓取图调度：波次执行、字段剪枝、缓存注入。
    /// </summary>
    public async Task<IReadOnlyList<VideoSourceFetchResult>> ExecuteAsync(
        VideoScrapeRequest request,
        VideoScrapeCacheMode cacheMode,
        CancellationToken ct)
    {
        var results = new List<VideoSourceFetchResult>();
        var fetchedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 决定哪些来源需要实际请求
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
                    fetchedSources.Add(node.SourceId);
                    _logger?.Info($"[FetchGraph] 缓存命中: {node}");
                    continue;
                }
            }

            nodesToFetch.Add(node);
        }

        _logger?.Info($"[FetchGraph] 需要联网: {nodesToFetch.Count}, 缓存命中: {cachedResults.Count}");

        // 并发执行所有需要联网的节点
        var fetchTasks = nodesToFetch.Select(async node =>
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
        });

        var fetchedResults = await Task.WhenAll(fetchTasks);

        results.AddRange(cachedResults);
        results.AddRange(fetchedResults);

        return results;
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
