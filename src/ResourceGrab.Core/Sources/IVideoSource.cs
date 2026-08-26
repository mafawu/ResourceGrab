namespace ResourceGrab.Core.Sources;

/// <summary>在线视频源元信息（预留）。</summary>
public class VideoSourceInfo
{
    /// <summary>源唯一标识，如 "example-online"。</summary>
    public string Id { get; init; } = "";

    /// <summary>展示名称。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>搜索是否支持排序选项。</summary>
    public bool SupportsSearchSort { get; init; }

    /// <summary>备用镜像域名列表（Cloudflare 拦截时按顺序切换）。</summary>
    public IReadOnlyList<string> MirrorUrls { get; init; } = Array.Empty<string>();

    /// <summary>是否需要先访问首页预热 Cookie。</summary>
    public bool RequiresCookieWarmup { get; init; }

    /// <summary>是否需要通过 curl 子进程绕过 Cloudflare。</summary>
    public bool RequiresCurlFallback { get; init; }
}

/// <summary>在线视频条目摘要（预留）。</summary>
public class OnlineVideoSummary
{
    public string Id { get; init; } = "";

    /// <summary>所属源 id（OnlineVideoCard 据此路由到源）。</summary>
    public string SourceId { get; init; } = "";

    public string Title { get; init; } = "";

    public string CoverUrl { get; init; } = "";

    public List<string> Tags { get; init; } = new();

    /// <summary>展示用时长文本，如 "12:34"。</summary>
    public string DurationText { get; init; } = "";
}

/// <summary>在线视频搜索结果（预留）。</summary>
public class OnlineVideoSearchResult
{
    public int Page { get; init; } = 1;

    public int? TotalPages { get; init; }

    public IReadOnlyList<OnlineVideoSummary> Items { get; init; } = Array.Empty<OnlineVideoSummary>();
}

/// <summary>在线视频详情。</summary>
public class OnlineVideoDetail
{
    public string SourceId { get; init; } = "";
    public string VideoUrl { get; init; } = "";
    public string Title { get; init; } = "";
    public string OriginalTitle { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public string DurationText { get; init; } = "";
    public List<string> Actors { get; init; } = new();
    public List<string> Tags { get; init; } = new();
    public string Description { get; init; } = "";
    public string Number { get; init; } = "";

    /// <summary>HLS m3u8 或 MP4 直链；null 表示无法获取。</summary>
    public string? StreamUrl { get; init; }

    /// <summary>播放该流需要的 Referer 头。</summary>
    public string Referer { get; init; } = "";

    /// <summary>该影片的磁力链接；null 表示无。</summary>
    public string? MagnetUri { get; init; }

    /// <summary>同系列推荐视频 URL 列表。</summary>
    public List<string> RelatedVideos { get; init; } = new();
}

/// <summary>
/// 在线视频源统一接口（预留扩展点）：未来接入在线站点时实现此接口，
/// 视频页可像漫画源一样增加「在线」标签页并复用卡片网格；
/// 当前视频模块仅面向本地库管理，不注册任何实现。
/// </summary>
public interface IVideoSource
{
    VideoSourceInfo Info { get; }

    /// <summary>按关键词搜索视频（page 从 1 开始）。</summary>
    Task<OnlineVideoSearchResult> SearchAsync(string keyword, int page, CancellationToken ct = default);

    /// <summary>获取视频详情页信息（含流地址与磁力链接）。</summary>
    Task<OnlineVideoDetail?> GetDetailAsync(string videoUrl, CancellationToken ct = default);
}
