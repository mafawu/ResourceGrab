using ResourceGrab.App.Themes;

namespace ResourceGrab.App.Shell.Providers;

/// <summary>视频：发现（在线搜索/推荐）+ 我的（视频库/刮削任务/演员）。</summary>
public sealed class VideoNavSectionProvider : INavSectionProvider
{
    public MediaKind Kind => MediaKind.Video;

    public IReadOnlyList<NavSection> GetSections() =>
    [
        new("发现",
        [
            new NavItem("video.online", "在线搜索", Icons.Search),
            // 在线推荐：MissAV 榜单（今日/本周/本月热门、新作上市）
            new NavItem("video.online-recommend", "在线推荐", Icons.Rank),
        ]),
        new("我的",
        [
            new NavItem("video.local", "本地", Icons.Local),
            // 本地推荐：基于本地库的推荐页，随本地入口分组
            new NavItem("video.recommend", "推荐", Icons.Library),
            new NavItem("video.tasks", "刮削任务", Icons.TaskProgress),
            new NavItem("video.actors", "演员", Icons.User),
        ]),
    ];
}