using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Net.Http;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Dialogs;
using ResourceGrab.App.Services;
using ResourceGrab.App.Common;
using ResourceGrab.Core.Utils;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace ResourceGrab.App.Views;

public partial class VideoView : CardGridViewBase
{
    private readonly VideoLibraryService _library;
    private readonly VideoScrapeService _scrapeService;
    private VideoScrapeTaskQueue? _taskQueue;
    private readonly ILogger _logger;
    private List<VideoItem> _filtered = [];
    private VideoItem? _currentItem;
    private string _searchText = "";
    private CancellationTokenSource? _enrichCts;
    private CancellationTokenSource? _scrapeCts;
    private readonly HashSet<string> _enrichQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _selectionMode;
    private int _page = 1;
    private int _pageCount = 1;
    private int _pageSize = 30;
    private Style _chipBorderStyle = null!;
    private Style _chipTextStyle = null!;
    private Style _chipCountStyle = null!;
    private string _currentNav = "search";
    private bool _scrapeRunning;
    private string _onlineSearchText = "";
    private int _onlinePage = 1;
    private CancellationTokenSource? _onlineSearchCts;
    private int _onlineSearchVersion;
    private VideoSearchPanel? _searchPanel;
    private int _sidebarDataVersion = -1;
    private Action? _actorClose;
    private Dictionary<string, int> _cachedTagCounts = new();
    private Dictionary<string, int> _cachedActorCounts = new();
    private Dictionary<string, int> _cachedSeriesCounts = new();
    private Dictionary<string, int> _cachedStudioCounts = new();
    private IVideoSource? OnlineSource => App.Services.GetServices<IVideoSource>()
        .FirstOrDefault(s => s.Info.Id == "missav");

    public VideoView()
    {
        InitializeComponent();
        _library = App.Services.GetRequiredService<VideoLibraryService>();
        _scrapeService = App.Services.GetRequiredService<VideoScrapeService>();
        _logger = App.Services.GetRequiredService<ILogger>();
        _chipBorderStyle = (Style)FindResource("VideoChipStyle");
        _chipTextStyle = (Style)FindResource("VideoChipTextStyle");
        _chipCountStyle = (Style)FindResource("VideoChipCountStyle");
        _taskQueue = App.Services.GetService(typeof(VideoScrapeTaskQueue)) as VideoScrapeTaskQueue;
        _ = Task.Run(() => _taskQueue?.StartProcessingAsync(ProcessQueueTaskAsync, CancellationToken.None));
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => { _enrichCts?.Cancel(); _scrapeCts?.Cancel(); };
        VideoThumbnailService.ThumbnailSaved += OnThumbnailSaved;
        Unloaded += (_, _) => VideoThumbnailService.ThumbnailSaved -= OnThumbnailSaved;
    }

    public void SetSearchPanel(VideoSearchPanel panel)
    {
        if (_searchPanel != null) { _searchPanel.FilterChanged -= OnFilterChanged; _searchPanel.SearchTextChanged -= OnSearchTextChanged; }
        _searchPanel = panel;
        _searchPanel.FilterChanged += OnFilterChanged;
        _searchPanel.SearchTextChanged += OnSearchTextChanged;
        ActorListPage.ActorSelected = actor => ShowActor(actor, () => SwitchNav("actors"));
    }

    private void OnFilterChanged(VideoSearchPanel.VideoFilterState state)
    {
        _searchText = state.Keyword;
        _filterState = state;
        ApplyAndRender();
    }

    private void OnSearchTextChanged(string text)
    {
        _searchText = text;
        ApplyAndRender();
    }

    public void OnShown()
    {
        if (_currentNav == "local") Refresh();
    }

    private void NavSearch_Click(object sender, RoutedEventArgs e) => SwitchNav("search");
    private void NavRecommend_Click(object sender, RoutedEventArgs e) => SwitchNav("recommend");
    private void NavLocal_Click(object sender, RoutedEventArgs e) => SwitchNav("local");

    public void SwitchNav(string nav)
    {
        _currentNav = nav;
        SearchPage.Visibility = nav == "search" ? Visibility.Visible : Visibility.Collapsed;
        RecommendPage.Visibility = nav == "recommend" ? Visibility.Visible : Visibility.Collapsed;
        LocalPage.Visibility = nav == "local" ? Visibility.Visible : Visibility.Collapsed;
        TaskPage.Visibility = nav == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        TaskBar.Visibility = Visibility.Collapsed;
        ActorPage.Visibility = Visibility.Collapsed;

        if (nav == "local") Refresh();
        if (nav == "actors") ActorListPage.Refresh();
    }

    private void ShowActor(string actor, Action onClose)
    {
        _actorClose = onClose;
        ActorPage.CloseRequested = () => { ActorPage.Visibility = Visibility.Collapsed; _actorClose?.Invoke(); };
        ActorPage.OnLocalWorkSelected = item => { ActorPage.Visibility = Visibility.Collapsed; _currentItem = item; ApplyAndRender(); };
        SearchPage.Visibility = RecommendPage.Visibility = LocalPage.Visibility = TaskPage.Visibility = ActorListPage.Visibility = Visibility.Collapsed;
        ActorPage.Visibility = Visibility.Visible;
        ActorPage.LoadAsync(actor);
    }

    private bool IsReady => _library is not null && _scrapeService is not null;

    private void Refresh()
    {
        RenderSidebar();
        ApplyAndRender();
    }

    private CancellationTokenSource? _renderCts;

    private void ApplyAndRender()
    {
        if (!IsReady) return;
        // 取消上一次未完成的渲染
        _renderCts?.Cancel();
        var ct = new CancellationTokenSource();
        _renderCts = ct;
        var options = BuildQueryOptions();
        var currentItem = _currentItem;
        try
        {
            using (RecursionGuard.Enter("ApplyAndRender"))
            {

        // 在后台线程执行查询和排序
        _ = Task.Run(() =>
        {
            var filtered = _library.Query(options).ToList();
            if (ct.IsCancellationRequested) return;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(() =>
            {
                if (ct.IsCancellationRequested) return;
                _filtered = filtered;
                var fresh = currentItem is null ? null : _all().FirstOrDefault(i => i.Id == currentItem.Id);
                if (currentItem is not null && fresh is null) _currentItem = null;
                else if (currentItem is not null) _currentItem = fresh;

                if (_currentItem is not null) RenderDetail(_currentItem);
                else RenderList();
            }));
        }, ct.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("[VideoView] ApplyAndRender 异常（疑似无限递归，已拦截）", ex);
        }
    }

    private IReadOnlyList<VideoItem> _all() => _library.Items;

    private VideoQueryOptions BuildQueryOptions() => new()
    {
        SearchText = _searchText,
        IncludedTags = _filterState?.IncludedTags,
        ExcludedTags = _filterState?.ExcludedTags,
        ActorFilter = _filterState is { Actors.Count: > 0 } f1 ? f1.Actors : null,
        Series = string.IsNullOrEmpty(_filterState?.Series) ? null : _filterState.Series,
        Studio = string.IsNullOrEmpty(_filterState?.Studio) ? null : _filterState.Studio,
        CensorType = _filterState?.CensorType,
        Resolution = string.IsNullOrEmpty(_filterState?.Resolution) ? null : _filterState.Resolution,
        Duration = _filterState?.Duration,
        FavoritesOnly = _filterState?.FavoritesOnly ?? false,
        WatchedOnly = _filterState?.WatchedOnly ?? false,
        ScrapeStatus = _filterState?.ScrapeStatus,
        SortBy = SortBox.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse(tag, out VideoSortBy sortBy) ? sortBy : VideoSortBy.AddedDesc,
    };

    private VideoSearchPanel.VideoFilterState? _filterState;

    private void SyncPanelCounts()
    {
        var version = _library.DataVersion;
        if (version == _sidebarDataVersion) { _filterState = _searchPanel?.BuildState(); return; }
        _sidebarDataVersion = version;

        // 尝试从磁盘缓存加载
        var cached = _library.LoadSidebarCounts();
        if (cached is { } c)
        {
            _cachedTagCounts = c.tags;
            _cachedActorCounts = c.actors;
            _cachedSeriesCounts = c.series;
            _cachedStudioCounts = c.studios;
        }
        else
        {
            // 缓存未命中，重新计算并保存
            _cachedTagCounts = _library.GetTagCounts();
            _cachedActorCounts = _library.GetActorCounts();
            _cachedSeriesCounts = _library.GetSeriesCounts();
            _cachedStudioCounts = _library.GetStudioCounts();
            _library.SaveSidebarCounts(_cachedTagCounts, _cachedActorCounts, _cachedSeriesCounts, _cachedStudioCounts);
        }
        _searchPanel?.SetCounts(_cachedTagCounts, _cachedActorCounts, _cachedSeriesCounts, _cachedStudioCounts);
        _filterState = _searchPanel?.BuildState();
    }

    private void RenderSidebar()
    {
        // 侧栏计数计算较重，延迟到 UI 空闲时执行，不阻塞列表渲染
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            SyncPanelCounts();
        }));
    }

    private void RenderList()
    {
        BackButton.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Collapsed;
        try
        {
            using (RecursionGuard.Enter("RenderList"))
            {
        VideoItems.Visibility = _filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _library.Items.Count > 0 ? "没有符合筛选条件的视频" : "还没有视频文件";
        EmptyAddButton.Visibility = _library.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        VideoCountText.Text = $"共 {_filtered.Count} 个";

        var pageItems = _filtered
            .Skip((_page - 1) * _pageSize)
            .Take(_pageSize)
            .ToList();

        VideoItems.ItemsSource = pageItems;
        VideoItems.ScrollToTop();
        RenderPaging();
        KickEnrichment(pageItems);
        KickEnrichment(pageItems);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("[VideoView] RenderList 异常（疑似无限递归，已拦截）", ex);
        }
    }

    private void VideoCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not VideoFileCard card) return;
        card.DetailRequested -= VideoCard_DetailRequested;
        card.SelectedChanged -= VideoCard_SelectedChanged;
        card.DetailRequested += VideoCard_DetailRequested;
        card.SelectedChanged += VideoCard_SelectedChanged;
    }

    private void VideoCard_DetailRequested(VideoItem item)
    {
        _currentItem = item;
        ApplyAndRender();
    }

    private void VideoCard_SelectedChanged(VideoItem item, bool selected)
    {
        if (selected) _selectedIds.Add(item.Id);
        else _selectedIds.Remove(item.Id);
    }

    private void RenderDetail(VideoItem item)
    {
        BackButton.Visibility = Visibility.Visible;
        VideoItems.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;
        VideoCountText.Text = item.Number;
        TitleText.Text = "详情";
        DetailNumber.Text = string.IsNullOrEmpty(item.Number) ? "(未识别番号)" : item.Number;
        DetailTitle.Text = item.DisplayTitle;
        OriginalTitleText.Text = item.OriginalTitle;
        FavoriteHeart.Text = item.IsFavorite ? "♥" : "♡";
        FavoriteHeart.Foreground = item.IsFavorite
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x6F))
            : (Brush)FindResource("TextSecondaryBrush");
        MetaText.Text = string.Join(" · ", new[]
        {
            item.Series,
            item.Studio,
            item.Publisher,
            item.Director.Length > 0 ? $"导演: {item.Director}" : "",
            item.ReleaseDate is { } date ? $"发行 {date:yyyy-MM-dd}" : "",
            item.DurationSeconds > 0 ? $"{item.DurationSeconds / 60:F0}min" : "",
            item.Score > 0 ? $"评分 {item.Score:F1}" : "",
            item.HasChineseSubtitle ? "中字" : "",
            item.Resolution,
        }.Where(s => !string.IsNullOrEmpty(s)));
        DescriptionText.Text = item.Description;

        var poster = new[] { item.PosterPath, item.CoverPath, item.ThumbnailPath }.FirstOrDefault(File.Exists);
        ImageLoader.SetSource(DetailPoster, poster);
        ActorChips.Children.Clear();
        foreach (var actor in item.Actors.Take(12)) ActorChips.Children.Add(MakeDetailChip(actor, () => ShowActor(actor, () => ApplyAndRender())));
        TagChips.Children.Clear();
        foreach (var tag in item.Tags.Concat(item.UserTags).Take(24)) TagChips.Children.Add(MakeDetailChip($"#{tag}", () => FilterByTag(tag)));

        PartsHost.Children.Clear();
        var parts = _library.Items.Where(i => !string.IsNullOrEmpty(i.Number) && i.Number.Equals(item.Number, StringComparison.OrdinalIgnoreCase)).ToList();
        PartsHost.Visibility = parts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var part in parts.OrderBy(i => i.Part))
        {
            var button = new Button { Content = string.IsNullOrEmpty(part.Part) ? "主片" : part.Part };
            button.Style = (Style)FindResource(part.Id == item.Id ? "LibraryPrimaryButtonStyle" : "LibraryActionButtonStyle");
            var captured = part;
            button.Click += (_, _) => { _currentItem = captured; ApplyAndRender(); };
            PartsHost.Children.Add(button);
        }

        RenderSeriesParts(item);
        RenderRating(item);
        RenderPreviews(item);
        RenderFileDetails(item);
        ScrapeReportPanel.LoadReport(item);
        RenderRecommends(item);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _currentItem = null; ApplyAndRender(); TitleText.Text = "本地视频";
        ScrapeReportPanel.HideReport();
    }
    private Border MakeDetailChip(string text, Action onClick)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("HoverBgBrush"),
            BorderBrush = (Brush)FindResource("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
        };
        border.Child = new TextBlock { Text = text, FontSize = 11.5 };
        border.MouseLeftButtonUp += (_, _) => onClick();
        return border;
    }

    private void RenderSeriesParts(VideoItem item)
    {
        SeriesPartsHost.Children.Clear();
        SeriesPartsHeader.Visibility = Visibility.Collapsed;
        SeriesPartsHost.Visibility = Visibility.Collapsed;
        if (item.SeriesNumbers.Count == 0) return;
        SeriesPartsHeader.Text = $"系列：{item.Series}（{item.SeriesNumbers.Count} 部）";
        SeriesPartsHeader.Visibility = Visibility.Visible;
        SeriesPartsHost.Visibility = Visibility.Visible;
        foreach (var number in item.SeriesNumbers.Take(20))
        {
            var local = _library.Items.FirstOrDefault(i => i.Number.Equals(number, StringComparison.OrdinalIgnoreCase));
            var button = new Button
            {
                Content = number,
                IsEnabled = local is not null,
                Style = (Style)FindResource(local?.Id == item.Id ? "LibraryPrimaryButtonStyle" : "LibraryActionButtonStyle"),
                Margin = new Thickness(0, 0, 6, 0),
            };
            var captured = local;
            button.Click += (_, _) => { if (captured is not null) { _currentItem = captured; ApplyAndRender(); } };
            SeriesPartsHost.Children.Add(button);
        }
    }

    private void RenderRating(VideoItem item)
    {
        RatingHost.Children.Clear();
        for (var i = 1; i <= 5; i++)
        {
            var star = new TextBlock
            {
                Text = i <= item.UserRating ? "★" : "☆",
                FontSize = 20,
                Cursor = Cursors.Hand,
                Foreground = i <= item.UserRating ? (Brush)FindResource("WarningBrush") : (Brush)FindResource("TextSecondaryBrush"),
            };
            var value = i;
            star.MouseLeftButtonUp += (_, _) =>
            {
                item.UserRating = value == item.UserRating ? value - 1 : value;
                _library.Update(item);
                ApplyAndRender();
            };
            RatingHost.Children.Add(star);
        }
    }

    private void RenderPreviews(VideoItem item)
    {
        PreviewsHost.Children.Clear();
        var images = item.PreviewImages.Where(File.Exists).Take(10).ToList();
        PreviewHeader.Visibility = images.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var image in images)
        {
            var img = new Image { Width = 180, Height = 120, Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 8, 0) };
            ImageLoader.SetSource(img, image);
            PreviewsHost.Children.Add(img);
        }
    }

    private void RenderFileDetails(VideoItem item)
    {
        FilePathText.Text = item.FilePath;
        FileSizeText.Text = FormatFileSize(item.FileSizeBytes);
        DateTime? modifiedAt = item.FileExists ? File.GetLastWriteTime(item.FilePath) : null;
        FileModifiedText.Text = modifiedAt is { } time ? time.ToString("yyyy-MM-dd HH:mm") : "未知";
        WatchStatsText.Text = item.WatchCount > 0
            ? $"{item.WatchCount} 次 · 最近 {FormatLocalTime(item.LastWatchedAt)}"
            : "未观看";
        OpenStatsText.Text = item.OpenCount > 0
            ? $"{item.OpenCount} 次 · 最近 {FormatLocalTime(item.LastOpenedAt)}"
            : "未打开";
        var directory = Path.GetDirectoryName(item.FilePath) ?? "";
        DirectoryPathText.Text = string.IsNullOrEmpty(directory) ? "未知" : directory;
        var nfoPath = string.IsNullOrEmpty(item.FilePath) ? "" : Path.ChangeExtension(item.FilePath, ".nfo");
        NfoStatusText.Text = File.Exists(nfoPath) ? "已生成" : "未生成";
        FileStatusText.Text = item.FileExists ? "正常" : "丢失";
        FileStatusText.Foreground = item.FileExists
            ? (Brush)FindResource("TextPrimaryBrush")
            : (Brush)FindResource("DangerBrush");
        SourceUrlText.Text = item.SourceUrls.TryGetValue("javbus", out var javBus)
            ? javBus
            : item.SourceUrls.TryGetValue("javdb", out var javDb) ? javDb : "";
        SourceUrlText.Visibility = string.IsNullOrEmpty(SourceUrlText.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderRecommends(VideoItem item)
    {
        SeriesRecommends.Children.Clear();
        var sameSeries = _library.Items.Where(i => i.Id != item.Id && !string.IsNullOrEmpty(item.Series) && i.Series.Equals(item.Series, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
        SeriesRecommendHeader.Visibility = sameSeries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var rec in sameSeries)
        {
            var card = new VideoPosterCard();
            card.Bind(rec);
            card.DetailRequested += detail => Dispatcher.Invoke(() => { _currentItem = detail; ApplyAndRender(); });
            SeriesRecommends.Children.Add(card);
        }

        ActorRecommends.Children.Clear();
        var sameActors = _library.Items.Where(i => i.Id != item.Id && i.Actors.Any(a => item.Actors.Contains(a, StringComparer.OrdinalIgnoreCase))).Take(10).ToList();
        ActorRecommendHeader.Visibility = sameActors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var rec in sameActors)
        {
            var card = new VideoPosterCard();
            card.Bind(rec);
            card.DetailRequested += detail => Dispatcher.Invoke(() => { _currentItem = detail; ApplyAndRender(); });
            ActorRecommends.Children.Add(card);
        }

        RelatedRecommends.Children.Clear();
        var relatedLocal = item.RelatedNumbers
            .Select(num => _library.Items.FirstOrDefault(i => i.Number.Equals(num, StringComparison.OrdinalIgnoreCase)))
            .Where(i => i is not null && i.Id != item.Id)
            .Take(10).ToList();
        RelatedRecommendHeader.Visibility = relatedLocal.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var rec in relatedLocal.Cast<VideoItem>())
        {
            var card = new VideoPosterCard();
            card.Bind(rec);
            card.DetailRequested += detail => Dispatcher.Invoke(() => { _currentItem = detail; ApplyAndRender(); });
            RelatedRecommends.Children.Add(card);
        }
    }

    private static string FormatLocalTime(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "";
        return DateTime.TryParse(iso, out var t) ? t.ToString("yyyy-MM-dd HH:mm") : iso;
    }

    private void CopySourceUrl_Click(object sender, MouseButtonEventArgs e)
    {
        if (_currentItem is not { } item || SourceUrlText.Visibility != Visibility.Visible) return;
        Clipboard.SetText(SourceUrlText.Text);
        ToastService.Show("已复制来源详情页", ToastKind.Success);
    }

    private void FilterByActor(string actor)
    {
        _currentItem = null;
        _searchPanel?.AddActorFilter(actor);
        ApplyAndRender();
    }

    private void FilterByTag(string tag)
    {
        _currentItem = null;
        _searchPanel?.AddTagFilter(tag);
        ApplyAndRender();
    }

    private void DetailFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem is not { } item) return;
        _library.ToggleFavorite(item.Id);
        ApplyAndRender();
    }

    private void PlayDetail_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem is not { FileExists: true } item)
        {
            ToastService.Show("文件不存在或已被移动", ToastKind.Error);
            return;
        }
        Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
        _library.RecordWatch(item.Id);
        ApplyAndRender();
    }

    private void DetailEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem is not { } item) return;
        var dialog = new VideoEditDialog(ConvertToFolder(item)) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            ApplyEditedFolder(item, dialog.Folder);
            _library.Update(item);
            ToastService.Show("已保存修改", ToastKind.Success);
            Refresh();
        }
    }

    private static VideoFolder ConvertToFolder(VideoItem item) => new()
    {
        Name = item.DisplayTitle,
        Series = item.Series,
        Director = item.Director,
        Description = item.Description,
        Tags = item.Tags,
        Actors = item.Actors,
        VoiceActors = [],
        ProductionTeam = [item.Studio],
        Rating = item.UserRating,
    };

    private static void ApplyEditedFolder(VideoItem item, VideoFolder folder)
    {
        item.Title = folder.Name;
        item.Series = folder.Series;
        item.Director = folder.Director;
        item.Description = folder.Description;
        item.Tags = folder.Tags;
        item.Actors = folder.Actors;
        item.UserRating = folder.Rating;
        if (folder.ProductionTeam.Count > 0)
        {
            item.Studio = folder.ProductionTeam[0];
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem is not { } item) return;
        var dir = Path.GetDirectoryName(item.FilePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        Process.Start("explorer.exe", dir);
        _library.RecordFolderOpen(item.Id);
        ApplyAndRender();
    }

    private void RemoveDetail_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem is not { } item) return;
        if (MessageBox.Show(Window.GetWindow(this), "确定从库中移除该记录吗？磁盘文件不会被删除。", "移除确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _library.Remove(item.Id);
        _currentItem = null;
        ToastService.Show("已移除记录", ToastKind.Info);
        Refresh();
    }

    private void OnlineSearch_Submitted(object? sender, string e)
    {
        _onlineSearchText = e.Trim();
        ExecuteOnlineSearch();
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        _onlineSearchText = OnlineSearchBox.Text.Trim();
        ExecuteOnlineSearch();
    }

    private async void ExecuteOnlineSearch()
    {
        if (string.IsNullOrWhiteSpace(_onlineSearchText))
        {
            OnlineEmptyHint.Text = "请输入搜索关键词";
            return;
        }
        var source = OnlineSource;
        if (source is null) { OnlineEmptyHint.Text = "在线源未注册"; return; }

        _onlineSearchCts?.Cancel();
        _onlineSearchCts = new CancellationTokenSource();
        var ct = _onlineSearchCts.Token;
        var version = ++_onlineSearchVersion;

        OnlineLoadingState.Visibility = Visibility.Visible;
        OnlineEmptyState.Visibility = Visibility.Collapsed;
        ResultsScroll.Visibility = Visibility.Collapsed;
        try
        {
            var result = await source.SearchAsync(_onlineSearchText, _onlinePage, ct);
            if (version != _onlineSearchVersion || ct.IsCancellationRequested) return;
            RenderOnlineResults(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _onlineSearchVersion)
            {
                OnlineLoadingState.Visibility = Visibility.Collapsed;
                OnlineEmptyState.Visibility = Visibility.Visible;
                OnlineEmptyHint.Text = $"搜索失败: {ex.Message}";
            }
        }
    }

    private void RenderOnlineResults(OnlineVideoSearchResult result)
    {
        OnlineLoadingState.Visibility = Visibility.Collapsed;
        ResultCardsPanel.Children.Clear();
        foreach (var item in result.Items.Take(30))
        {
            var border = new Border
            {
                Width = 170, Margin = new Thickness(0, 0, 10, 10),
                CornerRadius = new CornerRadius(8),
                Background = (Brush)FindResource("HoverBgBrush"),
                Cursor = Cursors.Hand,
            };
            var stack = new StackPanel();
            if (!string.IsNullOrEmpty(item.CoverUrl))
            {
                var img = new Image { Height = 96, Stretch = Stretch.UniformToFill, Margin = new Thickness(6,6,6,4) };
                    img.Loaded += (_, _) => Common.ImageLoader.SetSource(img, item.CoverUrl);
                stack.Children.Add(img);
            }
            stack.Children.Add(new TextBlock { Text = item.Title, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 34, Margin = new Thickness(8,2,8,8) });
            border.Child = stack;
            ResultCardsPanel.Children.Add(border);
        }
        ResultsScroll.Visibility = result.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OnlineEmptyState.Visibility = result.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        OnlineEmptyHint.Text = result.Items.Count > 0 ? "" : "没有找到匹配的视频";
        PageInfoText.Text = $"第 {_onlinePage} 页 · {result.Items.Count} 条";
        OnlinePagingHost.Visibility = result.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_onlinePage > 1) { _onlinePage--; await Task.CompletedTask; ExecuteOnlineSearch(); }
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        _onlinePage++; await Task.CompletedTask; ExecuteOnlineSearch();
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsReady && _currentNav == "local") ApplyAndRender();
    }

    private void KickEnrichment(IEnumerable<VideoItem> items)
    {
        var pending = items.Where(i => !_enrichQueued.Contains(i.FilePath) && i.FileExists && (i.DurationSeconds <= 0 || string.IsNullOrEmpty(i.Resolution) || string.IsNullOrEmpty(i.ThumbnailPath))).ToList();
        if (pending.Count == 0) return;
        foreach (var item in pending) _enrichQueued.Add(item.FilePath);
        try
        {
            using (RecursionGuard.Enter("KickEnrichment"))
            {
        _enrichCts?.Cancel();
        _enrichCts = new CancellationTokenSource();
        var ct = _enrichCts.Token;
        _ = Task.Run(async () =>
        {
            foreach (var item in pending)
            {
                ct.ThrowIfCancellationRequested();
                if (item.DurationSeconds <= 0 || string.IsNullOrEmpty(item.Resolution))
                {
                    var meta = await Task.Run(() => VideoMetadataReader.Read(item.FilePath), ct);
                    if (meta is { } m)
                    {
                        item.DurationSeconds = item.DurationSeconds <= 0 ? m.DurationSeconds : item.DurationSeconds;
                        item.Resolution = m.Width > 0 && m.Height > 0 ? $"{m.Width}x{m.Height}" : item.Resolution;
                    }
                }
                if (string.IsNullOrEmpty(item.ThumbnailPath) || !File.Exists(item.ThumbnailPath))
                {
                    item.ThumbnailPath = await VideoThumbnailService.GenerateAsync(item.FilePath, ct: ct);
                }
                _library.Update(item);
                Dispatcher.BeginInvoke(new Action(() => ApplyAndRender()));
            }
        }, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("[VideoView] KickEnrichment 异常（疑似无限递归，已拦截）", ex);
    }
        }

    private void OnThumbnailSaved(string path) => Dispatcher.Invoke(() =>
    {
        if (_currentItem is not null && _currentItem.FilePath == path) ApplyAndRender();
    });

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }

    private static string FormatLocalTime(DateTime? value)
        => value is { } time ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "未知";

    private void CopyFilePath_Click(object sender, MouseButtonEventArgs e)
    {
        if (_currentItem is not { } item || string.IsNullOrEmpty(item.FilePath)) return;
        Clipboard.SetText(item.FilePath);
        ToastService.Show("已复制文件路径", ToastKind.Success);
    }

    private void OpenDetailDirectory_Click(object sender, MouseButtonEventArgs e)
    {
        if (_currentItem is not { } item) return;
        var dir = Path.GetDirectoryName(item.FilePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            ToastService.Show("目录不存在", ToastKind.Error);
            return;
        }
        Process.Start("explorer.exe", dir);
        _library.RecordFolderOpen(item.Id);
        ApplyAndRender();
    }

    private void ScrapeDetail_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem is not { } item) return;
        StartScrape([item.Id], autoStarted: false);
    }

    private void RetryUnsuccessful_Click(object sender, RoutedEventArgs e)
        => RetryByStatus([ScrapeStatus.Pending, ScrapeStatus.NoMatch, ScrapeStatus.Failed], "重试未成功");

    private void RetryFailed_Click(object sender, RoutedEventArgs e)
        => RetryByStatus([ScrapeStatus.Failed], "重试失败");

    private void RetryByStatus(ScrapeStatus[] statuses, string title)
    {
        var ids = _library.Items
            .Where(item => statuses.Contains(item.ScrapeStatus))
            .Where(item => item.FileExists)
            .Select(item => item.Id)
            .ToList();
        if (ids.Count == 0)
        {
            ToastService.Show("没有需要重试的记录", ToastKind.Info);
            return;
        }
        StartScrape(ids, autoStarted: false, title);
    }

    private void ViewNoMatch_Click(object sender, RoutedEventArgs e)
    {
        _currentItem = null;
        var state = _searchPanel?.BuildState();
        _searchPanel?.ApplyState(new VideoSearchPanel.VideoFilterState(
            state?.Keyword ?? "",
            state?.IncludedTags ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            state?.ExcludedTags ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            state?.Actors ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            state?.Series ?? "",
            state?.Studio ?? "",
            state?.CensorType,
            state?.Resolution ?? "",
            state?.Duration,
            ScrapeStatus.NoMatch,
            false,
            false));
        Refresh();
    }

    private void StartScrape(IEnumerable<string> ids, bool autoStarted, string title = "刮削中")
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;
        _logger.Info($"[VideoView] 启动刮削任务: {idList.Count} 条 ({title})");
        _scrapeCts?.Cancel();
        _scrapeCts = new CancellationTokenSource();
        var ct = _scrapeCts.Token;
        _scrapeRunning = true;
        StopButton.IsEnabled = true;
        TaskTitle.Text = autoStarted ? "自动刮削中" : title;
        ViewNoMatchButton.Visibility = Visibility.Collapsed;
        RetryFailedButton.Visibility = Visibility.Collapsed;
        TaskLogList.ItemsSource = null;
        foreach (var id in idList) { var vItem = _library.GetById(id); _taskQueue?.Enqueue(vItem?.Number ?? id); }
        _ = Task.Run(async () =>
        {
            try
            {
                await _scrapeService.ScrapeAsync(idList, progress =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        TaskProgress.Value = progress.Total == 0 ? 100 : progress.Completed * 100.0 / progress.Total;
                        TaskSummary.Text = $"{progress.Completed}/{progress.Total} 成功{progress.SuccessCount} 未匹配{progress.NoMatchCount} 失败{progress.FailedCount}";
                        TaskLogList.ItemsSource = progress.Logs.ToArray();
                        ViewNoMatchButton.Visibility = progress.NoMatchCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                        RetryFailedButton.Visibility = progress.FailedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                    });
                }, ct);
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() => ToastService.Show("刮削已停止", ToastKind.Info));
            }
            catch (Exception ex)
            {
                _logger.Error("[VideoView] 刮削任务异常", ex);
                Dispatcher.Invoke(() => ToastService.ShowError(ex, "刮削失败："));
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    _scrapeRunning = false;
                    TaskTitle.Text = "已完成";
                    StopButton.IsEnabled = false;
                });
            }
        }, ct);
    }


    private async Task ProcessQueueTaskAsync(VideoScrapeTask task, CancellationToken ct)
    {
        // TaskBar 的 ScrapeAsync 已批量处理，此处仅等其完成后更新状态
        await Task.CompletedTask;
    }

    private void ToggleLogs_Click(object sender, RoutedEventArgs e)
        => TaskLogList.Visibility = TaskLogList.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void StopScrape_Click(object sender, RoutedEventArgs e) => _scrapeCts?.Cancel();

    private void ToggleSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_selectionMode && _selectedIds.Count > 0)
        {
            _selectedIds.Clear();
        }
        _selectionMode = !_selectionMode;
        ApplyAndRender();
    }

    private void SelectPage_Click(object sender, RoutedEventArgs e)
    {
        var ids = CurrentPageItems().Select(i => i.Id).ToList();
        if (ids.All(_selectedIds.Contains)) foreach (var id in ids) _selectedIds.Remove(id);
        else foreach (var id in ids) _selectedIds.Add(id);
        ApplyAndRender();
    }

    private List<VideoItem> CurrentPageItems()
        => _filtered.Skip((_page - 1) * _pageSize).Take(_pageSize).ToList();

    private void RenderPaging()
    {
        _pageCount = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)_pageSize));
        _page = Math.Clamp(_page, 1, _pageCount);
        PageText.Text = _filtered.Count == 0 ? "" : $"第 {_page} / {_pageCount} 页";
        PrevPageButton.IsEnabled = _page > 1;
        NextPageButton.IsEnabled = _page < _pageCount;
    }

    private void PageSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageSizeBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var size) && size != _pageSize)
        {
            _pageSize = size;
            if (IsReady)
            {
                _page = 1;
                ApplyAndRender();
            }
        }
    }

    private void ChangePage(object sender, RoutedEventArgs e)
    {
        if (sender == PrevPageButton && _page > 1) _page--;
        else if (sender == NextPageButton) _page++;
        else if (sender == PrevPageButton) _page--;
        else if (sender == NextPageButton) _page++;
        else _page = _pageCount;
        ApplyAndRender();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var ids = CurrentPageItems().Select(i => i.Id).ToList();
        if (ids.All(_selectedIds.Contains)) foreach (var id in ids) _selectedIds.Remove(id);
        else foreach (var id in ids) _selectedIds.Add(id);
        ApplyAndRender();
    }

    private async void BatchFavorite_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() => _library.SetFavorites(_selectedIds.ToList(), true));
        ToastService.Show($"已收藏 {_selectedIds.Count} 个视频", ToastKind.Success);
        ApplyAndRender();
    }

    private async void BatchUnfavorite_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() => _library.SetFavorites(_selectedIds.ToList(), false));
        ToastService.Show($"已取消收藏 {_selectedIds.Count} 个视频", ToastKind.Info);
        ApplyAndRender();
    }

    private void BatchScrape_Click(object sender, RoutedEventArgs e)
    {
        StartScrape(_selectedIds.ToList(), autoStarted: false, "批量刮削中");
    }

    private async void BatchRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIds.Count == 0) return;
        var result = MessageBox.Show(Window.GetWindow(this), $"确定从库中移除选中的 {_selectedIds.Count} 条记录吗？\n磁盘文件不会被删除。", "批量移除", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        var count = await Task.Run(() => _library.RemoveMany(_selectedIds.ToList()));
        _selectedIds.Clear();
        _currentItem = null;
        ToastService.Show($"已移除 {count} 条记录", ToastKind.Info);
        Refresh();
    }

    private void ManageRoots_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new VideoRootsDialog(_library) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
        Refresh();
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择视频文件夹" };
        if (dialog.ShowDialog() != true || !Directory.Exists(dialog.FolderName)) return;
        try
        {
            var added = await Task.Run(() => _library.AddFolder(dialog.FolderName));
            ToastService.Show($"已添加 {added.Count} 个视频", ToastKind.Success);
            Refresh();
        }
        catch (Exception ex) { ToastService.Show($"添加失败: {ex.Message}", ToastKind.Error); }
    }

    private async void RescanRoots_Click(object sender, RoutedEventArgs e)
    {
        var roots = _library.RootFolders;
        if (roots.Count == 0)
        {
            ToastService.Show("还没有已保存的视频根目录", ToastKind.Info);
            return;
        }
        RescanButton.IsEnabled = false;
        try
        {
            var pendingIds = await Task.Run(() =>
                roots.SelectMany(path => _library.Rescan(path))
                    .Select(item => item.Id)
                    .Distinct()
                    .ToList());
            ToastService.Show("重新扫描完成", ToastKind.Success);
            Refresh();
            if (pendingIds.Count > 0) StartScrape(pendingIds, autoStarted: true);
        }
        catch (Exception ex)
        {
            _logger.Error("[VideoView] 重新扫描根目录失败", ex);
            ToastService.ShowError(ex, "重新扫描失败：");
        }
        finally
        {
            RescanButton.IsEnabled = true;
        }
    }

}