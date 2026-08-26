namespace ResourceGrab.Core.Sources;

public enum ResourceKind { Manga, Novel, Video }

/// <summary>内容源元信息：UI 的站点切换、能力展示等均依赖此信息。</summary>
public class ComicSourceInfo
{
    /// <summary>资源种类（漫画/小说/视频），顶栏 Kind Pills 驱动</summary>
    public ResourceKind Kind { get; init; } = ResourceKind.Manga;

    /// <summary>源唯一标识，如 "jm"、"wnacg"。</summary>
    public string Id { get; init; } = "";

    /// <summary>展示名称，如 "禁漫天堂"。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>该源的核心能力（搜索/详情/下载）是否需要登录。</summary>
    public bool RequiresLogin { get; init; }

    /// <summary>搜索是否支持排序选项（最新/人气等）。</summary>
    public bool SupportsSearchSort { get; init; }

    /// <summary>是否支持分类浏览。</summary>
    public bool SupportsCategories { get; init; }

    /// <summary>是否支持排行。</summary>
    public bool SupportsRank { get; init; }

    /// <summary>是否支持每周必看（禁漫专属首页推荐）。</summary>
    public bool SupportsWeekly { get; init; }

    /// <summary>是否支持收藏（通常需登录）。</summary>
    public bool SupportsFavorites { get; init; }

    /// <summary>加载封面图片时需要附加的请求头（防盗链 Referer 等）。</summary>
    public IReadOnlyDictionary<string, string> CoverHeaders { get; init; } = new Dictionary<string, string>();

    /// <summary>并发下载图片的最大数量（站点限流配置，默认 40）。</summary>
    public int MaxImageConcurrency { get; init; } = 40;

    /// <summary>并发下载章节的最大数量（默认 3）。</summary>
    public int MaxChapterConcurrency { get; init; } = 3;

    /// <summary>并发获取章节图片 URL 的最大数量（默认 10）。</summary>
    public int MaxUrlFetchConcurrency { get; init; } = 10;
}
