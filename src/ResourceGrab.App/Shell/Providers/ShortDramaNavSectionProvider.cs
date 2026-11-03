using ResourceGrab.App.Themes;

namespace ResourceGrab.App.Shell.Providers;

/// <summary>
/// 短剧导航：发现（推荐/上新/榜单）+ 频道（四个内容频道）+ 我的（本地/追剧/观看历史）。
/// 与其它媒体不同，短剧是单源，所以左栏第一块不是「源切换」而是「线路状态卡」（由视图自己渲染）。
/// </summary>
public sealed class ShortDramaNavSectionProvider : INavSectionProvider
{
    public MediaKind Kind => MediaKind.ShortDrama;

    // 路由 Id 与频道 slug 的对应关系集中在这里，MainWindow 注册路由时复用同一份定义，
    // 避免"导航项 Id 写错 → 点了跳回漫画搜索页"这种静默失败。
    public const string RecommendRouteId = "drama.recommend";
    public const string NewestRouteId = "drama.newest";
    public const string RankRouteId = "drama.rank";
    public const string LocalRouteId = "drama.local";
    public const string FavoriteRouteId = "drama.favorite";
    public const string HistoryRouteId = "drama.history";

    /// <summary>频道路由 Id 前缀：<c>drama.channel.{slug}</c>。</summary>
    public const string ChannelRoutePrefix = "drama.channel.";

    public static string ChannelRouteId(string slug) => ChannelRoutePrefix + slug;

    public static readonly IReadOnlyList<(string Slug, string Name)> Channels =
    [
        ("ai-duanju", "成人短剧"),
        ("ai-manju", "成人漫剧"),
        ("ai-mogai", "AI 魔改"),
        ("ai-xiguanyuanchuan", "西瓜原创"),
    ];

    public IReadOnlyList<NavSection> GetSections() =>
    [
        new("发现",
        [
            new NavItem(RecommendRouteId, "推荐", Icons.Play),
            new NavItem(NewestRouteId, "最近上新", Icons.Weekly),
            new NavItem(RankRouteId, "榜单", Icons.Rank),
        ]),
        new("频道",
            Channels.Select(c => new NavItem(ChannelRouteId(c.Slug), c.Name, Icons.Category)).ToList()),
        new("我的",
        [
            new NavItem(LocalRouteId, "本地", Icons.Library),
        ]),
    ];
}
