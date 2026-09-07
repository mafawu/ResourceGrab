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

    /// <summary>是否需要先访问首页预热 Cookie（预留标志，当前无消费方）。</summary>
    public bool RequiresCookieWarmup { get; init; }

    /// <summary>是否需要通过 curl 子进程绕过 Cloudflare（预留标志，当前无消费方）。</summary>
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

    /// <summary>番号，如 "SSIS-960"；解析不出时为空串。</summary>
    public string Number { get; init; } = "";

    /// <summary>内容类型角标文本，如 "無碼影片"/"中文字幕"；无则留空。</summary>
    public string KindLabel { get; init; } = "";

    public List<string> Tags { get; init; } = new();

    /// <summary>展示用时长文本，如 "12:34"。</summary>
    public string DurationText { get; init; } = "";

    /// <summary>展示用评分文本，如 "4.5"；搜索页无评分的源留空，由详情回填。</summary>
    public string RatingText { get; init; } = "";
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

    /// <summary>发行/上传日期文本（yyyy-MM-dd），源未提供时为空。</summary>
    public string ReleaseDateText { get; init; } = "";

    /// <summary>HLS m3u8 或 MP4 直链；null 表示无法获取。</summary>
    public string? StreamUrl { get; init; }

    /// <summary>播放该流需要的 Referer 头。</summary>
    public string Referer { get; init; } = "";

    /// <summary>该影片的磁力链接；null 表示无。详情页有多条磁力时为第一条。</summary>
    public string? MagnetUri { get; init; }

    /// <summary>类型/分类（页面"類型"行），与 Tags 分开维护。</summary>
    public List<string> Genres { get; init; } = new();

    /// <summary>详情页预览图（剧照）URL 列表；源未提供时为空。</summary>
    public List<string> PreviewImages { get; init; } = new();

    /// <summary>发行商（页面"發行商"行，如 S1）。</summary>
    public string Maker { get; init; } = "";

    /// <summary>厂商/厂牌（页面"標籤"行，如 S1 NO.1 STYLE）。</summary>
    public string Label { get; init; } = "";

    /// <summary>导演。</summary>
    public string Director { get; init; } = "";

    /// <summary>评分文本（如 "8.5"）；源未提供时为空。</summary>
    public string RatingText { get; init; } = "";

    /// <summary>详情页磁力列表（名称/体积/日期/链接），站点未提供时为空。</summary>
    public IReadOnlyList<OnlineMagnetLink> Magnets { get; init; } = Array.Empty<OnlineMagnetLink>();

    /// <summary>同系列推荐视频 URL 列表。</summary>
    public List<string> RelatedVideos { get; init; } = new();
}

/// <summary>详情页磁力表格中的一条磁力。</summary>
public class OnlineMagnetLink
{
    public string Name { get; init; } = "";
    public string Size { get; init; } = "";
    public string Date { get; init; } = "";
    public string Url { get; init; } = "";
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

    /// <summary>
    /// 获取站内榜单/推荐列表（如今日热门、本周热门、新作上市），复用搜索摘要模型。
    /// 返回 null 表示该源不支持此榜单（默认实现）；在线推荐页据此决定是否展示该源。
    /// </summary>
    Task<OnlineVideoSearchResult?> GetListingAsync(VideoListingKind kind, int page, CancellationToken ct = default)
        => Task.FromResult<OnlineVideoSearchResult?>(null);

    /// <summary>
    /// 由搜索摘要的 Id 构造详情页 URL。
    /// 默认实现：Id 本身是完整 URL 则直接用，否则返回 null（源未提供拼URL规则）。
    /// </summary>
    string? GetDetailUrl(string videoId)
        => videoId.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? videoId : null;
}

/// <summary>在线榜单/推荐列表种类（对应源站服务端渲染的热门与更新列表页）。</summary>
public enum VideoListingKind
{
    /// <summary>今日热门。</summary>
    TodayHot,

    /// <summary>本周热门。</summary>
    WeeklyHot,

    /// <summary>本月热门。</summary>
    MonthlyHot,

    /// <summary>新作上市。</summary>
    NewRelease,
}
