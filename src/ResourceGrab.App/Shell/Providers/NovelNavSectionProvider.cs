using ResourceGrab.App.Themes;

namespace ResourceGrab.App.Shell.Providers;

/// <summary>
/// 小说：发现（在线书库/分类/排行）+ 我的（书架/本地索引/阅读历史）+ 工具（阅读设置）。
/// 在线书库被关掉时，「发现」整组不出现——设置里说了不显示就要真的不显示。
/// </summary>
public sealed class NovelNavSectionProvider : INavSectionProvider
{
    private readonly bool _onlineEnabled;

    public NovelNavSectionProvider(bool onlineEnabled = true)
    {
        _onlineEnabled = onlineEnabled;
    }

    public MediaKind Kind => MediaKind.Novel;

    public IReadOnlyList<NavSection> GetSections()
    {
        var sections = new List<NavSection>();

        if (_onlineEnabled)
        {
            sections.Add(new NavSection("发现",
            [
                new NavItem("novel.online", "在线书库", Icons.Search),
                new NavItem("novel.category", "分类", Icons.Category),
                new NavItem("novel.rank", "排行", Icons.Rank),
            ]));
        }

        sections.Add(new NavSection("我的",
        [
            new NavItem("novel.shelf", "我的书架", Icons.Local),
            new NavItem("novel.index", "本地索引", Icons.Library),
            new NavItem("novel.history", "阅读历史", Icons.History),
        ]));

        sections.Add(new NavSection("工具",
        [
            new NavItem("novel.settings", "阅读设置", Icons.Setting),
        ]));

        return sections;
    }
}
