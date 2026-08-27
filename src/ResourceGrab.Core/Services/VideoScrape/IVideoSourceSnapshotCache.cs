using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M4: 来源快照缓存接口
// ---------------------------------------------------------------------------

public interface IVideoSourceSnapshotCache
{
    /// <summary>尝试获取指定来源的缓存快照。</summary>
    ValueTask<VideoSourceSnapshot?> GetAsync(string number, string sourceId, string? language = null);

    /// <summary>保存来源快照到缓存。</summary>
    ValueTask SetAsync(VideoSourceSnapshot snapshot);

    /// <summary>检查指定来源是否有有效缓存。</summary>
    ValueTask<bool> HasAsync(string number, string sourceId, string? language = null);

    /// <summary>清除指定番号的所有缓存。</summary>
    ValueTask ClearAsync(string number);

    /// <summary>清除所有缓存。</summary>
    ValueTask ClearAllAsync();

    /// <summary>获取指定番号已缓存的来源 ID 列表。</summary>
    ValueTask<IReadOnlyList<string>> GetCachedSourceIdsAsync(string number);
}
