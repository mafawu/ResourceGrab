using System.Windows;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Filtering;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;

namespace ResourceGrab.App.Views;

/// <summary>VideoSearchPanel 实现 ISidebarPanel，作为 Filter 模式挂到 SidebarHost。</summary>
public partial class VideoSearchPanel : ISidebarPanel
{
    public string PanelId => "filter.video";
    public string Title => "视频筛选";
    public object? Icon => null;
    public new FrameworkElement Content => this;
}

/// <summary>
/// VideoSearchPanel 的统一筛选契约实现。
/// 选项 Id 约定：tag:{标签}、actor: / series: / studio: / resolution: / duration:{枚举} /
/// status:{枚举}、censor:{枚举}、toggle:favorites、toggle:watched。
/// </summary>
public partial class VideoSearchPanel : IFilterPanel
{
    private event EventHandler<FilterStateChangedEventArgs>? _contractFilterChanged;

    event EventHandler<FilterStateChangedEventArgs>? IFilterPanel.FilterChanged
    {
        add => _contractFilterChanged += value;
        remove => _contractFilterChanged -= value;
    }

    FilterSelection IFilterPanel.GetSelection()
    {
        var states = new Dictionary<string, FilterTriState>();
        foreach (var (tag, s) in _tagStates)
            states[$"tag:{tag}"] = s == 1 ? FilterTriState.Included : FilterTriState.Excluded;
        foreach (var a in _selectedActors) states[$"actor:{a}"] = FilterTriState.Included;
        foreach (var a in _excludedActors) states[$"actor:{a}"] = FilterTriState.Excluded;
        foreach (var s in _selectedSeries) states[$"series:{s}"] = FilterTriState.Included;
        foreach (var s in _excludedSeries) states[$"series:{s}"] = FilterTriState.Excluded;
        foreach (var s in _selectedStudio) states[$"studio:{s}"] = FilterTriState.Included;
        foreach (var s in _excludedStudio) states[$"studio:{s}"] = FilterTriState.Excluded;
        foreach (var r in _selectedResolutions) states[$"resolution:{r}"] = FilterTriState.Included;
        foreach (var r in _excludedResolutions) states[$"resolution:{r}"] = FilterTriState.Excluded;
        foreach (var d in _selectedDurations) states[$"duration:{d}"] = FilterTriState.Included;
        foreach (var d in _excludedDurations) states[$"duration:{d}"] = FilterTriState.Excluded;
        foreach (var s in _selectedStatuses) states[$"status:{s}"] = FilterTriState.Included;
        foreach (var s in _excludedStatuses) states[$"status:{s}"] = FilterTriState.Excluded;
        if (_censorType is { } censor) states[$"censor:{censor}"] = FilterTriState.Included;
        if (_favoritesOnly) states["toggle:favorites"] = FilterTriState.Included;
        else if (_excludeFavorites) states["toggle:favorites"] = FilterTriState.Excluded;
        if (_watchedOnly) states["toggle:watched"] = FilterTriState.Included;
        else if (_excludeWatched) states["toggle:watched"] = FilterTriState.Excluded;
        return new FilterSelection
        {
            Keyword = KeywordBox.Text.Trim(),
            OptionStates = states,
        };
    }

    void IFilterPanel.ApplySelection(FilterSelection selection)
    {
        var state = new VideoFilterState(
            selection.Keyword,
            ToSet(selection.OptionStates.Where(kv => kv.Value == FilterTriState.Included && kv.Key.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key[4..])),
            ToSet(selection.OptionStates.Where(kv => kv.Value == FilterTriState.Excluded && kv.Key.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key[4..])),
            ToSet(selection.OptionStates.Where(kv => kv.Value == FilterTriState.Included && kv.Key.StartsWith("actor:", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key[6..])),
            ToSet(selection.OptionStates.Where(kv => kv.Value == FilterTriState.Included && kv.Key.StartsWith("series:", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key[7..])),
            ToSet(selection.OptionStates.Where(kv => kv.Value == FilterTriState.Included && kv.Key.StartsWith("studio:", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key[7..])),
            Censor(selection, "censor:"),
            ToSet(selection.OptionStates.Where(kv => kv.Value == FilterTriState.Included && kv.Key.StartsWith("resolution:", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key[11..])),
            Durations(selection, included: true),
            Statuses(selection, included: true),
            selection.OptionStates.TryGetValue("toggle:favorites", out var fav) && fav == FilterTriState.Included,
            selection.OptionStates.TryGetValue("toggle:watched", out var watched) && watched == FilterTriState.Included,
            ToSet(selection.OptionStates.Where(kv => kv.Value == FilterTriState.Excluded && kv.Key.StartsWith("actor:", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key[6..])),
            ToSet(selection.OptionStates.Where(kv => kv.Value == FilterTriState.Excluded && kv.Key.StartsWith("series:", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key[7..])),
            ToSet(selection.OptionStates.Where(kv => kv.Value == FilterTriState.Excluded && kv.Key.StartsWith("studio:", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key[7..])),
            ToSet(selection.OptionStates.Where(kv => kv.Value == FilterTriState.Excluded && kv.Key.StartsWith("resolution:", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key[11..])),
            Durations(selection, included: false),
            Statuses(selection, included: false),
            selection.OptionStates.TryGetValue("toggle:favorites", out var favEx) && favEx == FilterTriState.Excluded,
            selection.OptionStates.TryGetValue("toggle:watched", out var watchedEx) && watchedEx == FilterTriState.Excluded);
        ApplyState(state);
        NotifyChanged();
    }

    void IFilterPanel.Clear() => ClearAllFilters();

    void IFilterPanel.SetSections(IReadOnlyList<FilterSection> sections)
    {
        var tags = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var actors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var series = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var studios = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections)
        {
            switch (section.Id)
            {
                case "tags":
                    foreach (var o in section.Options) tags[o.Label] = o.Count;
                    break;
                case "actors":
                    foreach (var o in section.Options) actors[o.Label] = o.Count;
                    break;
                case "series":
                    foreach (var o in section.Options) series[o.Label] = o.Count;
                    break;
                case "studios":
                    foreach (var o in section.Options) studios[o.Label] = o.Count;
                    break;
            }
        }
        SetCounts(tags, actors, series, studios);
    }

    private static IReadOnlySet<string> ToSet(IEnumerable<string> source)
        => source.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static CensorType? Censor(FilterSelection selection, string prefix)
        => selection.OptionStates.FirstOrDefault(kv => kv.Value == FilterTriState.Included && kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } kv
            && Enum.TryParse<CensorType>(kv.Key[prefix.Length..], out var censor)
                ? censor
                : null;

    private static IReadOnlySet<DurationRange> Durations(FilterSelection selection, bool included)
        => selection.OptionStates
            .Where(kv => kv.Value == (included ? FilterTriState.Included : FilterTriState.Excluded) && kv.Key.StartsWith("duration:", StringComparison.OrdinalIgnoreCase))
            .Select(kv => Enum.TryParse<DurationRange>(kv.Key["duration:".Length..], out var d) ? d : (DurationRange?)null)
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .ToHashSet();

    private static IReadOnlySet<ScrapeStatus> Statuses(FilterSelection selection, bool included)
        => selection.OptionStates
            .Where(kv => kv.Value == (included ? FilterTriState.Included : FilterTriState.Excluded) && kv.Key.StartsWith("status:", StringComparison.OrdinalIgnoreCase))
            .Select(kv => Enum.TryParse<ScrapeStatus>(kv.Key["status:".Length..], out var s) ? s : (ScrapeStatus?)null)
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .ToHashSet();
}
