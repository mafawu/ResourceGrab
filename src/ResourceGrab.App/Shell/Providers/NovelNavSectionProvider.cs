using ResourceGrab.App.Themes;

namespace ResourceGrab.App.Shell.Providers;

/// <summary>小说：我的（本地索引/阅读历史）+ 工具（索引设置）。</summary>
public sealed class NovelNavSectionProvider : INavSectionProvider
{
    public MediaKind Kind => MediaKind.Novel;

    public IReadOnlyList<NavSection> GetSections() =>
    [
        new("我的",
        [
            new NavItem("novel.index", "本地索引", Icons.Local),
            new NavItem("novel.history", "阅读历史", Icons.Rank, IsEnabled: false),
        ]),
        new("工具",
        [
            new NavItem("novel.settings", "索引设置", Icons.Setting, IsEnabled: false),
        ]),
    ];
}
