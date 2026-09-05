using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Common;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Services;
using ResourceGrab.App.ViewModels;
using ResourceGrab.Core.Downloading;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources;

namespace ResourceGrab.App.Views;

public partial class SearchView : CardGridViewBase
{
    private readonly SourceManager _sourceManager;
    private readonly AggregateSearchService _aggregate;
    private readonly ConfigService _config;
    private readonly DownloadManager _downloadManager;
    private readonly LocalLibraryService _localLibrary;

    private HashSet<string> _downloadedKeys = new();
    private long _page = 1;
    private int _searchVersion;
    private CancellationTokenSource? _searchCts;
    private string _cachedKeyword = "";
    private readonly Dictionary<string, Dictionary<long, SourceSearchGroup>> _sourcePageCache = new();
    private readonly object _cacheLock = new();
    private System.Windows.Controls.Primitives.ToggleButton _allTab = null!;
    private readonly List<System.Windows.Controls.Primitives.ToggleButton> _sourceTabs = new();

    public ObservableCollection<AlbumCardViewModel> Results { get; } = new();

    public StateKind CurrentState
    {
        get => (StateKind)GetValue(CurrentStateProperty);
        set => SetValue(CurrentStateProperty, value);
    }

    public static readonly DependencyProperty CurrentStateProperty = DependencyProperty.Register(
        nameof(CurrentState),
        typeof(StateKind),
        typeof(SearchView),
        new PropertyMetadata(StateKind.Hint));

    public ICommand RetryCommand { get; }

    public SearchView()
    {
        InitializeComponent();
        _sourceManager = App.Services.GetRequiredService<SourceManager>();
        _aggregate = App.Services.GetRequiredService<AggregateSearchService>();
        _config = App.Services.GetRequiredService<ConfigService>();
        _downloadManager = App.Services.GetRequiredService<DownloadManager>();
        _localLibrary = App.Services.GetRequiredService<LocalLibraryService>();
        RetryCommand = new RelayCommand(_ => _ = SearchAsync(1, force: true));
        BuildSourceTabs();
    }

    private void BuildSourceTabs()
    {
        _sourceTabs.Clear();
        Toolbar.Chips.Children.Clear();
        _allTab = new System.Windows.Controls.Primitives.ToggleButton
        {
            Style = (Style)FindResource("FilterMultiTabStyle"),
            Content = "全部",
            IsChecked = true,
            Margin = new Thickness(0, 0, 6, 6),
        };
        _allTab.Click += AllTab_Click;
        Toolbar.Chips.Children.Add(_allTab);
        foreach (var source in _sourceManager.Sources)
        {
            var tab = new System.Windows.Controls.Primitives.ToggleButton
            {
                Style = (Style)FindResource("FilterMultiTabStyle"),
                Content = source.Info.DisplayName,
                Tag = source,
                IsChecked = true,
                Margin = new Thickness(0, 0, 6, 6),
            };
            tab.Click += SourceToggle_Click;
            Toolbar.Chips.Children.Add(tab);
            _sourceTabs.Add(tab);
        }
    }

    private void AllTab_Click(object sender, RoutedEventArgs e)
    {
        var isChecked = _allTab.IsChecked == true;
        foreach (var t in _sourceTabs)
            t.IsChecked = isChecked;
        if (!string.IsNullOrWhiteSpace(Toolbar.SearchBox.Text))
            _ = SearchAsync(_page);
    }

    private void SourceToggle_Click(object sender, RoutedEventArgs e)
    {
        var allChecked = _sourceTabs.All(t => t.IsChecked == true);
        _allTab.IsChecked = allChecked;
        if (!string.IsNullOrWhiteSpace(Toolbar.SearchBox.Text))
            _ = SearchAsync(_page);
    }

    private IReadOnlyList<IComicSource> GetSelectedSources()
    {
        if (_allTab.IsChecked == true)
            return _sourceManager.Sources;
        return _sourceTabs.Where(t => t.IsChecked == true).Select(t => (IComicSource)t.Tag!).ToList();
    }

    private void KeywordSearch_SearchChanged(object? sender, string e)
    {
    }

    private void KeywordSearch_SearchSubmitted(object? sender, string e) => _ = SearchAsync(1, force: true);

    public void Search(string keyword)
    {
        Toolbar.SearchBox.Text = keyword;
        _ = SearchAsync(1, force: true);
    }

    public void Search(string keyword, string? sourceId)
    {
        SelectSource(sourceId);
        Search(keyword);
    }

    private void SelectSource(string? sourceId)
    {
        var selected = _sourceTabs.FirstOrDefault(t =>
            t.Tag is IComicSource source && source.Info.Id == sourceId);

        if (selected is null)
        {
            _allTab.IsChecked = true;
            foreach (var tab in _sourceTabs) tab.IsChecked = true;
            return;
        }

        _allTab.IsChecked = false;
        foreach (var tab in _sourceTabs) tab.IsChecked = ReferenceEquals(tab, selected);
    }

    public void RefreshDownloadedKeys(HashSet<string> keys)
    {
        _downloadedKeys = keys;
        foreach (var item in Results)
        {
            item.IsDownloaded = keys.Contains(LocalLibraryService.KeyFor(item.SourceId, item.Id));
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(1, force: true);

    private void PrevButton_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(_page - 1);

    private void NextButton_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(_page + 1);

    private async Task SearchAsync(long page, bool force = false)
    {
        var keyword = Toolbar.SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            ToastService.Show("请输入搜索关键词", ToastKind.Info);
            return;
        }
        if (page < 1) return;

        var selected = GetSelectedSources();
        if (selected.Count == 0)
        {
            ToastService.Show("请至少选择一个源", ToastKind.Info);
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var searchCt = _searchCts.Token;

        _page = page;
        var version = ++_searchVersion;
        if (force || keyword != _cachedKeyword)
        {
            lock (_cacheLock) _sourcePageCache.Clear();
            _cachedKeyword = keyword;
        }

        SetBusy(true);
        try
        {
            try
            {
                var keysTask = Task.Run(() => _localLibrary.GetDownloadedKeys(_config.Current.DownloadDir), searchCt);
                var completed = await Task.WhenAny(keysTask, Task.Delay(TimeSpan.FromSeconds(2), searchCt));
                if (completed == keysTask && keysTask.IsCompletedSuccessfully)
                    _downloadedKeys = keysTask.Result;
            }
            catch (OperationCanceledException) { return; }
            catch { }
            if (searchCt.IsCancellationRequested) return;

            if (selected.Count == 1)
            {
                var (group, fromCache) = await GetOrSearchSourceAsync(selected[0], keyword, (int)page, searchCt);
                if (version != _searchVersion || searchCt.IsCancellationRequested) return;
                RenderSingle(group, page, showError: !fromCache);
            }
            else
            {
                var tasks = selected.Select(s => GetOrSearchSourceAsync(s, keyword, (int)page, searchCt)).ToArray();
                var results = await Task.WhenAll(tasks);
                if (version != _searchVersion || searchCt.IsCancellationRequested) return;
                var groups = results.Select(r => r.Group).ToList();
                var anyNotCached = results.Any(r => !r.FromCache);
                RenderAggregate(groups, page, showError: anyNotCached);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _searchVersion)
            {
                ShowState(StateKind.Empty);
                ToastService.ShowError(ex);
            }
        }
        finally
        {
            if (version == _searchVersion)
                SetBusy(false);
        }
    }

    private async Task<(SourceSearchGroup Group, bool FromCache)> GetOrSearchSourceAsync(
        IComicSource source, string keyword, int page, CancellationToken ct = default)
    {
        if (TryGetCachedGroup(source.Info.Id, page, out var cached))
        {
            return (cached, true);
        }
        var group = await _aggregate.SearchSourceAsync(source, keyword, page, ct);
        StoreGroup(group, page);
        return (group, false);
    }

    private bool TryGetCachedGroup(string sourceId, int page, out SourceSearchGroup group)
    {
        lock (_cacheLock)
        {
            if (_sourcePageCache.TryGetValue(sourceId, out var pages) && pages.TryGetValue(page, out var cached))
            {
                group = cached;
                return true;
            }
        }
        group = null!;
        return false;
    }

    private void StoreGroup(SourceSearchGroup group, int page)
    {
        lock (_cacheLock)
        {
            if (!_sourcePageCache.TryGetValue(group.Source.Info.Id, out var pages))
            {
                pages = new Dictionary<long, SourceSearchGroup>();
                _sourcePageCache[group.Source.Info.Id] = pages;
            }
            pages[page] = group;
        }
    }

    private void RenderAggregate(IReadOnlyList<SourceSearchGroup> groups, long page, bool showError)
    {
        var singles = new List<SourceSearchGroup>();
        long maxPages = 1;
        bool hasItems = false;
        Results.Clear();
        foreach (var g in groups)
        {
            var result = g.Result;
            if (result == null)
            {
                if (showError)
                    ToastService.Show($"{g.Source.Info.DisplayName} 搜索失败", ToastKind.Error);
                continue;
            }
            maxPages = Math.Max(maxPages, result.TotalPages);
            if (result.IsSingleMatch && result.SingleComicId is not null)
            {
                singles.Add(g);
                continue;
            }
            if (result.Items.Count == 0) continue;
            hasItems = true;
            foreach (var item in result.Items)
                Results.Add(ToCard(item, g.Source, true));
        }
        if (!hasItems)
        {
            if (singles.Count == 1)
            {
                ShowState(StateKind.Result);
                Navigation.OpenComic(singles[0].Source.Info.Id, singles[0].Result!.SingleComicId!);
                return;
            }
            if (singles.Count > 1)
            {
                ToastService.Show($"多个源命中单本：{string.Join("、", singles.Select(s => s.Source.Info.DisplayName))}", ToastKind.Info);
            }
            ShowState(StateKind.Empty);
            return;
        }
        UpdatePaging(page, maxPages);
    }

    private void RenderSingle(SourceSearchGroup group, long page, bool showError)
    {
        var result = group.Result;
        if (result is null)
        {
            ShowState(StateKind.Empty);
            if (showError)
            {
                ToastService.Show($"{group.Source.Info.DisplayName} 搜索失败", ToastKind.Error);
            }
            return;
        }
        if (result.IsSingleMatch && result.SingleComicId is { } singleId)
        {
            Results.Clear();
            ShowState(StateKind.Result);
            Navigation.OpenComic(group.Source.Info.Id, singleId);
            return;
        }
        if (result.Items.Count == 0)
        {
            ShowState(StateKind.Empty);
            return;
        }

        Results.Clear();
        foreach (var item in result.Items)
        {
            Results.Add(ToCard(item, group.Source, false));
        }
        UpdatePaging(page, result.TotalPages);
    }

    private void UpdatePaging(long page, long totalPages)
    {
        PageText.Text = $"第 {Math.Max(1, page)} / {Math.Max(1, totalPages)} 页";
        PrevButton.IsEnabled = page > 1;
        NextButton.IsEnabled = page < totalPages;
        ShowState(StateKind.Result);
    }

    private AlbumCardViewModel ToCard(ComicSummary item, IComicSource source, bool showBadge) => new()
    {
        Id = item.Id,
        SourceId = source.Info.Id,
        Name = item.Title,
        AuthorText = string.IsNullOrEmpty(item.Author) ? "未知作者" : item.Author,
        CoverUrl = item.CoverUrl,
        SourceBadge = showBadge ? source.Info.DisplayName : "",
        ImageHeaders = source.Info.CoverHeaders,
        IsDownloaded = _downloadedKeys.Contains(LocalLibraryService.KeyFor(source.Info.Id, item.Id)),
        OpenCommand = new RelayCommand(_ => Navigation.OpenComic(source.Info.Id, item.Id)),
        DownloadCommand = new AsyncRelayCommand(async _ =>
        {
            try
            {
                var (count, title) = await DownloadHelper.EnqueueAllAsync(
                    source, _config, _downloadManager, item.Id);
                ToastService.Show($"已将「{title}」全部 {count} 个章节加入下载队列", ToastKind.Success);
            }
            catch (Exception ex)
            {
                ToastService.ShowError(ex);
            }
        }),
    };

    private void SetBusy(bool busy)
    {
        if (busy)
        {
            CurrentState = StateKind.Loading;
            PagingPanel.Visibility = Visibility.Collapsed;
        }
        else if (CurrentState == StateKind.Loading)
        {
            CurrentState = StateKind.Hint;
        }
    }

    private void ShowState(StateKind state)
    {
        CurrentState = state;
        PagingPanel.Visibility = state == StateKind.Result ? Visibility.Visible : Visibility.Collapsed;
    }
}
