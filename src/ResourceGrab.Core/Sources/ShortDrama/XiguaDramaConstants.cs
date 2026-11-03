namespace ResourceGrab.Core.Sources.ShortDrama;

/// <summary>
/// 西瓜短剧站点常量：URL 模板、选择器、节流参数。
/// <para>
/// 站点结构（2026-11 实测）：服务端渲染 HTML，列表/详情/播放帧都由 HTML 直出；
/// 域名是"随机子域 + 官方发布页"形态，故基址必须可配置。
/// </para>
/// </summary>
public static class XiguaDramaConstants
{
    /// <summary>默认基址：官方发布页里的主站，实测最稳。</summary>
    public const string DefaultBaseUrl = "https://xiguaai.net";

    /// <summary>
    /// 备用线路（用户直连不通时按序尝试）。全部来自 /go-home/ 官方发布页；
    /// 其中 *.glyxnzmv.cc 是轮换子域，失效属正常，抓到 /go-home/ 后会自动刷新。
    /// </summary>
    public static readonly string[] FallbackHosts =
    [
        "https://xiguaai.net",
        "https://xiguadj.co",
        "https://xiguaduanju8.pages.dev",
    ];

    /// <summary>官方线路发布页（域名自发现的唯一入口）。</summary>
    public const string GoHomePath = "/go-home/";

    /// <summary>标签索引页。</summary>
    public const string TagsPath = "/tags/";

    /// <summary>
    /// 请求 UA：站点前置 Cloudflare 且按 UA 做白名单，非浏览器 UA 直接 403。
    /// 必须与播放/下载链路共用同一个常量（签名 token 可能与 UA 绑定）。
    /// </summary>
    public const string UserAgent = VideoConstants.UserAgent;

    /// <summary>请求间隔（毫秒）：自动翻页/预取时避免把站点打疼。</summary>
    public const int RequestIntervalMs = 1000;

    /// <summary>单次请求超时（秒）。</summary>
    public const int TimeoutSeconds = 30;

    // ---- URL 模板 -------------------------------------------------------

    /// <summary>频道 slug → 展示名。顺序即 UI 里的展示顺序。</summary>
    public static readonly IReadOnlyList<(string Slug, string Name)> Channels =
    [
        ("ai-duanju", "成人短剧"),
        ("ai-manju", "成人漫剧"),
        ("ai-mogai", "AI 魔改"),
        ("ai-xiguanyuanchuan", "西瓜原创"),
    ];

    /// <summary>
    /// 列表路径。⚠️ 频道的分页是 <c>/{slug}/{page}/</c>，
    /// 标签的分页却是 <c>/tag/{slug}/page/{page}/</c>——两者不一样，不能共用模板。
    /// </summary>
    public static string FeedPath(DramaFeedKind kind, int page, string? scope)
    {
        page = Math.Max(1, page);
        var paged = page <= 1 ? "" : page.ToString();
        return kind switch
        {
            DramaFeedKind.Recommend => page <= 1 ? "/recommend" : $"/recommend/{paged}/",
            DramaFeedKind.Newest => page <= 1 ? "/newest" : $"/newest/{paged}/",
            DramaFeedKind.Channel when !string.IsNullOrWhiteSpace(scope) =>
                page <= 1 ? $"/{scope.Trim('/')}/" : $"/{scope.Trim('/')}/{paged}/",
            DramaFeedKind.Tag when !string.IsNullOrWhiteSpace(scope) =>
                page <= 1 ? $"/tag/{scope.Trim('/')}/" : $"/tag/{scope.Trim('/')}/page/{paged}/",
            _ => "/newest",
        };
    }

    /// <summary>搜索路径：<c>/search/{kw}/</c>、<c>/search/{kw}/{page}/</c>（<c>?keyword=</c> 会 302 到路径形式）。</summary>
    public static string SearchPath(string keyword, int page)
    {
        var kw = Uri.EscapeDataString((keyword ?? "").Trim());
        return page <= 1 ? $"/search/{kw}/" : $"/search/{kw}/{page}/";
    }

    /// <summary>详情路径。</summary>
    public static string DetailPath(string dramaId) => $"/detail/{dramaId.Trim('/')}/";

    /// <summary>
    /// 播放帧路径：**唯一可靠的取流入口**。
    /// 真正的 <c>/video/{id}/</c> 页面由 JS 挂载播放器，HTML 里没有 m3u8。
    /// </summary>
    public static string PlayFramePath(string dramaId, int episode)
        => $"/video/{dramaId.Trim('/')}/play-frame/?ep={Math.Max(1, episode)}";

    /// <summary>榜单路径。</summary>
    public static string RankPath(DramaRankKind kind) => kind switch
    {
        DramaRankKind.Hot => "/ranks/hot/",
        DramaRankKind.Recommend => "/ranks/recommend/",
        DramaRankKind.Potential => "/ranks/potential/",
        _ => "/ranks/hot/",
    };

    // ---- 选择器 ---------------------------------------------------------
    //
    // 说明：频道页会把「当前热播 / 高分推荐 / 最多收藏」三个排序面板以 <template> 内嵌，
    // 导致同一批剧集在 HTML 里出现 3~4 次（实测 80 张卡 / 60 个唯一 ID）。
    // 因此卡片一律：优先取 .hg-card-grid.is-active 作用域，否则全页按 ID 去重。

    public const string ActiveGridSelector = "div.hg-card-grid.is-active";
    public const string CardSelector = "div.hg-drama-card[data-track-id]";
    public const string CardTitleSelector = "h3.hg-drama-card__title a";
    public const string CardCoverSelector = "div.hg-drama-card__cover img";
    public const string CardScoreSelector = "span.hg-drama-card__score";
    public const string CardEpisodeSelector = "span.hg-drama-card__episode";
    public const string CardBadgeSelector = "span.hg-drama-card__badge";
    public const string CardDescSelector = "p.hg-drama-card__desc";
    public const string CardTagSelector = "div.hg-drama-card__tags a.hg-tag";

    public const string DetailRootSelector = "section.hg-web-detail";
    public const string DetailTitleSelector = "section.hg-web-detail h1";
    public const string DetailPosterSelector = "div.hg-web-detail__poster img";
    public const string DetailScoreSelector = "span.hg-web-detail__score em";
    public const string DetailFactsSelector = "span.hg-web-detail__facts";
    public const string DetailDescSelector = "p.hg-web-detail__desc";
    public const string DetailTagsSelector = "div.hg-web-detail__tags a.hg-tag";

    /// <summary>选集摘要文本（"共 6 集 · 已更新 6 集"）。</summary>
    public const string DetailEpHeadTextSelector = "div.hg-web-detail__ep-head p";

    public const string DetailEpisodeLinksSelector = "div[data-ep-grid] a";
    public const string DetailRelatedSelector = "div.hg-web-detail__related div.hg-drama-card[data-track-id]";

    public const string PlayRootSelector = "section.hg-play";
    public const string PlaySlideSelector = "article[data-play-slide]";

    public const string RankRowSelector = "div.hg-rank-item[data-track-id]";
    /// <summary>
    /// 标签链接。⚠️ 标签索引页（/tags/）用的是 <c>a.hg-tagindex-chip</c> 而不是卡片上的 <c>a.hg-tag</c>，
    /// 所以这里只能按 href 前缀选，不能按 class 选。
    /// </summary>
    public const string TagLinkSelector = "a[href^='/tag/']";
}
