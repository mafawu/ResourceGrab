namespace ResourceGrab.Core.Sources.Novel;

/// <summary>站内搜索范围，对应各站点的搜索类型参数。</summary>
public enum NovelSearchField
{
    All,
    Title,
    Author,
    Tag,
}

/// <summary>搜索结果中的小说摘要。</summary>
public sealed class NovelSummary
{
    /// <summary>站点侧书籍 id（如 alicesw 的 35647）。</summary>
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    public string Author { get; init; } = "";

    public string CoverUrl { get; init; } = "";

    /// <summary>连载状态原文（"连载中" / "已完结" / 空）。</summary>
    public string Status { get; init; } = "";

    public string Category { get; init; } = "";

    /// <summary>字数原文，如 "42.8 万字"。解析不出时为空。</summary>
    public string WordCountText { get; init; } = "";

    /// <summary>章节数原文，如 "128 章"；列表页通常拿不到。</summary>
    public string ChapterCountText { get; init; } = "";

    /// <summary>列表页摘要（可能为空）。</summary>
    public string Intro { get; init; } = "";

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>更新时间原文，如 "11-04 10:20"。</summary>
    public string UpdatedText { get; init; } = "";

    /// <summary>最新一章标题（列表页给到的"最新章节"列）。</summary>
    public string LatestChapterTitle { get; init; } = "";

    /// <summary>人气值原文（排行 / 分类列表的 hot 列）；搜索页没有。</summary>
    public string HitsText { get; init; } = "";
}

/// <summary>站点分类（如「玄幻」对应 /lists/62.html）。</summary>
public sealed class NovelCategory
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string Url { get; init; } = "";
}

/// <summary>排行周期。</summary>
public enum NovelRankPeriod
{
    Day,
    Week,
    Month,
    All,
}

/// <summary>分类列表的排序方式，对应站点 order 参数。</summary>
public enum NovelListSort
{
    Update,
    WordCount,
    Hits,
}

/// <summary>小说章节。站点把"书 id"和"目录 id"分开，两个都要留。</summary>
public sealed class NovelChapter
{
    /// <summary>章节在书内的顺序（0 起，按站点目录顺序）。</summary>
    public int Index { get; init; }

    public string Title { get; init; } = "";

    /// <summary>章节所属的站点书籍（目录）id，与 <see cref="NovelSummary.Id"/> 可能不同。</summary>
    public string BookId { get; init; } = "";

    /// <summary>章节 key（站点 URL 里的短哈希）。</summary>
    public string Key { get; init; } = "";

    public string Url { get; init; } = "";

    public string UpdatedText { get; init; } = "";

    /// <summary>用于本地缓存与阅读进度键：{BookId}/{Key}。</summary>
    public string Id => BookId + "/" + Key;
}

/// <summary>书籍详情：书页能拿到的全部信息 + 完整目录。</summary>
public sealed class NovelDetail
{
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    public string Author { get; init; } = "";

    public string CoverUrl { get; init; } = "";

    public string Intro { get; init; } = "";

    public string Category { get; init; } = "";

    public string Status { get; init; } = "";

    public string WordCountText { get; init; } = "";

    public int ChapterCount { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<NovelChapter> Chapters { get; init; } = Array.Empty<NovelChapter>();

    /// <summary>最新章节标题（书页"最 新："那一行）。</summary>
    public string LatestChapterTitle { get; init; } = "";

    public string UpdatedText { get; init; } = "";
}

/// <summary>一页搜索结果。</summary>
public sealed class NovelSearchResult
{
    public IReadOnlyList<NovelSummary> Items { get; init; } = Array.Empty<NovelSummary>();

    /// <summary>当前页码（1 起）。</summary>
    public int Page { get; init; } = 1;

    /// <summary>总页数；站点未给出时取当前页。</summary>
    public int TotalPages { get; init; } = 1;
}
