using System.Windows;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Filtering;

namespace ResourceGrab.App.Views;

/// <summary>LocalSearchPanel 实现 ISidebarPanel，作为 Filter 模式挂到 SidebarHost。</summary>
public partial class LocalSearchPanel : ISidebarPanel
{
    public string PanelId => "filter.manga";
    public string Title => "本地筛选";
    public object? Icon => null;
    public new FrameworkElement Content => this;
}

/// <summary>
/// LocalSearchPanel 的统一筛选契约实现。
/// 选项 Id 约定：tag:{标签}、author:{作者}、rating:{星级}。
/// </summary>
public partial class LocalSearchPanel : IFilterPanel
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
        foreach (var t in _includedTags) states[$"tag:{t}"] = FilterTriState.Included;
        foreach (var t in _excludedTags) states[$"tag:{t}"] = FilterTriState.Excluded;
        foreach (var a in _includedAuthors) states[$"author:{a}"] = FilterTriState.Included;
        foreach (var a in _excludedAuthors) states[$"author:{a}"] = FilterTriState.Excluded;
        foreach (var r in _includedRatings) states[$"rating:{r}"] = FilterTriState.Included;
        foreach (var r in _excludedRatings) states[$"rating:{r}"] = FilterTriState.Excluded;
        return new FilterSelection
        {
            Keyword = KeywordBox.Text.Trim(),
            OptionStates = states,
        };
    }

    void IFilterPanel.ApplySelection(FilterSelection selection)
    {
        KeywordBox.Text = selection.Keyword;
        _includedTags.Clear();
        _excludedTags.Clear();
        _includedAuthors.Clear();
        _excludedAuthors.Clear();
        _includedRatings.Clear();
        _excludedRatings.Clear();
        foreach (var (id, state) in selection.OptionStates)
        {
            if (state == FilterTriState.None) continue;
            var included = state == FilterTriState.Included;
            if (id.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)) (included ? _includedTags : _excludedTags).Add(id[4..]);
            else if (id.StartsWith("author:", StringComparison.OrdinalIgnoreCase)) (included ? _includedAuthors : _excludedAuthors).Add(id[7..]);
            else if (id.StartsWith("rating:", StringComparison.OrdinalIgnoreCase) && int.TryParse(id[7..], out var stars)) (included ? _includedRatings : _excludedRatings).Add(stars);
        }
        RebuildTagFilters();
        RebuildAuthorFilters();
        RebuildRatingFilters();
        UpdateClearButtons();
        UpdateInputUi();
    }

    void IFilterPanel.Clear() => ClearButton_Click(this, new RoutedEventArgs());

    void IFilterPanel.SetSections(IReadOnlyList<FilterSection> sections)
    {
        foreach (var section in sections)
        {
            switch (section.Id)
            {
                case "tags":
                    SetTagCounts(section.Options.ToDictionary(o => o.Label, o => o.Count, StringComparer.OrdinalIgnoreCase));
                    break;
                case "authors":
                    SetAuthorCounts(section.Options.ToDictionary(o => o.Label, o => o.Count, StringComparer.OrdinalIgnoreCase));
                    break;
                case "ratings":
                    _ratingCounts = new Dictionary<int, int>();
                    foreach (var o in section.Options)
                        if (int.TryParse(o.Id["rating:".Length..], out var stars))
                            _ratingCounts[stars] = o.Count;
                    RebuildRatingFilters();
                    break;
            }
        }
        UpdateClearButtons();
    }
}
