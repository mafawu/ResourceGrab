using ResourceGrab.App.Filtering;

namespace ResourceGrab.App.Filtering.Providers;

/// <summary>小说筛选提供者。</summary>
public sealed class NovelFilterProvider
{
    public IReadOnlyList<FilterSection> BuildSections(
        Dictionary<string, int> tagCounts,
        HashSet<string> includedTags, HashSet<string> excludedTags)
    {
        var tagOptions = tagCounts.Select(kv => new FilterOption(
            kv.Key, kv.Key, kv.Value,
            includedTags.Contains(kv.Key) ? FilterTriState.Included :
            excludedTags.Contains(kv.Key) ? FilterTriState.Excluded : FilterTriState.None)).ToList();

        return [new("tags", "标签", FilterSectionLayout.Wrap, tagOptions)];
    }
}
