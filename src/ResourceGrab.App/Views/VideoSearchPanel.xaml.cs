using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Models;

namespace ResourceGrab.App.Views;

/// <summary>视频筛选侧边栏：参考 LocalSearchPanel 的分组布局与三态 Chip 交互。</summary>
public partial class VideoSearchPanel : UserControl
{
    private readonly Dictionary<string, int> _tagStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedActors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludedActors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedSeries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludedSeries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedStudio = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludedStudio = new(StringComparer.OrdinalIgnoreCase);
    private CensorType? _censorType;
    private readonly HashSet<string> _selectedResolutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludedResolutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<DurationRange> _selectedDurations = new();
    private readonly HashSet<DurationRange> _excludedDurations = new();
    private readonly HashSet<ScrapeStatus> _selectedStatuses = new();
    private readonly HashSet<ScrapeStatus> _excludedStatuses = new();
    private bool _favoritesOnly;
    private bool _excludeFavorites;
    private bool _watchedOnly;
    private bool _excludeWatched;

    public event Action<VideoFilterState>? FilterChanged;
    public event Action<string>? SearchTextChanged;
    public event Action<string>? SourceChanged;
    public event Action<string>? OnlineSearchSubmitted;

    private bool _isOnlineMode;

    public VideoSearchPanel()
    {
        InitializeComponent();
        UpdateInputUi();
    }

    public record VideoFilterState(
        string Keyword,
        IReadOnlySet<string> IncludedTags,
        IReadOnlySet<string> ExcludedTags,
        IReadOnlySet<string> Actors,
        IReadOnlySet<string> Series,
        IReadOnlySet<string> Studio,
        CensorType? CensorType,
        IReadOnlySet<string> Resolution,
        IReadOnlySet<DurationRange> Duration,
        IReadOnlySet<ScrapeStatus> ScrapeStatus,
        bool FavoritesOnly,
        bool WatchedOnly,
        IReadOnlySet<string>? ExcludedActors = null,
        IReadOnlySet<string>? ExcludedSeries = null,
        IReadOnlySet<string>? ExcludedStudio = null,
        IReadOnlySet<string>? ExcludedResolution = null,
        IReadOnlySet<DurationRange>? ExcludedDuration = null,
        IReadOnlySet<ScrapeStatus>? ExcludedScrapeStatus = null,
        bool ExcludeFavorites = false,
        bool ExcludeWatched = false);

    // ── 数据填充（由 VideoView 调用）────────────────────────────────

    public void SetTagCounts(IReadOnlyDictionary<string, int> counts) => RebuildTags(counts);
    public void SetActorCounts(IReadOnlyDictionary<string, int> counts) => RebuildActors(counts);
    public void SetSeriesCounts(IReadOnlyDictionary<string, int> counts) => RebuildSeries(counts);
    public void SetStudioCounts(IReadOnlyDictionary<string, int> counts) => RebuildStudios(counts);

    public void SetKeyword(string text)
    {
        KeywordBox.Text = text;
    }

    /// <summary>切换本地/在线模式，影响侧栏标题和源选择器可见性。</summary>
    public void SetMode(bool isOnline)
    {
        _isOnlineMode = isOnline;
        PanelTitleText.Text = isOnline ? "在线搜索" : "视频筛选";
        SourceSelectorHost.Visibility = isOnline ? Visibility.Visible : Visibility.Collapsed;
    }

    public string SelectedSourceId =>
        SourceBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "missav";

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isOnlineMode)
            SourceChanged?.Invoke(SelectedSourceId);
    }

    /// <summary>在线模式回车提交搜索。</summary>
    public void SubmitOnlineSearch() => OnlineSearchSubmitted?.Invoke(KeywordBox.Text.Trim());

    public void ClearAllFilters()
    {
        _tagStates.Clear();
        _selectedActors.Clear();
        _excludedActors.Clear();
        _selectedSeries.Clear();
        _excludedSeries.Clear();
        _selectedStudio.Clear();
        _excludedStudio.Clear();
        _selectedResolutions.Clear();
        _excludedResolutions.Clear();
        _selectedDurations.Clear();
        _excludedDurations.Clear();
        _selectedStatuses.Clear();
        _excludedStatuses.Clear();
        _excludeFavorites = _excludeWatched = false;
        _censorType = null;
        _favoritesOnly = false;
        _watchedOnly = false;
        KeywordBox.Text = "";
        NotifyChanged();
    }

    public void AddActorFilter(string actor)
    {
        _selectedActors.Add(actor);
        NotifyChanged();
    }

    public void AddTagFilter(string tag)
    {
        _tagStates[tag] = 1;
        NotifyChanged();
    }

    /// <summary>把标签加入排除（详情页标签右键调用）；若该标签已在排除中则保持排除。</summary>
    public void AddTagExclusion(string tag)
    {
        _tagStates[tag] = 2;
        NotifyChanged();
    }

    /// <summary>把演员加入排除（详情页演员右键调用），同时取消其选中态。</summary>
    public void AddActorExclusion(string actor)
    {
        _excludedActors.Add(actor);
        _selectedActors.Remove(actor);
        NotifyChanged();
    }

    /// <summary>把系列加入排除（详情页系列标题右键调用），同时取消其选中态。</summary>
    public void AddSeriesExclusion(string series)
    {
        _excludedSeries.Add(series);
        _selectedSeries.Remove(series);
        NotifyChanged();
    }

    /// <summary>把片商加入排除，同时取消其选中态。</summary>
    public void AddStudioExclusion(string studio)
    {
        _excludedStudio.Add(studio);
        _selectedStudio.Remove(studio);
        NotifyChanged();
    }

    public VideoFilterState BuildState() => new(
        KeywordBox.Text.Trim(),
        ToSet(_tagStates.Where(kv => kv.Value == 1)),
        ToSet(_tagStates.Where(kv => kv.Value == 2)),
        new HashSet<string>(_selectedActors, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(_selectedSeries, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(_selectedStudio, StringComparer.OrdinalIgnoreCase),
        _censorType,
        new HashSet<string>(_selectedResolutions, StringComparer.OrdinalIgnoreCase),
        new HashSet<DurationRange>(_selectedDurations),
        new HashSet<ScrapeStatus>(_selectedStatuses),
        _favoritesOnly, _watchedOnly,
        new HashSet<string>(_excludedActors, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(_excludedSeries, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(_excludedStudio, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(_excludedResolutions, StringComparer.OrdinalIgnoreCase),
        new HashSet<DurationRange>(_excludedDurations),
        new HashSet<ScrapeStatus>(_excludedStatuses),
        _excludeFavorites, _excludeWatched);

    public void ApplyState(VideoFilterState state)
    {
        _tagStates.Clear();
        foreach (var tag in state.IncludedTags) _tagStates[tag] = 1;
        foreach (var tag in state.ExcludedTags)
        {
            if (!_tagStates.ContainsKey(tag)) _tagStates[tag] = 2;
            else _tagStates.Remove(tag);
        }
        _selectedActors.Clear();
        foreach (var actor in state.Actors) _selectedActors.Add(actor);
        _excludedActors.Clear();
        foreach (var actor in state.ExcludedActors ?? new HashSet<string>()) _excludedActors.Add(actor);
        _selectedSeries.Clear();
        foreach (var s in state.Series) _selectedSeries.Add(s);
        _selectedStudio.Clear();
        foreach (var s in state.Studio) _selectedStudio.Add(s);
        _excludedSeries.Clear();
        foreach (var s in state.ExcludedSeries ?? new HashSet<string>()) _excludedSeries.Add(s);
        _excludedStudio.Clear();
        foreach (var s in state.ExcludedStudio ?? new HashSet<string>()) _excludedStudio.Add(s);
        _selectedResolutions.Clear();
        foreach (var r in state.Resolution) _selectedResolutions.Add(r);
        _excludedResolutions.Clear();
        foreach (var r in state.ExcludedResolution ?? new HashSet<string>()) _excludedResolutions.Add(r);
        _selectedDurations.Clear();
        foreach (var d in state.Duration) _selectedDurations.Add(d);
        _excludedDurations.Clear();
        foreach (var d in state.ExcludedDuration ?? new HashSet<DurationRange>()) _excludedDurations.Add(d);
        _selectedStatuses.Clear();
        foreach (var s in state.ScrapeStatus) _selectedStatuses.Add(s);
        _excludedStatuses.Clear();
        foreach (var s in state.ExcludedScrapeStatus ?? new HashSet<ScrapeStatus>()) _excludedStatuses.Add(s);
        _excludeFavorites = state.ExcludeFavorites;
        _excludeWatched = state.ExcludeWatched;
        _censorType = state.CensorType;
        _favoritesOnly = state.FavoritesOnly;
        _watchedOnly = state.WatchedOnly;
        KeywordBox.Text = state.Keyword;
        RebuildAll();
    }

    // ── Chip 渲染 ────────────────────────────────────────────────────

    private Style _chipStyle => (Style)FindResource("VideoChipStyle");
    private Style _chipTextStyle => (Style)FindResource("VideoChipTextStyle");
    private Style _chipCountStyle => (Style)FindResource("VideoChipCountStyle");

    private enum ChipState { Off, Active, Exclude }

    private Border MakeChip(string text, ChipState state, string? suffix = null, Action? onClick = null,
        Action? onRightClick = null)
    {
        var prefix = state switch { ChipState.Active => "\u2713 ", ChipState.Exclude => "\u2205 ", _ => "" };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = $"{prefix}{text}", FontSize = 11, Style = _chipTextStyle, VerticalAlignment = VerticalAlignment.Center });
        if (suffix is not null)
            panel.Children.Add(new TextBlock { Text = $" {suffix}", FontSize = 9.5, Style = _chipCountStyle, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        var border = new Border
        {
            Style = _chipStyle,
            Tag = state switch { ChipState.Active => "active", ChipState.Exclude => "exclude", _ => "" },
            Child = panel,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 6, 6),
        };
        if (onClick is not null) border.MouseLeftButtonUp += (_, e) => { onClick(); e.Handled = true; };
        if (onRightClick is not null) border.MouseRightButtonUp += (_, e) => { onRightClick(); e.Handled = true; };
        return border;
    }

    private void RenderAll(IReadOnlyDictionary<string,int> tagCounts, IReadOnlyDictionary<string,int> actorCounts,
        IReadOnlyDictionary<string,int> seriesCounts, IReadOnlyDictionary<string,int> studioCounts)
    {
        TagCountText.Text = tagCounts.Count == 0 ? "" : $"({tagCounts.Count})";
        ActorCountText.Text = actorCounts.Count == 0 ? "" : $"({actorCounts.Count})";
        SeriesCountText.Text = seriesCounts.Count == 0 ? "" : $"({seriesCounts.Count})";
        StudioCountText.Text = studioCounts.Count == 0 ? "" : $"({studioCounts.Count})";

        CensorHost.Children.Clear();
        foreach (var censor in Enum.GetValues<CensorType>())
        {
            if (censor == CensorType.Unknown) continue;
            var selected = _censorType == censor;
            CensorHost.Children.Add(MakeChip(censor.ToString(), selected ? ChipState.Active : ChipState.Off, onClick: () =>
            {
                _censorType = selected ? null : censor;
                NotifyChanged();
            }));
        }

        TagsHost.Children.Clear();
        foreach (var (tag, count) in tagCounts.Take(40))
        {
            _tagStates.TryGetValue(tag, out var s);
            var state = (ChipState)s;
            TagsHost.Children.Add(MakeChip(tag, state, $"({count})",
                onClick: () => { if (!_tagStates.Remove(tag)) _tagStates[tag] = 1; NotifyChanged(); },
                onRightClick: () => { if (_tagStates.TryGetValue(tag, out var cur) && cur == 2) _tagStates.Remove(tag); else _tagStates[tag] = 2; NotifyChanged(); }));
            if (state == ChipState.Exclude) continue;
        }
        TagEmptyText.Visibility = tagCounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ActorsHost.Children.Clear();
        foreach (var (actor, count) in actorCounts.Take(30))
        {
            var selected = _selectedActors.Contains(actor);
            var excluded = _excludedActors.Contains(actor);
            var state = selected ? ChipState.Active : excluded ? ChipState.Exclude : ChipState.Off;
            ActorsHost.Children.Add(MakeChip(actor, state, $"({count})",
                onClick: () => { _excludedActors.Remove(actor); if (!_selectedActors.Remove(actor)) _selectedActors.Add(actor); NotifyChanged(); },
                onRightClick: () => { if (!_excludedActors.Remove(actor)) { _selectedActors.Remove(actor); _excludedActors.Add(actor); } NotifyChanged(); }));
        }
        ActorEmptyText.Visibility = actorCounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SeriesHost.Children.Clear();
        foreach (var (series, count) in seriesCounts.Take(20))
        {
            var selected = _selectedSeries.Contains(series);
            var excluded = _excludedSeries.Contains(series);
            var state = selected ? ChipState.Active : excluded ? ChipState.Exclude : ChipState.Off;
            SeriesHost.Children.Add(MakeChip(series, state, $"({count})",
                onClick: () => { _excludedSeries.Remove(series); if (!_selectedSeries.Remove(series)) _selectedSeries.Add(series); NotifyChanged(); },
                onRightClick: () => { if (!_excludedSeries.Remove(series)) { _selectedSeries.Remove(series); _excludedSeries.Add(series); } NotifyChanged(); }));
        }
        SeriesEmptyText.Visibility = seriesCounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        StudioHost.Children.Clear();
        foreach (var (studio, count) in studioCounts.Take(20))
        {
            var selected = _selectedStudio.Contains(studio);
            var excluded = _excludedStudio.Contains(studio);
            var state = selected ? ChipState.Active : excluded ? ChipState.Exclude : ChipState.Off;
            StudioHost.Children.Add(MakeChip(studio, state, $"({count})",
                onClick: () => { _excludedStudio.Remove(studio); if (!_selectedStudio.Remove(studio)) _selectedStudio.Add(studio); NotifyChanged(); },
                onRightClick: () => { if (!_excludedStudio.Remove(studio)) { _selectedStudio.Remove(studio); _excludedStudio.Add(studio); } NotifyChanged(); }));
        }
        StudioEmptyText.Visibility = studioCounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ResolutionHost.Children.Clear();
        foreach (var res in new[] { "4K", "1080p", "720p", "480p" })
        {
            var selected = _selectedResolutions.Contains(res);
            var excluded = _excludedResolutions.Contains(res);
            var state = selected ? ChipState.Active : excluded ? ChipState.Exclude : ChipState.Off;
            ResolutionHost.Children.Add(MakeChip(res, state,
                onClick: () => { _excludedResolutions.Remove(res); if (!_selectedResolutions.Remove(res)) _selectedResolutions.Add(res); NotifyChanged(); },
                onRightClick: () => { if (!_excludedResolutions.Remove(res)) { _selectedResolutions.Remove(res); _excludedResolutions.Add(res); } NotifyChanged(); }));
        }

        DurationHost.Children.Clear();
        AddDurationChip(DurationRange.Under30Minutes, "<30min");
        AddDurationChip(DurationRange.From30To60Minutes, "30-60min");
        AddDurationChip(DurationRange.Over60Minutes, ">60min");

        StatusHost.Children.Clear();
        AddToggleChip("收藏", _favoritesOnly, _excludeFavorites, v => _favoritesOnly = v, v => _excludeFavorites = v);
        AddToggleChip("已看", _watchedOnly, _excludeWatched, v => _watchedOnly = v, v => _excludeWatched = v);
        foreach (var status in new[] { ScrapeStatus.Pending, ScrapeStatus.NoMatch, ScrapeStatus.Failed })
        {
            var selected = _selectedStatuses.Contains(status);
            var excluded = _excludedStatuses.Contains(status);
            var state = selected ? ChipState.Active : excluded ? ChipState.Exclude : ChipState.Off;
            StatusHost.Children.Add(MakeChip(status switch
            {
                ScrapeStatus.Pending => "未刮削",
                ScrapeStatus.NoMatch => "未匹配",
                _ => "失败",
            }, state,
                onClick: () => { _excludedStatuses.Remove(status); if (!_selectedStatuses.Remove(status)) _selectedStatuses.Add(status); NotifyChanged(); },
                onRightClick: () => { if (!_excludedStatuses.Remove(status)) { _selectedStatuses.Remove(status); _excludedStatuses.Add(status); } NotifyChanged(); }));
        }
    }

    /// <summary>收藏/已看这类两态开关：左键=只看（✓），右键=排除（∅，只看相反的一侧）。</summary>
    private void AddToggleChip(string label, bool active, bool excluded, Action<bool> setActive, Action<bool> setExcluded)
    {
        var state = active ? ChipState.Active : excluded ? ChipState.Exclude : ChipState.Off;
        StatusHost.Children.Add(MakeChip(label, state,
            onClick: () => { setActive(!active); if (!active) setExcluded(false); NotifyChanged(); },
            onRightClick: () => { setExcluded(!excluded); if (!excluded) setActive(false); NotifyChanged(); }));
    }

    private void AddDurationChip(DurationRange range, string label)
    {
        var selected = _selectedDurations.Contains(range);
        var excluded = _excludedDurations.Contains(range);
        var state = selected ? ChipState.Active : excluded ? ChipState.Exclude : ChipState.Off;
        DurationHost.Children.Add(MakeChip(label, state,
            onClick: () => { _excludedDurations.Remove(range); if (!_selectedDurations.Remove(range)) _selectedDurations.Add(range); NotifyChanged(); },
            onRightClick: () => { if (!_excludedDurations.Remove(range)) { _selectedDurations.Remove(range); _excludedDurations.Add(range); } NotifyChanged(); }));
    }

    private void RebuildTags(IReadOnlyDictionary<string, int> counts) => RefreshChips();
    private void RebuildActors(IReadOnlyDictionary<string, int> counts) => RefreshChips();
    private void RebuildSeries(IReadOnlyDictionary<string, int> counts) => RefreshChips();
    private void RebuildStudios(IReadOnlyDictionary<string, int> counts) => RefreshChips();

    /// <summary>由外部提供全部计数后统一重渲染。</summary>
    public void SetCounts(
        IReadOnlyDictionary<string, int> tagCounts,
        IReadOnlyDictionary<string, int> actorCounts,
        IReadOnlyDictionary<string, int> seriesCounts,
        IReadOnlyDictionary<string, int> studioCounts)
    {
        _lastTagCounts = tagCounts;
        _lastActorCounts = actorCounts;
        _lastSeriesCounts = seriesCounts;
        _lastStudioCounts = studioCounts;
        RenderAll(tagCounts, actorCounts, seriesCounts, studioCounts);
    }

    private void RefreshChips()
    {
        // 占位：等 SetCounts 统一调用后重渲染。
    }

    // ── 关键字输入框 ──────────────────────────────────────────────

    private void KeywordBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateInputUi();
        var text = KeywordBox.Text.Trim();
        if (_isOnlineMode)
        {
            // 在线模式：只在回车时触发搜索，不实时过滤。
        }
        else
        {
            SearchTextChanged?.Invoke(text);
        }
    }

    private void KeywordBox_GotFocus(object sender, RoutedEventArgs e) => UpdateInputUi();
    private void KeywordBox_LostFocus(object sender, RoutedEventArgs e) => UpdateInputUi();

    private void KeywordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _isOnlineMode)
        {
            SubmitOnlineSearch();
            e.Handled = true;
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
        => ClearAllFilters();

    private void UpdateInputUi()
    {
        var hasText = KeywordBox.Text.Length > 0;
        PlaceholderText.Visibility = hasText || KeywordBox.IsFocused ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    private static IReadOnlySet<string> ToSet(IEnumerable<KeyValuePair<string, int>> source)
        => source.Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void RebuildAll()
        => SetCounts(_lastTagCounts, _lastActorCounts, _lastSeriesCounts, _lastStudioCounts);

    private IReadOnlyDictionary<string, int> _lastTagCounts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, int> _lastActorCounts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, int> _lastSeriesCounts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, int> _lastStudioCounts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private void NotifyChanged()
    {
        FilterChanged?.Invoke(BuildState());
        _contractFilterChanged?.Invoke(this, new ResourceGrab.App.Filtering.FilterStateChangedEventArgs(
            ((ResourceGrab.App.Filtering.IFilterPanel)this).GetSelection()));
        RebuildAll();
    }
}
