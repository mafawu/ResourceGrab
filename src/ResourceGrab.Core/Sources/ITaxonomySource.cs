namespace ResourceGrab.Core.Sources;

/// <summary>
/// 写真图集的浏览维度。与漫画的单层 <see cref="ICategorySource"/> 区分开：
/// 一个写真源可声明支持其中若干种，导航按声明生成。
/// </summary>
public enum TaxonomyKind
{
    /// <summary>分类（题材大类）。</summary>
    Category,

    /// <summary>标签。</summary>
    Tag,

    /// <summary>Coser / 模特。</summary>
    Coser,

    /// <summary>系列 / 作品。</summary>
    Franchise,

    /// <summary>角色。</summary>
    Character,

    /// <summary>主题 / 合辑。</summary>
    Topic,
}

/// <summary>
/// 写真源的多维浏览扩展点。实现该接口且 <see cref="ComicSourceInfo.Kind"/> 为
/// <see cref="ResourceKind.Photobook"/> 的源，会在「写真」入口下提供维度浏览。
/// </summary>
public interface ITaxonomySource
{
    /// <summary>取指定维度下的条目列表（分类项 / 标签 / Coser 等）。</summary>
    Task<IReadOnlyList<TaxonomyItem>> GetTaxonomyItemsAsync(TaxonomyKind kind, CancellationToken ct = default);

    /// <summary>按维度条目浏览图集（page 从 1 开始）。</summary>
    Task<SearchResult> BrowseByTaxonomyAsync(TaxonomyKind kind, string itemId, int page, CancellationToken ct = default);
}

/// <summary>维度下的一个条目，如某个标签、某位 Coser。</summary>
/// <param name="Id">站点侧标识，用于后续浏览请求。</param>
/// <param name="Name">展示名称。</param>
/// <param name="Count">该条目下的图集数量；站点未提供时为 null。</param>
public sealed record TaxonomyItem(string Id, string Name, int? Count = null);
