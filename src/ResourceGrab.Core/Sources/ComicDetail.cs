namespace ResourceGrab.Core.Sources;

/// <summary>漫画详情（含章节列表），与具体站点解耦的通用模型。</summary>
public class ComicDetail
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public string Description { get; init; } = "";
    public List<string> Authors { get; init; } = new();
    public List<string> Tags { get; init; } = new();
    public List<Chapter> Chapters { get; init; } = new();
}
