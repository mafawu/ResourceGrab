namespace ResourceGrab.Core.Sources.Novel;

/// <summary>
/// 小说内容源统一接口：站点差异（搜索参数、目录结构、正文清洗）全部收敛到实现内。
/// 与 <see cref="IComicSource"/> 分开——小说是文本流，没有章节图片列表。
/// </summary>
public interface INovelSource
{
    /// <summary>源元信息；Kind 必须为 <see cref="ResourceKind.Novel"/>。</summary>
    ComicSourceInfo Info { get; }

    /// <summary>按关键词搜索（page 从 1 开始）。</summary>
    Task<NovelSearchResult> SearchAsync(
        string keyword, int page, NovelSearchField field = NovelSearchField.All, CancellationToken ct = default);

    /// <summary>书籍详情（含完整目录）。</summary>
    Task<NovelDetail> GetDetailAsync(string novelId, CancellationToken ct = default);

    /// <summary>只取目录（详情页已缓存时用于刷新章节）。</summary>
    Task<IReadOnlyList<NovelChapter>> GetChaptersAsync(string novelId, CancellationToken ct = default);

    /// <summary>取单章正文，返回已按段落清洗好的纯文本。</summary>
    Task<string> GetChapterTextAsync(NovelChapter chapter, CancellationToken ct = default);

    /// <summary>站点分类列表（分类页的左栏 chips）。</summary>
    Task<IReadOnlyList<NovelCategory>> GetCategoriesAsync(CancellationToken ct = default);

    /// <summary>按分类浏览（page 从 1 开始）。</summary>
    Task<NovelSearchResult> GetCategoryAsync(
        string categoryId, int page, NovelListSort sort = NovelListSort.Update, CancellationToken ct = default);

    /// <summary>榜单（日 / 周 / 月 / 总）。</summary>
    Task<NovelSearchResult> GetRankAsync(NovelRankPeriod period, CancellationToken ct = default);
}
