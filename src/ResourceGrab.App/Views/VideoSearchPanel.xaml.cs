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
    private string _selectedSeries = "";
    private string _selectedStudio = "";
    private CensorType? _censorType;
    private string _resolution = "";
    private DurationRange? _duration;
    private ScrapeStatus? _scrapeStatus;
    private bool _favoritesOnly;
    private bool _watchedOnly;

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
        string Series,
        string Studio,
        CensorType? CensorType,
        string Resolution,
        DurationRange? Duration,
        ScrapeStatus? ScrapeStatus,
        bool FavoritesOnly,
        bool WatchedOnly);

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
        _selectedSeries = _selectedStudio = _resolution = "";
        _censorType = null;
        _duration = null;
        _scrapeStatus = null;
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

    public VideoFilterState BuildState() => new(
        KeywordBox.Text.Trim(),
        ToSet(_tagStates.Where(kv => kv.Value == 1)),
        ToSet(_tagStates.Where(kv => kv.Value == 2)),
        new HashSet<string>(_selectedActors, StringComparer.OrdinalIgnoreCase),
        _selectedSeries, _selectedStudio, _censorType, _resolution,
        _duration, _scrapeStatus, _favoritesOnly, _watchedOnly);

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
        _selectedSeries = state.Series;
        _selectedStudio = state.Studio;
        _censorType = state.CensorType;
        _resolution = state.Resolution;
        _duration = state.Duration;
        _scrapeStatus = state.ScrapeStatus;
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
            ActorsHost.Children.Add(MakeChip(actor, selected ? ChipState.Active : ChipState.Off, $"({count})",
                onClick: () => { if (!_selectedActors.Remove(actor)) _selectedActors.Add(actor); NotifyChanged(); }));
        }
        ActorEmptyText.Visibility = actorCounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SeriesHost.Children.Clear();
        foreach (var (series, count) in seriesCounts.Take(20))
        {
            var selected = _selectedSeries.Equals(series, StringComparison.OrdinalIgnoreCase);
            SeriesHost.Children.Add(MakeChip(series, selected ? ChipState.Active : ChipState.Off, $"({count})",
                onClick: () => { _selectedSeries = selected ? "" : series; NotifyChanged(); }));
        }
        SeriesEmptyText.Visibility = seriesCounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        StudioHost.Children.Clear();
        foreach (var (studio, count) in studioCounts.Take(20))
        {
            var selected = _selectedStudio.Equals(studio, StringComparison.OrdinalIgnoreCase);
            StudioHost.Children.Add(MakeChip(studio, selected ? ChipState.Active : ChipState.Off, $"({count})",
                onClick: () => { _selectedStudio = selected ? "" : studio; NotifyChanged(); }));
        }
        StudioEmptyText.Visibility = studioCounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ResolutionHost.Children.Clear();
        foreach (var res in new[] { "4K", "1080p", "720p", "480p" })
        {
            var selected = _resolution == res;
            ResolutionHost.Children.Add(MakeChip(res, selected ? ChipState.Active : ChipState.Off,
                onClick: () => { _resolution = selected ? "" : res; NotifyChanged(); }));
        }

        DurationHost.Children.Clear();
        AddDurationChip(DurationRange.Under30Minutes, "<30min");
        AddDurationChip(DurationRange.From30To60Minutes, "30-60min");
        AddDurationChip(DurationRange.Over60Minutes, ">60min");

        StatusHost.Children.Clear();
        StatusHost.Children.Add(MakeChip(_favoritesOnly ? "收藏" : "收藏", _favoritesOnly ? ChipState.Active : ChipState.Off,
            onClick: () => { _favoritesOnly = !_favoritesOnly; NotifyChanged(); }));
        StatusHost.Children.Add(MakeChip("已看", _watchedOnly ? ChipState.Active : ChipState.Off,
            onClick: () => { _watchedOnly = !_watchedOnly; NotifyChanged(); }));
        foreach (var status in new[] { ScrapeStatus.Pending, ScrapeStatus.NoMatch, ScrapeStatus.Failed })
        {
            var selected = _scrapeStatus == status;
            StatusHost.Children.Add(MakeChip(status switch
            {
                ScrapeStatus.Pending => "未刮削",
                ScrapeStatus.NoMatch => "未匹配",
                _ => "失败",
            }, selected ? ChipState.Active : ChipState.Off,
                onClick: () => { _scrapeStatus = selected ? null : status; NotifyChanged(); }));
        }
    }

    private void AddDurationChip(DurationRange range, string label)
    {
        var selected = _duration == range;
        DurationHost.Children.Add(MakeChip(label, selected ? ChipState.Active : ChipState.Off,
            onClick: () => { _duration = selected ? null : range; NotifyChanged(); }));
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
        RebuildAll();
    }
}
