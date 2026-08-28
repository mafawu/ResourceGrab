using ResourceGrab.App.Themes;
using ResourceGrab.Core.Sources;

namespace ResourceGrab.App.Shell.Providers;

/// <summary>按漫画源能力生成左侧导航；不支持的入口直接隐藏。</summary>
public sealed class MangaNavSectionProvider : INavSectionProvider
{
    private readonly ComicSourceInfo _info;

    public MangaNavSectionProvider(ComicSourceInfo info)
    {
        _info = info;
    }

    public MediaKind Kind => MediaKind.Manga;

    public IReadOnlyList<NavSection> GetSections()
    {
        var discovery = new List<NavItem> { new("manga.search", "搜索", Icons.Search) };
        if (_info.SupportsRank)
            discovery.Add(new NavItem("manga.rank", "排行", Icons.Rank));
        if (_info.SupportsCategories)
            discovery.Add(new NavItem("manga.category", "分类", Icons.Category));
        if (_info.SupportsWeekly)
            discovery.Add(new NavItem("manga.weekly", "每周必看", Icons.Weekly));

        var mine = new List<NavItem> { new("manga.local", "本地", Icons.Local) };
        if (_info.SupportsFavorites)
            mine.Add(new NavItem("manga.favorites", "收藏", Icons.FavoriteStar));

        return
        [
            new("发现", discovery),
            new("我的", mine),
            new("工具", new List<NavItem> { new("manga.scrape", "刮削工具", Icons.Setting) }),
        ];
    }
}
