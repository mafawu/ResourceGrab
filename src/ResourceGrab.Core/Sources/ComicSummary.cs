namespace ResourceGrab.Core.Sources;

/// <summary>搜索 / 列表页中的漫画摘要。</summary>
public class ComicSummary
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public string Author { get; init; } = "";
    public string Category { get; init; } = "";
}
