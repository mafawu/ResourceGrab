namespace ResourceGrab.Core.Sources;

/// <summary>分类浏览能力：实现该接口的源可在"分类"导航中展示。</summary>
public interface ICategorySource
{
    /// <summary>获取分类列表。</summary>
    Task<IReadOnlyList<ComicCategory>> GetCategoriesAsync(CancellationToken ct = default);

    /// <summary>按分类浏览漫画（page 从 1 开始）。</summary>
    Task<SearchResult> GetCategoryComicsAsync(string categoryId, int page, CancellationToken ct = default);
}
