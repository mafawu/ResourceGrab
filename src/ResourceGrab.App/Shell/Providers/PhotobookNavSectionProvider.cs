using ResourceGrab.App.Themes;
using ResourceGrab.Core.Sources;

namespace ResourceGrab.App.Shell.Providers;

/// <summary>
/// 按写真源的**实际能力**生成左侧导航。
/// <para>
/// 与 <see cref="MangaNavSectionProvider"/> 的区别：写真不提供「作者合集 / 本地 / 推荐」这些无条件项。
/// 导航项分三类：
/// </para>
/// <list type="number">
/// <item>「写真」列表页 —— 所有写真源都具备，无条件出现，也是写真入口的默认落点；</item>
/// <item>「随机」—— 仅实现 <see cref="IRandomPhotobookSource"/> 的源出现；</item>
/// <item>分类 / 标签 / Coser / 系列 / 角色 / 主题 —— 按 <see cref="ComicSourceInfo.Taxonomies"/> 声明出现。</item>
/// </list>
/// <para>
/// 各写真源能力差异极大（lovecutes 有六个维度、妹子图只有标签），所以这里一律按能力生成、
/// 不摆"置灰的缺失项"：导航里出现什么，就是该源真能做什么。
/// </para>
/// </summary>
public sealed class PhotobookNavSectionProvider : INavSectionProvider
{
    /// <summary>「写真」列表页路由 Id（所有写真源通用）。</summary>
    public const string HomeRouteId = "photobook.home";

    /// <summary>「随机」路由 Id（仅 <see cref="IRandomPhotobookSource"/> 源）。</summary>
    public const string RandomRouteId = "photobook.random";

    private readonly ComicSourceInfo _info;
    private readonly bool _hasRandom;

    /// <param name="source">
    /// 当前写真源。尚未接入任何写真源时传入的是别类源（Kind 非 Photobook），
    /// 此时只保留「写真」入口，页面自会提示"未接入"，避免左栏整片空白。
    /// </param>
    public PhotobookNavSectionProvider(IComicSource source)
    {
        _info = source.Info;
        _hasRandom = source is IRandomPhotobookSource;
    }

    public MediaKind Kind => MediaKind.Photobook;

    /// <summary>维度展示元数据。数组顺序即导航顺序。</summary>
    private static readonly (TaxonomyKind Kind, string Title, object? Icon)[] Meta =
    [
        (TaxonomyKind.Category, "分类", Icons.Category),
        (TaxonomyKind.Tag, "标签", Icons.Tag),
        (TaxonomyKind.Coser, "Coser", Icons.User),
        (TaxonomyKind.Franchise, "系列", Icons.Library),
        (TaxonomyKind.Character, "角色", Icons.Character),
        (TaxonomyKind.Topic, "主题", Icons.Document),
    ];

    /// <summary>维度 → 路由 Id。路由 Id 与导航项 Id 复用同一字符串，避免两处漂移。</summary>
    public static string RouteId(TaxonomyKind kind) => $"photobook.{kind.ToString().ToLowerInvariant()}";

    /// <summary>写真入口的默认落点：「写真」列表页（任何写真源都可用）。</summary>
    public static string DefaultRouteId => HomeRouteId;

    public IReadOnlyList<NavSection> GetSections()
    {
        var items = new List<NavItem>
        {
            new(HomeRouteId, "写真", Icons.Photobook),
        };

        // 尚无写真源接入时不摆维度项（一个都点不通）
        if (_info.Kind == ResourceKind.Photobook)
        {
            if (_hasRandom)
            {
                items.Add(new NavItem(RandomRouteId, "随机", Icons.Shuffle));
            }

            items.AddRange(Meta
                .Where(m => _info.Taxonomies.Contains(m.Kind))
                .Select(m => new NavItem(RouteId(m.Kind), m.Title, m.Icon)));
        }

        return [new NavSection("浏览", items)];
    }
}
