namespace ResourceGrab.Core.Sources;

/// <summary>
/// 漫画内容源统一接口：站点差异（API 协议、加密、防盗链 headers、图片分块）全部收敛到实现内。
/// 下载管线与 UI 只依赖此接口。
/// </summary>
public interface IComicSource
{
    ComicSourceInfo Info { get; }

    /// <summary>按关键词搜索漫画（page 从 1 开始）。</summary>
    Task<SearchResult> SearchAsync(string keyword, int page, CancellationToken ct = default);

    /// <summary>获取漫画详情（含章节列表）。</summary>
    Task<ComicDetail> GetComicAsync(string comicId, CancellationToken ct = default);

    /// <summary>获取某章节的全部图片页（顺序即下载命名顺序）。</summary>
    Task<IReadOnlyList<ImagePage>> GetChapterImagesAsync(Chapter chapter, CancellationToken ct = default);
}
