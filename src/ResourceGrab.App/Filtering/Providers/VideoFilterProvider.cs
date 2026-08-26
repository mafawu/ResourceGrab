using ResourceGrab.App.Filtering;

namespace ResourceGrab.App.Filtering.Providers;

/// <summary>视频筛选提供者。</summary>
public sealed class VideoFilterProvider
{
    public IReadOnlyList<FilterSection> BuildSections(
        Dictionary<string, int> tagCounts,
        Dictionary<string, int> seriesCounts,
        HashSet<string> includedTags, HashSet<string> excludedTags)
    {
        var tagOptions = tagCounts.Select(kv => new FilterOption(
            kv.Key, kv.Key, kv.Value,
            includedTags.Contains(kv.Key) ? FilterTriState.Included :
            excludedTags.Contains(kv.Key) ? FilterTriState.Excluded : FilterTriState.None)).ToList();

        var seriesOptions = seriesCounts.Select(kv => new FilterOption(
            kv.Key, kv.Key, kv.Value,
            includedTags.Contains("series:" + kv.Key) ? FilterTriState.Included : FilterTriState.None)).ToList();

        return
        [
            new("tags", "标签", FilterSectionLayout.Wrap, tagOptions),
            new("series", "系列", FilterSectionLayout.Wrap, seriesOptions),
        ];
    }
}
