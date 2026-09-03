using System.Collections.Concurrent;
using ResourceGrab.Core.Sources;

namespace ResourceGrab.Core.Services;

/// <summary>
/// 在线视频详情内存缓存：应用运行生命周期内有效。
/// 搜索卡片回填与详情侧栏共用；同一详情 URL 只请求一次，再次点击直接命中。
/// 无上限——单条详情数据量极小，千级条目仍在几百 KB 量级。
/// </summary>
public sealed class OnlineVideoDetailCache
{
    private readonly ConcurrentDictionary<string, OnlineVideoDetail> _cache = new(StringComparer.Ordinal);

    public bool TryGet(string url, out OnlineVideoDetail detail) => _cache.TryGetValue(url, out detail!);

    public void Set(string url, OnlineVideoDetail detail) => _cache[url] = detail;

    public int Count => _cache.Count;

    public void Clear() => _cache.Clear();
}
