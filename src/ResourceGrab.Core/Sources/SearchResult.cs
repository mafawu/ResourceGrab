namespace ResourceGrab.Core.Sources;

/// <summary>搜索结果：普通列表，或命中唯一漫画时的直接命中。</summary>
public class SearchResult
{
    public IReadOnlyList<ComicSummary> Items { get; init; } = Array.Empty<ComicSummary>();

    /// <summary>站点报告的总命中数。</summary>
    public long Total { get; init; }

    /// <summary>总页数（用于分页；无法精确获知时取 1）。</summary>
    public long TotalPages { get; init; } = 1;

    /// <summary>搜索直接命中唯一漫画时，可直接用该 id 调用 GetComicAsync。</summary>
    public string? SingleComicId { get; init; }

    public bool IsSingleMatch => SingleComicId is not null;
}
