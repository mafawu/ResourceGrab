using ResourceGrab.App.Filtering;

namespace ResourceGrab.App.Filtering.Providers;

/// <summary>漫画本地库筛选提供者。</summary>
public sealed class MangaLocalFilterProvider
{
    public IReadOnlyList<FilterSection> BuildSections(
        Dictionary<string, int> tagCounts,
        Dictionary<string, int> authorCounts,
        HashSet<string> includedTags, HashSet<string> excludedTags,
        HashSet<string> includedAuthors, HashSet<string> excludedAuthors)
    {
        var tagOptions = tagCounts.Select(kv => new FilterOption(
            kv.Key, kv.Key, kv.Value,
            includedTags.Contains(kv.Key) ? FilterTriState.Included :
            excludedTags.Contains(kv.Key) ? FilterTriState.Excluded : FilterTriState.None)).ToList();

        var authorOptions = authorCounts.Select(kv => new FilterOption(
            kv.Key, kv.Key, kv.Value,
            includedAuthors.Contains(kv.Key) ? FilterTriState.Included :
            excludedAuthors.Contains(kv.Key) ? FilterTriState.Excluded : FilterTriState.None)).ToList();

        return
        [
            new("tags", "标签", FilterSectionLayout.Wrap, tagOptions),
            new("authors", "作者", FilterSectionLayout.Wrap, authorOptions),
        ];
    }
}
