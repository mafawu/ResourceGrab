using ResourceGrab.App.Themes;

namespace ResourceGrab.App.Shell.Providers;

/// <summary>视频：发现（在线搜索/推荐）+ 我的（视频库/刮削任务）。</summary>
public sealed class VideoNavSectionProvider : INavSectionProvider
{
    public MediaKind Kind => MediaKind.Video;

    public IReadOnlyList<NavSection> GetSections() =>
    [
        new("发现",
        [
            new NavItem("video.online", "在线搜索", Icons.Search),
            new NavItem("video.recommend", "推荐", Icons.Rank, IsEnabled: false),
        ]),
        new("我的",
        [
            new NavItem("video.local", "本地", Icons.Local),
            new NavItem("video.tasks", "刮削任务", Icons.Setting, IsEnabled: false),
        ]),
    ];
}

