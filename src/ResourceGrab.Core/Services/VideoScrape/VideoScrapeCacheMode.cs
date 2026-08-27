namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M4: 缓存模式
// ---------------------------------------------------------------------------

public enum VideoScrapeCacheMode
{
    /// <summary>有缓存读缓存，缺失来源联网补抓。</summary>
    Auto,
    /// <summary>忽略缓存，全部重新联网。</summary>
    ForceRefreshAll,
    /// <summary>只抓取路由中缺少缓存的来源。</summary>
    RefreshMissing,
    /// <summary>只读缓存，不发网络请求。</summary>
    CacheOnly,
    /// <summary>过期缓存仍可用于兜底，但后台刷新。</summary>
    BypassExpired
}
