namespace ResourceGrab.Core.Sources;

/// <summary>分类节点：各站点分类体系不同，统一为 id + 名称 + 可选子分类。</summary>
public class ComicCategory
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public IReadOnlyList<ComicCategory> Children { get; init; } = Array.Empty<ComicCategory>();
}
