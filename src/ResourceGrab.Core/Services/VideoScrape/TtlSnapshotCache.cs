using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// 阶段1: 快照缓存 TTL 装饰器。
// DiskJsonSnapshotCache 本身不做时效判断；老 VideoScrapeService 在调用侧用 7 天 TTL
// 判断新旧。图引擎的 VideoFetchGraphScheduler 直接调 GetAsync，因此在此统一套上 TTL，
// 过期快照按未命中处理，语义与老管道一致。
// ---------------------------------------------------------------------------

public sealed class TtlSnapshotCache : IVideoSourceSnapshotCache
{
    private readonly IVideoSourceSnapshotCache _inner;
    private readonly TimeSpan _ttl;
    private readonly ILogger? _logger;

    public TtlSnapshotCache(IVideoSourceSnapshotCache inner, TimeSpan ttl, ILogger? logger = null)
    {
        _inner = inner;
        _ttl = ttl;
        _logger = logger;
    }

    public async ValueTask<VideoSourceSnapshot?> GetAsync(string number, string sourceId, string? language = null)
    {
        var snapshot = await _inner.GetAsync(number, sourceId, language);
        if (snapshot is null) return null;

        if (DateTimeOffset.UtcNow - snapshot.FetchedAt > _ttl)
        {
            _logger?.Info($"[Cache] {number}/{sourceId} 快照已过期（TTL {_ttl.TotalDays:0} 天），重新联网");
            return null;
        }
        return snapshot;
    }

    public ValueTask SetAsync(VideoSourceSnapshot snapshot) => _inner.SetAsync(snapshot);

    public ValueTask<bool> HasAsync(string number, string sourceId, string? language = null)
        => _inner.HasAsync(number, sourceId, language);

    public ValueTask ClearAsync(string number) => _inner.ClearAsync(number);

    public ValueTask ClearAllAsync() => _inner.ClearAllAsync();

    public ValueTask<IReadOnlyList<string>> GetCachedSourceIdsAsync(string number)
        => _inner.GetCachedSourceIdsAsync(number);
}
