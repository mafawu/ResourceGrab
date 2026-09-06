using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Net.Http;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Dialogs;
using ResourceGrab.App.Services;
using ResourceGrab.App.Common;
using ResourceGrab.App.ViewModels;
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
    private string _activeTaskId = "";
    private VideoTaskStatus _lastTaskStatus;
    // 重扫根目录期间复用 TaskBar 显示扫描进度；_rescanBusy 防止刮削事件覆盖扫描文案。
    private bool _rescanBusy;
    private CancellationTokenSource? _rescanCts;
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
    private string _onlineSearchText = "";
    private int _onlinePage = 1;
    private int _onlineLastPage = 1;
    private CancellationTokenSource? _onlineSearchCts;
    private int _onlineSearchVersion;
    /// <summary>在线搜索卡片宽度：按结果区宽度与每行个数换算（与本地结果同一套规则），默认 170。</summary>
    private double _onlineCardWidth = 170;

    /// <summary>单次搜索自动续抓的页数上限。MissAV 服务端每页仅返回约 12 条，需合并多页才够一屏。</summary>
    private const int OnlineAutoMergePages = 3;
    /// <summary>单次搜索最多上屏的条数。</summary>
    private const int OnlineMaxResults = 60;
    private int _onlineRenderedCount;
    private VideoSearchPanel? _searchPanel;
    private int _sidebarDataVersion = -1;
    private Action? _actorClose;
    private Dictionary<string, int> _cachedTagCounts = new();
    private Dictionary<string, int> _cachedActorCounts = new();
    private Dictionary<string, int> _cachedSeriesCounts = new();
    private Dictionary<string, int> _cachedStudioCounts = new();
    /// <summary>当前选中的在线搜索源 Id（源 Tab 切换，默认 MissAV）。</summary>
    private string _selectedOnlineSourceId = "missav";

    private IVideoSource? OnlineSource => App.Services.GetServices<IVideoSource>()
        .FirstOrDefault(s => s.Info.Id == _selectedOnlineSourceId);

    public VideoView()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        InitializeComponent();
        _library = App.Services.GetRequiredService<VideoLibraryService>();
        _scrapeService = App.Services.GetRequiredService<VideoScrapeService>();
        _logger = App.Services.GetRequiredService<ILogger>();
        _logger.Info($"[VideoView] InitializeComponent 耗时 {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        _chipBorderStyle = (Style)FindResource("VideoChipStyle");
        _chipTextStyle = (Style)FindResource("VideoChipTextStyle");
        _chipCountStyle = (Style)FindResource("VideoChipCountStyle");
        _taskQueue = App.Services.GetService(typeof(VideoScrapeTaskQueue)) as VideoScrapeTaskQueue;
        ResultsScroll.SizeChanged += (_, _) => UpdateOnlineCardWidth();
        BuildLocalToolbar();
        _logger.Info($"[VideoView] 构造函数其余部分耗时 {sw.ElapsedMilliseconds} ms");
        Loaded += OnLoaded;
        Unloaded += (_, _) => { _enrichCts?.Cancel(); _onlineEnrichCts?.Cancel(); if (_taskQueue != null) _taskQueue.ProgressChanged -= OnQueueProgressChanged; };
        VideoThumbnailService.ThumbnailSaved += OnThumbnailSaved;
        Unloaded += (_, _) => VideoThumbnailService.ThumbnailSaved -= OnThumbnailSaved;
    }

    /// <summary>本地页计数徽标（BrowserToolbar 内容在代码后置组装，代码后置需要写它的 Text）。</summary>
    private readonly TextBlock _videoCountText = new() { FontSize = 11 };
    private ComboBox? _sortBox;
    private Button _rescanButton = null!;

    /// <summary>组装本地页统一工具栏：标题 + 计数 + 排序 + 重新扫描/添加文件夹。</summary>
    private void BuildLocalToolbar()
    {
        var titleText = new TextBlock
        {
            Text = "本地视频",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var countBadge = new Border
        {
            Style = (Style)FindResource("CountBadgeStyle"),
            Margin = new Thickness(8, 0, 0, 0),
            Child = _videoCountText,
        };
        LocalToolbar.TitleContent.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { titleText, countBadge },
        };

        _sortBox = new ComboBox
        {
            Style = (Style)FindResource("LibrarySortComboBoxStyle"),
            SelectedIndex = 0,
        };
        foreach (var (label, tag) in new[]
        {
            ("最近添加", "AddedDesc"), ("最早添加", "AddedAsc"), ("标题 A → Z", "TitleAsc"),
            ("发行日期 ↓", "ReleaseDateDesc"), ("评分最高", "ScoreDesc"), ("我的评分", "UserRatingDesc"),
        })
        {
            _sortBox.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        }
        _sortBox.SelectionChanged += SortBox_SelectionChanged;
        LocalToolbar.SortContent.Content = _sortBox;

        var rescanButton = _rescanButton = new Button
        {
            Style = (Style)FindResource("LibraryActionButtonStyle"),
            ToolTip = "重新扫描",
            Margin = new Thickness(8, 0, 0, 0),
            Content = new TextBlock { Text = "重新扫描", FontSize = 12, FontWeight = FontWeights.SemiBold },
        };
        rescanButton.Click += RescanRoots_Click;
        var addFolderButton = new Button
        {
            Style = (Style)FindResource("LibraryPrimaryButtonStyle"),
            Content = "添加文件夹",
            Margin = new Thickness(8, 0, 0, 0),
        };
        addFolderButton.Click += AddFolder_Click;
        LocalToolbar.Actions.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { rescanButton, addFolderButton },
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_taskQueue != null) _taskQueue.ProgressChanged += OnQueueProgressChanged;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Refresh();
        _logger.Info($"[VideoView] OnLoaded Refresh 耗时 {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>每行个数（Columns）或视图尺寸变化时，与本地虚拟网格一起换算在线卡片宽度。</summary>
    protected override void UpdateCellSize()
    {
        base.UpdateCellSize();
        UpdateOnlineCardWidth();
    }

    /// <summary>本地详情栏开关：true=详情打开（主窗口应收起右侧本地搜索面板），false=详情关闭（恢复面板）。</summary>
    public event Action<bool>? DetailPanelToggled;

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

    /// <summary>是否已经显示过（首次进入默认在线搜索并隐藏右侧筛选栏；非首次保留原子页状态）。</summary>
    private bool _shownOnce;

    public void OnShown()
    {
        BuildVideoSourceTabs();
        // 仅首次进入时落到默认子页（在线搜索 + 隐藏侧栏）；之后切回视频页保留上次状态，
        // 由 MainWindow 的 ShellNavigator.RestoreLast 恢复最近路由。
        if (!_shownOnce)
        {
            _shownOnce = true;
            if (_currentNav != "search") SwitchNav("search");
            else DetailPanelToggled?.Invoke(true);
        }
    }

    private void NavSearch_Click(object sender, RoutedEventArgs e) => SwitchNav("search");
    private void NavRecommend_Click(object sender, RoutedEventArgs e) => SwitchNav("recommend");
    private void NavLocal_Click(object sender, RoutedEventArgs e) => SwitchNav("local");

    /// <summary>构建在线搜索源切换 Tab（MissAV / JavDB…）。单选，切换即清空结果区并重新搜索。</summary>
    private void BuildVideoSourceTabs()
    {
        if (OnlineToolbar.Chips is null) return;
        OnlineToolbar.Chips.Children.Clear();
        var sources = App.Services.GetServices<IVideoSource>().ToList();
        if (sources.Count == 0) return;
        // 当前选中的源已不可用（如配置变更）时回退到第一个
        if (sources.All(s => s.Info.Id != _selectedOnlineSourceId))
            _selectedOnlineSourceId = sources[0].Info.Id;

        foreach (var source in sources)
        {
            var tab = new System.Windows.Controls.RadioButton
            {
                Style = (Style)FindResource("FilterTabStyle"),
                Content = source.Info.DisplayName,
                Tag = source.Info.Id,
                IsChecked = source.Info.Id == _selectedOnlineSourceId,
                Margin = new Thickness(0, 0, 6, 6),
            };
            tab.Click += OnlineSourceTab_Click;
            OnlineToolbar.Chips.Children.Add(tab);
        }
    }

    private void OnlineSourceTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton { Tag: string id } || id == _selectedOnlineSourceId) return;
        _selectedOnlineSourceId = id;
        // 换源：取消在途搜索/详情回填，清空结果区，回到空状态
        _onlineSearchCts?.Cancel();
        _onlineEnrichCts?.Cancel();
        _onlineSearchVersion++;
        _onlinePage = 1;
        _onlineLastPage = 1;
        ResultCardsPanel.Children.Clear();
        _onlineRenderedCount = 0;
        ClosePopoutIfOpen();
        OnlinePreviewPlayer.Stop();
        CloseOnlineFullDetail();
        SetOnlineDetailVisible(false);
        ResultsScroll.Visibility = Visibility.Collapsed;
        OnlineEmptyState.Visibility = Visibility.Visible;
        OnlineEmptyHint.Text = "输入关键词开始在线搜索";
        UpdateOnlinePagingText();
        // 已有搜索词时立即用新源重新搜
        if (!string.IsNullOrWhiteSpace(_onlineSearchText))
            ExecuteOnlineSearch();
    }

    public void SwitchNav(string nav)
    {
        _currentNav = nav;
        SearchPage.Visibility = nav == "search" ? Visibility.Visible : Visibility.Collapsed;
        RecommendPage.Visibility = nav == "recommend" ? Visibility.Visible : Visibility.Collapsed;
        LocalPage.Visibility = nav == "local" ? Visibility.Visible : Visibility.Collapsed;
        TaskPage.Visibility = nav == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        ActorListPage.Visibility = nav == "actors" ? Visibility.Visible : Visibility.Collapsed;
        // 有进行中的刮削任务或目录扫描时保留底部进度条，避免切页后进度不可见。
        if (!HasActiveScrapeTask && !_rescanBusy) TaskBar.Visibility = Visibility.Collapsed;
        ActorPage.Visibility = Visibility.Collapsed;

        // 切到其他子页时收起详情栏，恢复右侧本地搜索面板
        if (nav != "local" && _currentItem is not null) CloseDetail();
        // 在线详情侧栏与完整详情页随切页一并收起，恢复右侧筛选面板
        if (nav != "search" && (OnlineDetailDrawer.Visibility == Visibility.Visible || OnlineFullDetailPage.Visibility == Visibility.Visible))
        {
            _onlineDetailVersion++;
            _onlineFullDetailVersion++;
            _onlinePendingAutoPlay = false;
            ClosePopoutIfOpen();
            OnlinePreviewPlayer.Stop();
            CloseOnlineFullDetail();
            SetOnlineDetailVisible(false);
            DetailPanelToggled?.Invoke(false);
        }

        // 搜索页不使用本地筛选侧栏：进入搜索页自动隐藏，切回其他子页时恢复
        DetailPanelToggled?.Invoke(nav == "search");

        if (nav == "local") Refresh();
        if (nav == "actors") ActorListPage.Refresh();
    }

    private bool HasActiveScrapeTask =>
        _taskQueue?.GetTask(_activeTaskId) is { } task
        && task.Status is VideoTaskStatus.Pending or VideoTaskStatus.WaitingRetry or VideoTaskStatus.Running;

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
        ExcludedActors = _filterState is { ExcludedActors.Count: > 0 } f2 ? f2.ExcludedActors : null,
        Series = _filterState is { Series.Count: > 0 } f3 ? f3.Series : null,
        Studio = _filterState is { Studio.Count: > 0 } f4 ? f4.Studio : null,
        ExcludedSeries = _filterState is { ExcludedSeries.Count: > 0 } f5 ? f5.ExcludedSeries : null,
        ExcludedStudio = _filterState is { ExcludedStudio.Count: > 0 } f6 ? f6.ExcludedStudio : null,
        ExcludedResolution = _filterState is { ExcludedResolution.Count: > 0 } f7 ? f7.ExcludedResolution : null,
        ExcludedDuration = _filterState is { ExcludedDuration.Count: > 0 } f8 ? f8.ExcludedDuration : null,
        ExcludeFavorites = _filterState?.ExcludeFavorites ?? false,
        ExcludeWatched = _filterState?.ExcludeWatched ?? false,
        ExcludedScrapeStatus = _filterState is { ExcludedScrapeStatus.Count: > 0 } f9 ? f9.ExcludedScrapeStatus : null,
        CensorType = _filterState?.CensorType,
        Resolution = _filterState is { Resolution.Count: > 0 } f10 ? f10.Resolution : null,
        Duration = _filterState is { Duration.Count: > 0 } f11 ? f11.Duration : null,
        FavoritesOnly = _filterState?.FavoritesOnly ?? false,
        WatchedOnly = _filterState?.WatchedOnly ?? false,
        ScrapeStatus = _filterState is { ScrapeStatus.Count: > 0 } f12 ? f12.ScrapeStatus : null,
        SortBy = _sortBox?.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse(tag, out VideoSortBy sortBy) ? sortBy : VideoSortBy.AddedDesc,
    };

    private VideoSearchPanel.VideoFilterState? _filterState;
    /// <summary>上次渲染的页面条目 Id 序列，用于判断内容是否变化（不变则保留滚动位置）。</summary>
    private List<string>? _lastRenderedPageIds;

    private void SyncPanelCounts()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
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
        _logger.Info($"[VideoView] SyncPanelCounts 耗时 {sw.ElapsedMilliseconds} ms");
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
        DetailScroll.Visibility = Visibility.Collapsed;
        // 仅本地子页渲染列表时才恢复右侧筛选栏；搜索/任务等其他子页的右栏状态由 SwitchNav 统一管理。
        // 若无条件触发，首次进入视频页时 OnLoaded → Refresh → RenderList 会把已收起的右栏重新显示出来。
        if (_currentNav == "local") DetailPanelToggled?.Invoke(false);
        try
        {
            using (RecursionGuard.Enter("RenderList"))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                VideoItems.Visibility = _filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _library.Items.Count > 0 ? "没有符合筛选条件的视频" : "还没有视频文件";
        EmptyAddButton.Visibility = _library.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        _videoCountText.Text = $"共 {_filtered.Count} 个";

        var pageItems = _filtered
            .Skip((_page - 1) * _pageSize)
            .Take(_pageSize)
            .ToList();

        // 内容没变（如后台 enrichment 刷新卡片数据）时不回到顶部，保留滚动位置；
        // 只有筛选/翻页真正改变了列表内容才滚到顶。
        var pageIds = pageItems.Select(i => i.Id).ToList();
        var unchanged = _lastRenderedPageIds is not null
            && pageIds.Count == _lastRenderedPageIds.Count
            && pageIds.SequenceEqual(_lastRenderedPageIds);
        _lastRenderedPageIds = pageIds;
        var savedOffset = unchanged ? VideoItems.VerticalOffset : 0;

        VideoItems.ItemsSource = pageItems
            .Select(i => new VideoCardAdapter(i, VideoCard_DetailRequested, VideoCard_SelectedChanged)
            {
                IsSelectable = _selectionMode,
                IsSelected = _selectedIds.Contains(i.Id),
            })
            .ToList();
        if (savedOffset > 0) VideoItems.RestoreOffset(savedOffset);
        else VideoItems.ScrollToTop();
        RenderPaging();
        KickEnrichment(pageItems);
        _logger.Info($"[VideoView] RenderList 耗时 {sw.ElapsedMilliseconds} ms ({pageItems.Count} 张卡片)");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("[VideoView] RenderList 异常（疑似无限递归，已拦截）", ex);
        }
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
        DetailScroll.Visibility = Visibility.Visible;
        DetailPanelToggled?.Invoke(true);
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
        foreach (var actor in item.Actors.Take(12)) ActorChips.Children.Add(MakeDetailChip(actor, () => ShowActor(actor, () => ApplyAndRender()), () => ExcludeActor(actor)));
        TagChips.Children.Clear();
        foreach (var tag in item.Tags.Concat(item.UserTags).Take(24)) TagChips.Children.Add(MakeDetailChip($"#{tag}", () => FilterByTag(tag), () => ExcludeTag(tag)));

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

    /// <summary>关闭右侧详情栏，恢复本地搜索面板。</summary>
    private void CloseDetail()
    {
        if (_currentItem is null && DetailScroll.Visibility == Visibility.Collapsed) return;
        _currentItem = null;
        DetailScroll.Visibility = Visibility.Collapsed;
        ScrapeReportPanel.HideReport();
        DetailPanelToggled?.Invoke(false);
    }

    private void DetailClose_Click(object sender, RoutedEventArgs e) => CloseDetail();
    private Border MakeDetailChip(string text, Action onClick, Action? onRightClick = null, string? toolTip = null)
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
            ToolTip = toolTip ?? (onRightClick is not null ? "左键筛选，右键排除" : null),
        };
        border.Child = new TextBlock { Text = text, FontSize = 11.5 };
        border.MouseLeftButtonUp += (_, _) => onClick();
        if (onRightClick is not null) border.MouseRightButtonUp += (_, e) => { onRightClick(); e.Handled = true; };
        return border;
    }

    private void RenderSeriesParts(VideoItem item)
    {
        SeriesPartsHost.Children.Clear();
        SeriesPartsHeader.Visibility = Visibility.Collapsed;
        SeriesPartsHost.Visibility = Visibility.Collapsed;
        if (item.SeriesNumbers.Count == 0) return;
        SeriesPartsHeader.Text = $"系列：{item.Series}（{item.SeriesNumbers.Count} 部）";
        SeriesPartsHeader.ToolTip = "右键排除该系列";
        SeriesPartsHeader.Cursor = Cursors.Hand;
        // 渲染可能重复执行，先解绑再绑定，避免事件叠加
        SeriesPartsHeader.MouseRightButtonUp -= SeriesHeader_RightClick;
        SeriesPartsHeader.MouseRightButtonUp += SeriesHeader_RightClick;
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

    /// <summary>详情页系列标题右键：回到列表并排除该系列。</summary>
    private void SeriesHeader_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_currentItem is not { } item || string.IsNullOrEmpty(item.Series)) return;
        _currentItem = null;
        _searchPanel?.AddSeriesExclusion(item.Series);
        ApplyAndRender();
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
        var sameSeries = VideoLibraryService.CollapseByNumber(_library.Items.Where(i => i.Id != item.Id && !string.IsNullOrEmpty(item.Series) && i.Series.Equals(item.Series, StringComparison.OrdinalIgnoreCase))).Take(10).ToList();
        SeriesRecommendHeader.Visibility = sameSeries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var rec in sameSeries)
        {
            var card = new VideoPosterCard();
            card.Bind(rec);
            card.DetailRequested += detail => Dispatcher.Invoke(() => { _currentItem = detail; ApplyAndRender(); });
            SeriesRecommends.Children.Add(card);
        }

        ActorRecommends.Children.Clear();
        var sameActors = VideoLibraryService.CollapseByNumber(_library.Items.Where(i => i.Id != item.Id && i.Actors.Any(a => item.Actors.Contains(a, StringComparer.OrdinalIgnoreCase)))).Take(10).ToList();
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

    /// <summary>详情页标签右键：回到列表并排除该标签。</summary>
    private void ExcludeTag(string tag)
    {
        _currentItem = null;
        _searchPanel?.AddTagExclusion(tag);
        ApplyAndRender();
    }

    /// <summary>详情页演员右键：回到列表并排除该演员。</summary>
    private void ExcludeActor(string actor)
    {
        _currentItem = null;
        _searchPanel?.AddActorExclusion(actor);
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
        _onlinePage = 1;
        ExecuteOnlineSearch();
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        _onlineSearchText = OnlineToolbar.SearchBox.Text.Trim();
        _onlinePage = 1;
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
        _onlinePendingAutoPlay = false;
        ClosePopoutIfOpen();
        CloseOnlineFullDetail();
        if (OnlineDetailDrawer.Visibility == Visibility.Visible) SetOnlineDetailVisible(false);
        try
        {
            // 服务端每页只有十几条：一次并发抓取本段所有页（首页 + 后续页），
            // 首页响应一到立即上屏，不等剩余页；剩余页回来后按番号去重增量追加。
            // （旧实现串行等 3 页才渲染，再叠加 Cloudflare 降级链，首屏要十几秒。）
            // MissAV 每页条数少需合并多页；JavDB 等源单页结果充足，只抓首页。
            var mergePages = MergePagesFor(source);
            var pages = Enumerable.Range(_onlinePage, mergePages).ToList();
            var pageTasks = pages.ToDictionary(p => p, p => source.SearchAsync(_onlineSearchText, p, ct));

            var first = await pageTasks[_onlinePage];
            if (version != _onlineSearchVersion || ct.IsCancellationRequested) return;

            _onlineLastPage = _onlinePage;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            RenderOnlineResults(first.Items.Where(i => seen.Add(OnlineDedupeKey(i))).ToList(), append: false);
            if (_onlineRenderedCount == 0)
            {
                // 仅 MissAV 无结果时按番号换其他刮削源兜底（其搜索最可能被反爬吞结果）；
                // 其他源（JavDB）无结果直接保持空状态
                if (source.Info.Id == "missav")
                    await TryOnlineFallbackSearchAsync(source, ct);
                return;
            }

            var added = new List<OnlineVideoSummary>();
            var lastPageWithNew = _onlinePage;
            foreach (var page in pages.Skip(1))
            {
                OnlineVideoSearchResult next;
                try { next = await pageTasks[page]; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // 单页续抓失败不拖垮整体：已上屏的首页保留，只少这一页的结果
                    _logger.Warn($"[VideoView] 在线搜索第 {page} 页续抓失败: {ex.Message}");
                    continue;
                }
                if (version != _onlineSearchVersion || ct.IsCancellationRequested) return;

                var newItems = next.Items.Where(i => seen.Add(OnlineDedupeKey(i))).ToList();
                if (newItems.Count == 0) continue;
                added.AddRange(newItems);
                lastPageWithNew = page;
            }

            if (added.Count > 0)
            {
                _onlineLastPage = lastPageWithNew;
                RenderOnlineResults(added, append: true);
            }
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

    private void RenderOnlineResults(List<OnlineVideoSummary> items, bool append)
    {
        OnlineLoadingState.Visibility = Visibility.Collapsed;
        if (!append)
        {
            ResultCardsPanel.Children.Clear();
            _onlineRenderedCount = 0;
        }
        UpdateOnlineCardWidth();
        var cards = new List<(OnlineVideoSummary Item, VideoPosterCard Card)>();
        foreach (var item in items)
        {
            if (_onlineRenderedCount >= OnlineMaxResults) break;
            // 与本地库一致的海报卡片（封面为在线图片 URL，ImageLoader 支持网络加载）
            var card = new VideoPosterCard();
            card.Width = _onlineCardWidth;
            card.Bind(item);
            card.OnlineDetailRequested += OnlinePosterDetailRequested;
            card.OnlinePreviewRequested += OnlinePosterPreviewRequested;
            card.OnlineFullDetailRequested += OnlinePosterFullDetailRequested;
            ResultCardsPanel.Children.Add(card);
            cards.Add((item, card));
            _onlineRenderedCount++;
        }
        ResultsScroll.Visibility = _onlineRenderedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        OnlineEmptyState.Visibility = _onlineRenderedCount > 0 ? Visibility.Collapsed : Visibility.Visible;
        OnlineEmptyHint.Text = _onlineRenderedCount > 0 ? "" : "没有找到匹配的视频";
        UpdateOnlinePagingText();

        // 搜索页摘要信息有限，后台逐卡片拉详情回填（发行日期/演员/标签），低并发渐进显示
        if (cards.Count > 0) StartOnlineDetailEnrichment(cards);
    }

    private void UpdateOnlinePagingText()
    {
        PageInfoText.Text = _onlineLastPage > _onlinePage
            ? $"第 {_onlinePage}-{_onlineLastPage} 页 · {_onlineRenderedCount} 条"
            : $"第 {_onlinePage} 页 · {_onlineRenderedCount} 条";
        OnlinePagingHost.Visibility = _onlineRenderedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>在线结果跨页去重键：番号优先（同番号的 -uncensored-leak 等变体会跨页重复），无番号退回 Id。</summary>
    private static string OnlineDedupeKey(OnlineVideoSummary i)
        => string.IsNullOrEmpty(i.Number) ? i.Id : i.Number;

    /// <summary>单次搜索自动合并的页数：MissAV 每页约 12 条需合并多页才够一屏；其他源（如 JavDB）单页即可。</summary>
    private static int MergePagesFor(IVideoSource? source)
        => source is { Info.Id: "missav" } ? OnlineAutoMergePages : 1;

    /// <summary>
    /// MissAV 搜索无结果时的换源兜底：关键词可解析为番号时，按 advanced.contentRoutes 的
    /// 来源顺序逐源按番号取详情（此前刮削过的直接吃快照缓存）。命中即上屏一张卡片
    /// （角标显示来源名），详情预写入缓存供侧栏同步渲染。返回 true 表示已上屏兜底结果。
    /// </summary>
    private async Task<bool> TryOnlineFallbackSearchAsync(IVideoSource missav, CancellationToken ct)
    {
        var fallback = App.Services.GetService<OnlineVideoFallbackSearchService>();
        if (fallback is null) return false;
        var version = _onlineSearchVersion;
        OnlineLoadingText.Text = "MissAV 无结果，正在尝试其他源…";
        OnlineVideoFallbackHit? hit;
        try { hit = await fallback.SearchAsync(_onlineSearchText, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warn($"[VideoView] 换源兜底失败: {ex.Message}");
            hit = null;
        }
        finally { OnlineLoadingText.Text = "搜索中…"; }
        if (hit is null || version != _onlineSearchVersion || ct.IsCancellationRequested) return false;

        // 预填详情缓存：卡片点击 → GetDetailUrl(合成 Id) → 缓存命中直接渲染，不再联网
        var detailUrl = missav.GetDetailUrl(hit.Summary.Id);
        if (!string.IsNullOrEmpty(detailUrl)) _detailCache.Set(detailUrl, hit.Detail);

        _onlineLastPage = _onlinePage;
        RenderOnlineResults([hit.Summary], append: false);
        ToastService.Show($"MissAV 无结果，已从 {hit.Summary.KindLabel} 找到该番号", ToastKind.Info);
        return true;
    }

    /// <summary>
    /// 按结果区可用宽度与每行个数换算在线卡片宽度并应用到已有卡片：
    /// 卡片 + 右侧 10px 间距恰好铺满整行（不设上限，宽屏下拖动滑杆变化明显）；
    /// 详情侧栏展开等挤压视口导致可用宽度放不下目标列数时，自动减少列数，避免卡片横向溢出。
    /// （详情侧栏开合、拖动滑杆、窗口缩放都会触发重算。）
    /// </summary>
    private void UpdateOnlineCardWidth()
    {
        if (ResultCardsPanel is null || ResultsScroll.ActualWidth <= 0) return;
        const double scrollbarReserve = 16;
        const double cardGap = 10; // PosterCard 右侧 Margin
        var available = ResultsScroll.ActualWidth > scrollbarReserve
            ? ResultsScroll.ActualWidth - scrollbarReserve
            : ResultsScroll.ActualWidth;
        var columns = Math.Clamp(Columns, 1, GridCellSizer.MaxColumns);
        var minSlot = GridCellSizer.MinCardWidth + cardGap;
        var maxFit = Math.Max(1, (int)Math.Floor((available + cardGap) / minSlot));
        columns = Math.Min(columns, maxFit);
        var slotWidth = Math.Floor(available / columns);
        // 在线卡片放宽下限（200）：横版封面 + 大标题需要足够宽度才方便浏览；窄窗自动减列
        _onlineCardWidth = Math.Max(200, slotWidth - cardGap);
        foreach (var card in ResultCardsPanel.Children.OfType<VideoPosterCard>())
            card.Width = _onlineCardWidth;
    }

    // ── 在线详情侧栏 ─────────────────────────────────────────────────

    private OnlineVideoSummary? _onlineDetailSummary;
    private string? _onlineDetailUrl;
    private int _onlineDetailVersion;
    /// <summary>完整详情页渲染版本号：切换影片/重搜/切页时自增，用于丢弃过期的异步详情回填。</summary>
    private int _onlineFullDetailVersion;
    private CancellationTokenSource? _onlineEnrichCts;

    /// <summary>详情页内存缓存（DI 单例，应用生命周期内有效）。</summary>
    private readonly OnlineVideoDetailCache _detailCache = App.Services.GetRequiredService<OnlineVideoDetailCache>();

    /// <summary>详情回填并发闸门：首页批次与追加批次共享，总并发恒为 2，避免触发站点反爬。</summary>
    private readonly SemaphoreSlim _onlineDetailGate = new(2, 2);

    /// <summary>
    /// 后台为每张搜索卡片拉取详情并回填（并发 2，避免触发站点反爬）。
    /// 新搜索/页面切换会取消上一轮回填；详情同时写入会话缓存供侧栏复用。
    /// </summary>
    private void StartOnlineDetailEnrichment(IReadOnlyList<(OnlineVideoSummary Item, VideoPosterCard Card)> cards)
    {
        _onlineEnrichCts?.Cancel();
        _onlineEnrichCts = new CancellationTokenSource();
        var ct = _onlineEnrichCts.Token;
        var version = _onlineSearchVersion;
        var source = OnlineSource;
        if (source is null) return;

        _ = Task.Run(async () =>
        {
            await Task.WhenAll(cards.Select(async entry =>
            {
                try
                {
                    var url = source.GetDetailUrl(entry.Item.Id);
                    if (string.IsNullOrEmpty(url)) return;

                    if (!_detailCache.TryGet(url, out var detail))
                    {
                        await _onlineDetailGate.WaitAsync(ct);
                        try { detail = await source.GetDetailAsync(url, ct); }
                        finally { _onlineDetailGate.Release(); }
                        if (detail is not null) _detailCache.Set(url, detail);
                    }

                    if (detail is null || ct.IsCancellationRequested || version != _onlineSearchVersion) return;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (version != _onlineSearchVersion || ct.IsCancellationRequested) return;
                        if (ReferenceEquals(entry.Card.Parent, ResultCardsPanel))
                            entry.Card.UpdateOnlineDetail(detail);
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.Warn($"[VideoView] 在线详情回填失败: {entry.Item.Id} ({ex.Message})");
                }
            }));
        }, ct);
    }

    /// <summary>点击在线海报：先展示摘要并显示侧栏，随后拉取详情页补充完整信息。</summary>
    private void OnlinePosterDetailRequested(OnlineVideoSummary item) => OpenOnlineDetail(item, autoPlay: false);

    /// <summary>hover"▶ 预览"：打开详情并在流地址就绪后自动起播。</summary>
    private void OnlinePosterPreviewRequested(OnlineVideoSummary item) => OpenOnlineDetail(item, autoPlay: true);

    /// <summary>双击在线海报：直接进入完整详情页（侧栏随即收起；双击的第一次单击已初始化摘要/详情）。</summary>
    private void OnlinePosterFullDetailRequested(OnlineVideoSummary item)
    {
        if (_onlineDetailSummary?.Id != item.Id)
            OpenOnlineDetail(item, autoPlay: false);
        OpenOnlineFullDetail();
    }

    /// <summary>请求自动起播的待消费标记：详情流地址就绪后消费一次，随后清除。</summary>
    private bool _onlinePendingAutoPlay;

    private void OpenOnlineDetail(OnlineVideoSummary item, bool autoPlay)
    {
        _onlineDetailVersion++;
        var version = _onlineDetailVersion;
        _onlineDetailSummary = item;
        _onlineDetailUrl = null;
        _onlinePendingAutoPlay = autoPlay;

        OnlineDetailFullButton.Visibility = Visibility.Collapsed;
        SetOnlineDetailVisible(true);
        OnlineDetailPanel.ScrollToTop();
        // 与本地详情一致：顶替右侧筛选面板的位置
        DetailPanelToggled?.Invoke(true);
        // 换详情前停掉上一个的播放；摘要阶段无流地址，播放器先隐藏
        OnlinePreviewPlayer.Stop();
        OnlinePreviewPlayer.Visibility = Visibility.Collapsed;
        ImageLoader.SetSource(OnlineDetailPoster, item.CoverUrl);
        OnlineDetailNumber.Text = VideoNumberParser.Parse(item.Title).Number;
        OnlineDetailTitle.Text = item.Title;
        OnlineDetailOriginalTitle.Text = "";
        OnlineDetailRatingText.Text = "";
        OnlineDetailRating.Visibility = Visibility.Collapsed;
        OnlineInfoHost.Children.Clear();
        OnlineDetailTagChips.Children.Clear();
        OnlineDetailDescription.Text = "详情加载中…";
        OnlineDetailOpenUrlButton.Visibility = Visibility.Collapsed;
        OnlinePreviewImagesSection.Visibility = Visibility.Collapsed;
        OnlinePreviewImagesHost.Children.Clear();
        FillOnlineMagnetList(null, OnlineMagnetListHost, OnlineMagnetHint, OnlineTabMagnetButton);
        SelectOnlineTab(detailTab: true);

        var source = OnlineSource;
        if (source is null) { OnlineDetailDescription.Text = "在线源未注册"; return; }
        var url = source.GetDetailUrl(item.Id);
        if (string.IsNullOrEmpty(url)) { OnlineDetailDescription.Text = "该源不支持详情页"; return; }
        _onlineDetailUrl = url;

        // 生命周期内存缓存命中：同步渲染，零等待不闪烁
        if (_detailCache.TryGet(url, out var cached))
        {
            RenderOnlineDetail(cached);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var detail = await source.GetDetailAsync(url);
                if (detail is not null) _detailCache.Set(url, detail);
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (version != _onlineDetailVersion) return;
                    if (detail is null) { OnlineDetailDescription.Text = "未能获取到详情"; return; }
                    RenderOnlineDetail(detail);
                });
            }
            catch (Exception ex)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (version != _onlineDetailVersion) return;
                    OnlineDetailDescription.Text = $"详情加载失败: {ex.Message}";
                });
            }
        });
    }

    private void RenderOnlineDetail(OnlineVideoDetail detail)
    {
        // 兜底结果的详情带真实来源页地址，优先于合成缓存键 URL（供"打开详情页"直达源站）
        if (!string.IsNullOrEmpty(detail.VideoUrl)) _onlineDetailUrl = detail.VideoUrl;
        if (!string.IsNullOrEmpty(detail.CoverUrl)) ImageLoader.SetSource(OnlineDetailPoster, detail.CoverUrl);
        if (!string.IsNullOrEmpty(detail.Number)) OnlineDetailNumber.Text = detail.Number;
        OnlineDetailTitle.Text = detail.Title;
        OnlineDetailOriginalTitle.Text = detail.OriginalTitle != detail.Title ? detail.OriginalTitle : "";
        if (!string.IsNullOrEmpty(detail.RatingText))
        {
            OnlineDetailRatingText.Text = $"★ {detail.RatingText}";
            OnlineDetailRating.Visibility = Visibility.Visible;
        }
        else
        {
            OnlineDetailRating.Visibility = Visibility.Collapsed;
        }

        FillOnlineInfoGrid(detail, OnlineInfoHost);
        FillOnlineMagnetList(detail, OnlineMagnetListHost, OnlineMagnetHint, OnlineTabMagnetButton);

        // 在线播放：详情带流地址（MissAV 全片 m3u8；其他源可为预览 mp4）时
        // 用播放器顶替封面图，复用同一槽位；无流则保持普通封面
        var hasStream = !string.IsNullOrEmpty(detail.StreamUrl);
        OnlinePreviewPlayer.PosterUrl = string.IsNullOrEmpty(detail.CoverUrl)
            ? _onlineDetailSummary?.CoverUrl ?? ""
            : detail.CoverUrl;
        OnlinePreviewPlayer.StreamUrl = detail.StreamUrl ?? "";
        OnlinePreviewPlayer.Referer = detail.Referer;
        OnlinePreviewPlayer.ShowPlayButton = hasStream;
        OnlinePreviewPlayer.Visibility = hasStream ? Visibility.Visible : Visibility.Collapsed;
        OnlineDetailPopoutButton.Visibility = hasStream ? Visibility.Visible : Visibility.Collapsed;

        // "▶ 预览"请求的自动起播：流就绪后消费标记并起播一次
        if (_onlinePendingAutoPlay)
        {
            _onlinePendingAutoPlay = false;
            if (hasStream) OnlinePreviewPlayer.Play();
        }

        OnlineDetailDescription.Text = detail.Description;
        OnlineDetailOpenUrlButton.Visibility = string.IsNullOrEmpty(_onlineDetailUrl)
            ? Visibility.Collapsed : Visibility.Visible;

        // 标签胶囊（详情页 /tags/ 链接；类型已在信息行展示，不重复）
        OnlineDetailTagChips.Children.Clear();
        foreach (var tag in detail.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Take(24))
            OnlineDetailTagChips.Children.Add(MakeDetailChip($"#{tag}",
                () => RunOnlineSearch($"#{tag}"), toolTip: "点击搜索该标签"));

        RenderOnlinePreviewImages(detail, OnlinePreviewImagesHost, OnlinePreviewImagesSection);
        // 详情数据就绪后开放「完整详情 →」入口
        OnlineDetailFullButton.Visibility = Visibility.Visible;
    }

    /// <summary>预览图（剧照）横向条：点击在浏览器查看原图；详情无预览图时整段隐藏。
    /// 侧栏与完整详情页共用，host/section 分别指向各自容器。</summary>
    private void RenderOnlinePreviewImages(OnlineVideoDetail detail, Panel host, FrameworkElement section)
    {
        host.Children.Clear();
        var images = detail.PreviewImages.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        section.Visibility = images.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (images.Count == 0) return;

        foreach (var url in images)
        {
            var captured = url;
            var image = new Image { Stretch = Stretch.UniformToFill };
            ImageLoader.SetSource(image, url);
            var card = new Border
            {
                Width = 150, Height = 100,
                CornerRadius = new CornerRadius(8),
                BorderBrush = (Brush)FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                Background = (Brush)FindResource("HoverBgBrush"),
                Margin = new Thickness(0, 0, 6, 6),
                Child = image,
                ClipToBounds = true,
                Cursor = Cursors.Hand,
                ToolTip = "点击查看原图",
            };
            card.MouseLeftButtonUp += (_, _) =>
                Process.Start(new ProcessStartInfo(captured) { UseShellExecute = true });
            host.Children.Add(card);
        }
    }

    /// <summary>详情侧栏 Tab 切换；Tab 按钮选中态复用本地详情的主/次按钮样式。</summary>
    private void SelectOnlineTab(bool detailTab)
    {
        var primary = (Style)FindResource("LibraryPrimaryButtonStyle");
        var action = (Style)FindResource("LibraryActionButtonStyle");
        OnlineDetailTabInfo.Visibility = detailTab ? Visibility.Visible : Visibility.Collapsed;
        OnlineDetailTabMagnets.Visibility = detailTab ? Visibility.Collapsed : Visibility.Visible;
        OnlineTabDetailButton.Style = detailTab ? primary : action;
        OnlineTabMagnetButton.Style = detailTab ? action : primary;
    }

    private void OnlineTabDetail_Click(object sender, RoutedEventArgs e) => SelectOnlineTab(detailTab: true);
    private void OnlineTabMagnet_Click(object sender, RoutedEventArgs e) => SelectOnlineTab(detailTab: false);

    /// <summary>
    /// 表格式信息行：番号/发行日期/标题/女优/类型/发行商/导演/厂商/评分/时长。
    /// 每行左侧标签、右侧值（女优、类型为可点击胶囊），行间细分隔线；无数据的行隐藏。
    /// 侧栏与完整详情页共用，host 为各自的信息表容器。
    /// </summary>
    private void FillOnlineInfoGrid(OnlineVideoDetail detail, Panel host)
    {
        var rows = new (string Label, string Value, IReadOnlyList<string>? Chips)[]
        {
            ("番号", detail.Number, null),
            ("发行日期", detail.ReleaseDateText, null),
            ("标题", string.IsNullOrEmpty(detail.Title) ? detail.OriginalTitle : detail.Title, null),
            ("女优", "", detail.Actors.Where(a => !string.IsNullOrWhiteSpace(a)).Take(12).ToList()),
            ("类型", "", detail.Genres.Where(t => !string.IsNullOrWhiteSpace(t)).Take(16).ToList()),
            ("发行商", detail.Maker, null),
            ("导演", detail.Director, null),
            ("厂商", detail.Label, null),
            ("评分", detail.RatingText, null),
            ("时长", string.IsNullOrEmpty(detail.DurationText) ? "" : detail.DurationText, null),
        };

        host.Children.Clear();
        var labelBrush = (Brush)FindResource("TextSecondaryBrush");
        var valueBrush = (Brush)FindResource("TextPrimaryBrush");
        var separatorBrush = (Brush)FindResource("CardBorderBrush");
        var visibleRows = rows.Where(r => (r.Chips is not null && r.Chips.Count > 0)
                                          || (r.Chips is null && !string.IsNullOrWhiteSpace(r.Value))).ToList();

        for (var i = 0; i < visibleRows.Count; i++)
        {
            var (label, value, chips) = visibleRows[i];

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelText = new TextBlock
            {
                Text = label, FontSize = 11.5, Foreground = labelBrush,
                VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(0, 7, 0, 7),
            };
            rowGrid.Children.Add(labelText);

            UIElement valueElement;
            if (chips is not null)
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 3) };
                foreach (var chip in chips)
                {
                    wrap.Children.Add(MakeDetailChip(chip,
                        () => RunOnlineSearch(chip),
                        toolTip: "点击搜索"));
                }
                valueElement = wrap;
            }
            else
            {
                valueElement = new TextBlock
                {
                    Text = value, FontSize = 12, Foreground = valueBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Padding = new Thickness(0, 7, 0, 7),
                };
            }
            Grid.SetColumn(valueElement, 1);
            rowGrid.Children.Add(valueElement);

            // 行间细分隔线（最后一行不加，收在卡片圆角内）
            var rowBorder = new Border { Child = rowGrid };
            if (i > 0)
            {
                rowBorder.BorderThickness = new Thickness(0, 1, 0, 0);
                rowBorder.BorderBrush = separatorBrush;
                rowBorder.Opacity = 0.7;
            }
            host.Children.Add(rowBorder);
        }
    }

    /// <summary>磁力列表：JAVDB 表格式行——名称+日期 | 体积 | 复制，点击整行即复制。
    /// 侧栏与完整详情页共用；tabButton 为侧栏 Tab 按钮（完整页无 Tab，传 null）。</summary>
    private void FillOnlineMagnetList(OnlineVideoDetail? detail, Panel host, TextBlock hint, Button? tabButton)
    {
        host.Children.Clear();
        var magnets = detail?.Magnets ?? [];
        if (tabButton is not null)
            tabButton.Content = magnets.Count > 0 ? $"磁力列表 ({magnets.Count})" : "磁力列表";
        hint.Visibility = magnets.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (detail is null) return;

        var secondaryBrush = (Brush)FindResource("TextSecondaryBrush");
        foreach (var magnet in magnets)
        {
            var item = magnet;
            var border = new Border
            {
                Background = (Brush)FindResource("HoverBgBrush"),
                BorderBrush = (Brush)FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand,
                ToolTip = $"{item.Name}\n{item.Url}",
            };

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = string.IsNullOrEmpty(item.Name) ? "(未命名)" : item.Name,
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var dateText = new TextBlock
            {
                Text = item.Date, FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
                Foreground = secondaryBrush,
            };
            rowGrid.Children.Add(new StackPanel { Children = { nameText, dateText } });

            if (!string.IsNullOrEmpty(item.Size))
            {
                var sizeText = new TextBlock
                {
                    Text = item.Size, FontSize = 12, FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                    Foreground = (Brush)FindResource("PrimaryBrush"),
                };
                Grid.SetColumn(sizeText, 1);
                rowGrid.Children.Add(sizeText);
            }

            var copyButton = MakeCopyButton(() => CopyMagnet(item.Url));
            Grid.SetColumn(copyButton, 2);
            rowGrid.Children.Add(copyButton);

            border.Child = rowGrid;
            border.MouseLeftButtonUp += (_, _) => CopyMagnet(item.Url);
            host.Children.Add(border);
        }
    }

    private static Button MakeCopyButton(Action onCopy)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("LibraryActionButtonStyle"),
            Content = "复制",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => onCopy();
        return button;
    }

    private void CopyMagnet(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        Clipboard.SetText(url);
        ToastService.Show("磁力链接已复制", ToastKind.Success);
    }

    /// <summary>以任意关键词重新发起在线搜索（在线详情页标签/演员点击时调用）。</summary>
    private void RunOnlineSearch(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return;
        _onlinePage = 1;
        OnlinePreviewPlayer.Stop();
        SetOnlineDetailVisible(false);
        OnlineToolbar.SearchBox.Text = keyword;
        _onlineSearchText = keyword.Trim();
        ExecuteOnlineSearch();
    }

    /// <summary>放大播放：主界面播放器把当前位置与播放状态交接给小窗，小窗从同一位置续播。
    /// 关闭小窗时再交接回主界面——两个播放器实例接力，进度与暂停状态保持同步。</summary>
    private PopoutPlayerWindow? _popoutWindow;
    private bool _suppressPopoutResume;

    /// <summary>放大播放：把来源播放器（侧栏/完整页）当前位置与暂停态交接给小窗，从同一位置续播。
    /// 关闭小窗时再交接回来源所在界面——两个播放器实例接力，进度与暂停状态保持同步。</summary>
    private void OpenPopoutFor(OnlineVideoPreviewPlayer from, string title, string coverUrl)
    {
        if (_popoutWindow is not null) { _popoutWindow.Activate(); return; }

        var position = from.CurrentPositionMs;
        var paused = from.IsPaused;
        from.Stop();
        _popoutWindow = new PopoutPlayerWindow(
            title,
            from.StreamUrl,
            from.Referer,
            coverUrl,
            position, paused,
            OnPopoutClosed);
        _popoutWindow.Show();
        _logger.Info("[VideoView] 播放已交接给小窗（从同一位置续播）");
    }

    private void OnlineDetailPopout_Click(object sender, RoutedEventArgs e)
        => OpenPopoutFor(OnlinePreviewPlayer, OnlineDetailTitle.Text,
            _onlineDetailSummary?.CoverUrl ?? OnlinePreviewPlayer.PosterUrl);

    private void OnlineFullPopout_Click(object sender, RoutedEventArgs e)
        => OpenPopoutFor(OnlineFullPreviewPlayer, OnlineFullTitle.Text,
            _onlineDetailSummary?.CoverUrl ?? OnlineFullPreviewPlayer.PosterUrl);

    /// <summary>
    /// 播放接力：把 from 播放器的当前位置/暂停态交接给 to 播放器，从同一位置续播。
    /// 弹窗（小窗 ↔ 主界面）、侧栏 ↔ 完整页三处共用同一套交接语义；
    /// 无实际播放位置时停止并恢复封面+播放按钮态。
    /// </summary>
    private static void TransferPlayback(OnlineVideoPreviewPlayer? from, OnlineVideoPreviewPlayer? to, long positionMs, bool paused)
    {
        from?.Stop();
        if (to is null) return;
        if (positionMs > 0 && !string.IsNullOrEmpty(to.StreamUrl))
        {
            to.PlayFrom(positionMs, paused);
        }
        else
        {
            to.Stop();
            to.ShowPlayButton = !string.IsNullOrEmpty(to.StreamUrl);
        }
    }

    /// <summary>小窗关闭：把当前位置/状态交接回主界面（侧栏或完整页，看当前所在界面）续播；
    /// 程序主动关窗（切页/关详情/重搜）时抑制交接，直接停止。</summary>
    private void OnPopoutClosed()
    {
        var popout = _popoutWindow;
        _popoutWindow = null;
        var resume = !_suppressPopoutResume;
        _suppressPopoutResume = false;
        if (popout is null) return;

        // 交接回当前所在界面：完整页打开则回完整页播放器，否则回侧栏播放器
        var target = OnlineFullDetailPage.Visibility == Visibility.Visible
            ? (OnlineVideoPreviewPlayer)OnlineFullPreviewPlayer
            : OnlinePreviewPlayer;
        if (resume && (popout.IsPlaying || popout.IsPaused) && popout.CurrentPositionMs > 0)
        {
            TransferPlayback(null, target, popout.CurrentPositionMs, popout.IsPaused);
            _logger.Info("[VideoView] 播放已交接回主界面（从同一位置续播）");
        }
        else
        {
            // 小窗里没有实际播放（或主动关窗）：回到海报态
            target.Stop();
            target.ShowPlayButton = !string.IsNullOrEmpty(target.StreamUrl);
        }
    }

    /// <summary>关闭详情/切页前先关小窗；主动关闭时抑制回交接续播。</summary>
    private void ClosePopoutIfOpen()
    {
        if (_popoutWindow is null) return;
        _suppressPopoutResume = true;
        _popoutWindow.Close();
    }

    /// <summary>侧栏抽屉与其遮罩的可见性联动（浮层化后抽屉覆盖在结果区上，遮罩拦截点击）。
    /// 展开时带 200ms 水平滑入动画（失败不影响主功能，无动画直接切换）。</summary>
    private void SetOnlineDetailVisible(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            OnlineDetailDrawer.Visibility = Visibility.Visible;
            AnimateDrawerIn();
        }
        else
        {
            OnlineDetailDrawer.Visibility = Visibility.Collapsed;
        }
        OnlineDetailMask.Visibility = v;
    }

    /// <summary>抽屉从右缘滑入；动画结束后归零 transform 以免残留偏移。</summary>
    private void AnimateDrawerIn()
    {
        try
        {
            OnlineDetailDrawer.RenderTransform ??= new TranslateTransform();
            var transform = (TranslateTransform)OnlineDetailDrawer.RenderTransform;
            var anim = new DoubleAnimation(620, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            anim.Completed += (_, _) => transform.X = 0;
            transform.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        catch
        {
            // 动画失败不影响主功能：直接显示
        }
    }

    private void OnlineDetailClose_Click(object sender, RoutedEventArgs e) => CloseOnlineDetail();

    /// <summary>点击遮罩关闭详情：复用关闭逻辑（含停播、恢复封面态）。</summary>
    private void OnlineDetailMask_Click(object sender, MouseButtonEventArgs e) => CloseOnlineDetail();

    /// <summary>关闭侧栏详情：停播、恢复封面态、收起抽屉与遮罩。</summary>
    private void CloseOnlineDetail()
    {
        _onlineDetailVersion++;
        _onlinePendingAutoPlay = false;
        ClosePopoutIfOpen();
        OnlinePreviewPlayer.Stop();
        // 关闭详情恢复待播放态：封面 + 播放按钮（有流时）
        OnlinePreviewPlayer.ShowPlayButton = !string.IsNullOrEmpty(OnlinePreviewPlayer.StreamUrl);
        SetOnlineDetailVisible(false);
    }

    // ── 完整详情页（整页子页） ──────────────────────────────────────────

    /// <summary>侧栏「完整详情 →」：收起侧栏与遮罩，整页覆盖结果区；播放交接给完整页大播放器（同位置续播）。</summary>
    private void OnlineDetailFull_Click(object sender, RoutedEventArgs e) => OpenOnlineFullDetail();

    private void OpenOnlineFullDetail(bool refresh = false)
    {
        // refresh：完整页内推荐卡片切换影片时强制重载；否则（按钮/双击首次进入）已在页内则忽略
        if (OnlineFullDetailPage.Visibility == Visibility.Visible && !refresh) return;
        if (string.IsNullOrEmpty(_onlineDetailUrl)) return;
        _onlineFullDetailVersion++;
        var version = _onlineFullDetailVersion;

        // 记录侧栏播放位置/暂停态并停掉，完整页播放器渲染就绪后同位置续播
        var sidebarPosition = OnlinePreviewPlayer.CurrentPositionMs;
        var sidebarPaused = OnlinePreviewPlayer.IsPaused;
        OnlinePreviewPlayer.Stop();
        ClosePopoutIfOpen();
        SetOnlineDetailVisible(false);
        // 整页子页：隐藏搜索框/结果区/分页，由完整详情页替换独占显示
        ShowOnlineResultsArea(false);
        OnlineFullDetailPage.Visibility = Visibility.Visible;
        OnlineFullScroll.ScrollToTop();

        // 摘要先行：番号/标题占位，其余等详情从缓存或网络补齐
        ImageLoader.SetSource(OnlineFullPoster, _onlineDetailSummary?.CoverUrl ?? "");
        OnlineFullNumber.Text = _onlineDetailSummary is null
            ? ""
            : (string.IsNullOrEmpty(_onlineDetailSummary.Number)
                ? VideoNumberParser.Parse(_onlineDetailSummary.Title).Number
                : _onlineDetailSummary.Number);
        OnlineFullTitle.Text = _onlineDetailSummary?.Title ?? "";
        OnlineFullOriginalTitle.Text = "";
        OnlineFullRating.Visibility = Visibility.Collapsed;
        OnlineFullInfoHost.Children.Clear();
        OnlineFullTagChips.Children.Clear();
        OnlineFullDescription.Text = "详情加载中…";
        OnlineFullMagnetsHost.Children.Clear();
        OnlineFullMagnetHint.Visibility = Visibility.Collapsed;
        OnlineFullPreviewImagesSection.Visibility = Visibility.Collapsed;
        OnlineFullPreviewImagesHost.Children.Clear();
        OnlineFullRelatedSection.Visibility = Visibility.Collapsed;
        OnlineFullRelatedHost.Children.Clear();
        OnlineFullOpenUrlButton.Visibility = string.IsNullOrEmpty(_onlineDetailUrl) ? Visibility.Collapsed : Visibility.Visible;
        // 完整页播放器待详情渲染后再配流；先清掉上一次的状态
        OnlineFullPreviewPlayer.Stop();
        OnlineFullPreviewPlayer.Visibility = Visibility.Collapsed;
        OnlineFullPopoutButton.Visibility = Visibility.Collapsed;

        var source = OnlineSource;
        if (source is null) { OnlineFullDescription.Text = "在线源未注册"; return; }
        var url = _onlineDetailUrl;

        // 生命周期内存缓存命中：同步渲染，零等待不闪烁
        if (_detailCache.TryGet(url, out var cached))
        {
            RenderOnlineFullDetail(cached);
            TransferPlayback(null, OnlineFullPreviewPlayer, sidebarPosition, sidebarPaused);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var detail = await source.GetDetailAsync(url);
                if (detail is not null) _detailCache.Set(url, detail);
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (version != _onlineFullDetailVersion || OnlineFullDetailPage.Visibility != Visibility.Visible) return;
                    if (detail is null) { OnlineFullDescription.Text = "未能获取到详情"; return; }
                    RenderOnlineFullDetail(detail);
                    TransferPlayback(null, OnlineFullPreviewPlayer, sidebarPosition, sidebarPaused);
                });
            }
            catch (Exception ex)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (version != _onlineFullDetailVersion) return;
                    OnlineFullDescription.Text = $"详情加载失败: {ex.Message}";
                });
            }
        });
    }

    /// <summary>完整详情页渲染：与侧栏共用信息表/磁力/剧照渲染，双列布局 + 16:9 大播放器。</summary>
    private void RenderOnlineFullDetail(OnlineVideoDetail detail)
    {
        if (!string.IsNullOrEmpty(detail.CoverUrl)) ImageLoader.SetSource(OnlineFullPoster, detail.CoverUrl);
        if (!string.IsNullOrEmpty(detail.Number)) OnlineFullNumber.Text = detail.Number;
        OnlineFullTitle.Text = detail.Title;
        OnlineFullOriginalTitle.Text = detail.OriginalTitle != detail.Title ? detail.OriginalTitle : "";
        if (!string.IsNullOrEmpty(detail.RatingText))
        {
            OnlineFullRatingText.Text = $"★ {detail.RatingText}";
            OnlineFullRating.Visibility = Visibility.Visible;
        }
        else
        {
            OnlineFullRating.Visibility = Visibility.Collapsed;
        }

        FillOnlineInfoGrid(detail, OnlineFullInfoHost);
        FillOnlineMagnetList(detail, OnlineFullMagnetsHost, OnlineFullMagnetHint, null);

        var hasStream = !string.IsNullOrEmpty(detail.StreamUrl);
        OnlineFullPreviewPlayer.PosterUrl = string.IsNullOrEmpty(detail.CoverUrl)
            ? _onlineDetailSummary?.CoverUrl ?? ""
            : detail.CoverUrl;
        OnlineFullPreviewPlayer.StreamUrl = detail.StreamUrl ?? "";
        OnlineFullPreviewPlayer.Referer = detail.Referer;
        OnlineFullPreviewPlayer.ShowPlayButton = hasStream;
        OnlineFullPreviewPlayer.Visibility = hasStream ? Visibility.Visible : Visibility.Collapsed;
        OnlineFullPopoutButton.Visibility = hasStream ? Visibility.Visible : Visibility.Collapsed;

        OnlineFullDescription.Text = detail.Description;
        OnlineFullOpenUrlButton.Visibility = string.IsNullOrEmpty(_onlineDetailUrl)
            ? Visibility.Collapsed : Visibility.Visible;

        // 标签胶囊（详情页 /tags/ 链接；类型已在信息行展示，不重复）
        OnlineFullTagChips.Children.Clear();
        foreach (var tag in detail.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Take(24))
            OnlineFullTagChips.Children.Add(MakeDetailChip($"#{tag}",
                () => RunOnlineSearch($"#{tag}"), toolTip: "点击搜索该标签"));

        RenderOnlinePreviewImages(detail, OnlineFullPreviewImagesHost, OnlineFullPreviewImagesSection);
        RenderOnlineFullRelated(detail);
        EnrichFullDetailFromSecondarySource(detail);
    }

    /// <summary>
    /// 同系列推荐：JavDB 详情里的 RelatedVideos（番号列表）后台逐番号搜索找摘要，
    /// 命中则渲染 VideoPosterCard（单击/双击均切换当前完整页到该片）；无数据或全部未命中则整段隐藏。
    /// </summary>
    private void RenderOnlineFullRelated(OnlineVideoDetail detail)
    {
        OnlineFullRelatedHost.Children.Clear();
        var numbers = detail.RelatedVideos
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Take(12).ToList();
        OnlineFullRelatedSection.Visibility = numbers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (numbers.Count == 0) return;

        var source = OnlineSource;
        if (source is null) return;
        var version = _onlineFullDetailVersion;
        _ = Task.Run(async () =>
        {
            var hits = new List<OnlineVideoSummary?>();
            await Task.WhenAll(numbers.Select(async num =>
            {
                try
                {
                    await _onlineDetailGate.WaitAsync();
                    OnlineVideoSummary? hit = null;
                    try
                    {
                        var rs = await source.SearchAsync(num, 1);
                        hit = rs.Items.FirstOrDefault(i =>
                            string.Equals(OnlineDedupeKey(i), num, StringComparison.OrdinalIgnoreCase));
                    }
                    finally { _onlineDetailGate.Release(); }
                    lock (hits) hits.Add(hit);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[VideoView] 同系列推荐搜索失败 {num}: {ex.Message}");
                }
            }));

            _ = Dispatcher.BeginInvoke(() =>
            {
                if (version != _onlineFullDetailVersion || OnlineFullDetailPage.Visibility != Visibility.Visible) return;
                foreach (var item in hits)
                {
                    if (item is null) continue;
                    var card = new VideoPosterCard { Width = 150, Margin = new Thickness(0, 0, 8, 0) };
                    card.Bind(item);
                    // 推荐卡在完整页内：单击/双击均切换当前完整页；hover ▶ 播放不在此响应
                    card.OnlineDetailRequested += s => SwitchFullDetail(s);
                    card.OnlineFullDetailRequested += s => SwitchFullDetail(s);
                    OnlineFullRelatedHost.Children.Add(card);
                }
            });
        });
    }

    /// <summary>完整页内推荐卡片点击：切换到该片的完整详情（已在完整页时强制刷新内容）。</summary>
    private void SwitchFullDetail(OnlineVideoSummary item)
    {
        if (OnlineFullDetailPage.Visibility == Visibility.Visible && _onlineDetailSummary?.Id == item.Id) return;
        if (_onlineDetailSummary?.Id != item.Id)
        {
            _onlineDetailSummary = item;
            _onlineDetailUrl = OnlineSource?.GetDetailUrl(item.Id) ?? "";
        }
        OpenOnlineFullDetail(refresh: true);
    }

    /// <summary>
    /// 二级详情源：当前源详情通常缺磁力/同系列（MissAV 详情页不带），优先选 JavDB
    /// （详情含磁力列表与同系列番号，且自带 Cloudflare curl 降级），排除当前源。
    /// </summary>
    private IVideoSource? FindDetailEnrichmentSource(string currentSourceId)
        => App.Services.GetServices<IVideoSource>()
            .Where(s => s.Info.Id != currentSourceId)
            .OrderByDescending(s => s.Info.Id == "javdb")
            .FirstOrDefault();

    /// <summary>把二级源详情里的磁力/同系列合并进当前详情；两者都已有时原样返回。</summary>
    private static OnlineVideoDetail MergeDetailExtras(OnlineVideoDetail baseDetail, OnlineVideoDetail extra)
    {
        if (baseDetail.Magnets.Count > 0 && baseDetail.RelatedVideos.Count > 0) return baseDetail;
        var magnets = baseDetail.Magnets.Count > 0 ? baseDetail.Magnets : extra.Magnets;
        var related = baseDetail.RelatedVideos.Count > 0 ? baseDetail.RelatedVideos : extra.RelatedVideos;
        if (magnets == baseDetail.Magnets && related == baseDetail.RelatedVideos) return baseDetail;
        return new OnlineVideoDetail
        {
            SourceId = baseDetail.SourceId,
            VideoUrl = baseDetail.VideoUrl,
            Title = baseDetail.Title,
            OriginalTitle = baseDetail.OriginalTitle,
            CoverUrl = baseDetail.CoverUrl,
            DurationText = baseDetail.DurationText,
            Actors = baseDetail.Actors,
            Tags = baseDetail.Tags,
            Description = baseDetail.Description,
            Number = baseDetail.Number,
            ReleaseDateText = baseDetail.ReleaseDateText,
            StreamUrl = baseDetail.StreamUrl,
            Referer = baseDetail.Referer,
            MagnetUri = baseDetail.MagnetUri ?? (magnets.Count > 0 ? magnets[0].Url : null),
            Genres = baseDetail.Genres,
            PreviewImages = baseDetail.PreviewImages,
            Maker = baseDetail.Maker,
            Label = baseDetail.Label,
            Director = baseDetail.Director,
            RatingText = baseDetail.RatingText,
            Magnets = magnets,
            RelatedVideos = related,
        };
    }

    /// <summary>
    /// 完整详情跨源补充：详情缺磁力或同系列数据时，按番号向二级源搜索同番号影片并拉取其
    /// 详情，把磁力列表与同系列推荐补进当前完整页。20 秒超时，失败静默记日志，不打断浏览。
    /// </summary>
    private void EnrichFullDetailFromSecondarySource(OnlineVideoDetail detail)
    {
        if (detail.Magnets.Count > 0 && detail.RelatedVideos.Count > 0) return;
        if (string.IsNullOrWhiteSpace(detail.Number)) return;

        var version = _onlineFullDetailVersion;
        _ = Task.Run(async () =>
        {
            try
            {
                var secondary = FindDetailEnrichmentSource(detail.SourceId);
                if (secondary is null) return;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var ct = cts.Token;

                OnlineVideoDetail? extra;
                await _onlineDetailGate.WaitAsync(ct);
                try
                {
                    var search = await secondary.SearchAsync(detail.Number, 1, ct);
                    var hit = search.Items.FirstOrDefault(i =>
                        string.Equals(OnlineDedupeKey(i), detail.Number, StringComparison.OrdinalIgnoreCase));
                    var url = hit is null ? null : secondary.GetDetailUrl(hit.Id);
                    extra = url is null ? null : await secondary.GetDetailAsync(url, ct);
                }
                finally { _onlineDetailGate.Release(); }

                if (extra is null) return;

                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (version != _onlineFullDetailVersion || OnlineFullDetailPage.Visibility != Visibility.Visible) return;
                    var merged = MergeDetailExtras(detail, extra);
                    if (ReferenceEquals(merged, detail)) return;
                    FillOnlineMagnetList(merged, OnlineFullMagnetsHost, OnlineFullMagnetHint, null);
                    RenderOnlineFullRelated(merged);
                });
            }
            catch (Exception ex)
            {
                _logger.Warn($"[VideoView] 完整详情跨源补充失败 {detail.Number}: {ex.Message}");
            }
        });
    }

    /// <summary>完整详情页作为整页子页：显示时替换搜索页三行（搜索框/结果区/分页），隐藏时恢复。
    /// 分页行恢复后按当前结果数重算（进入前可能本就因无结果而隐藏）。</summary>
    private void ShowOnlineResultsArea(bool show)
    {
        OnlineToolbar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OnlineResultsArea.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) UpdateOnlinePagingText();
        else OnlinePagingHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>「← 返回搜索结果」：完整页收起并恢复搜索页三行，播放按位置交接回侧栏（有流有位置则续播，否则停止）。</summary>
    private void OnlineFullBack_Click(object sender, RoutedEventArgs e)
    {
        _onlineFullDetailVersion++;
        var position = OnlineFullPreviewPlayer.CurrentPositionMs;
        var paused = OnlineFullPreviewPlayer.IsPaused;
        var hadStream = !string.IsNullOrEmpty(OnlineFullPreviewPlayer.StreamUrl);
        OnlineFullPreviewPlayer.Stop();
        OnlineFullDetailPage.Visibility = Visibility.Collapsed;
        ShowOnlineResultsArea(true);

        if (position > 0 && hadStream)
        {
            // 正在播/暂停：重新打开侧栏，从同位置续播
            SetOnlineDetailVisible(true);
            TransferPlayback(null, OnlinePreviewPlayer, position, paused);
        }
        else
        {
            // 无实际播放：侧栏保持收起，侧栏播放器恢复封面态
            OnlinePreviewPlayer.Stop();
            OnlinePreviewPlayer.ShowPlayButton = !string.IsNullOrEmpty(OnlinePreviewPlayer.StreamUrl);
        }
    }

    /// <summary>收起完整详情页（换源/重搜/切页时调用）：停播完整页播放器、隐藏整页并恢复搜索页三行。</summary>
    private void CloseOnlineFullDetail()
    {
        if (OnlineFullDetailPage.Visibility != Visibility.Visible) return;
        _onlineFullDetailVersion++;
        OnlineFullPreviewPlayer.Stop();
        OnlineFullDetailPage.Visibility = Visibility.Collapsed;
        ShowOnlineResultsArea(true);
    }

    private void OnlineFullOpenUrl_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_onlineDetailUrl)) return;
        Process.Start(new ProcessStartInfo(_onlineDetailUrl) { UseShellExecute = true });
    }

    /// <summary>16:9 播放器槽高度随宽度自适应。</summary>
    private void OnlineFullPlayerSlot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = OnlineFullPlayerSlot.ActualWidth;
        if (width > 0) OnlineFullPlayerSlot.Height = width * 9.0 / 16.0;
    }

    private void OnlineDetailOpenUrl_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_onlineDetailUrl)) return;
        Process.Start(new ProcessStartInfo(_onlineDetailUrl) { UseShellExecute = true });
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        // 每次搜索会自动合并多页，按整段步进回退，避免与当前视图大量重叠
        if (_onlinePage > 1)
        {
            _onlinePage = Math.Max(1, _onlinePage - MergePagesFor(OnlineSource));
            ExecuteOnlineSearch();
        }
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        // 从上一次实际抓取的最后一页之后继续
        _onlinePage = Math.Max(_onlineLastPage, _onlinePage) + 1;
        ExecuteOnlineSearch();
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
            var lastRefreshAt = DateTime.UtcNow;
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
                // 整页刷新较重（重新绑定所有卡片），节流到约 2 秒一次；循环结束后再补一次最终刷新。
                if ((DateTime.UtcNow - lastRefreshAt).TotalMilliseconds >= 2000)
                {
                    lastRefreshAt = DateTime.UtcNow;
                    _ = Dispatcher.BeginInvoke(new Action(ApplyAndRender));
                }
            }
            _ = Dispatcher.BeginInvoke(new Action(ApplyAndRender));
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
            state?.Series ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            state?.Studio ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            state?.CensorType,
            state?.Resolution ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            state?.Duration ?? new HashSet<DurationRange>(),
            new HashSet<ScrapeStatus> { ScrapeStatus.NoMatch },
            false,
            false));
        Refresh();
    }

    private void StartScrape(IEnumerable<string> ids, bool autoStarted, string title = "刮削中")
    {
        var idList = ids.ToList();
        if (idList.Count == 0 || _taskQueue is null) return;
        _logger.Info($"[VideoView] 入队刮削任务: {idList.Count} 条 ({title})");
        _activeTaskId = _taskQueue.EnqueueBatchTask(idList, VideoTaskType.ScrapeVideo, autoStarted ? "自动刮削中" : title);
        ShowTaskBar();
        UpdateTaskBarFromQueue();
    }

    private void ShowTaskBar()
    {
        TaskBar.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;
        TaskLogList.Visibility = Visibility.Collapsed;
    }

    private void OnQueueProgressChanged(VideoTaskProgress _)
        => Dispatcher.BeginInvoke(new Action(UpdateTaskBarFromQueue));

    private void UpdateTaskBarFromQueue()
    {
        // 重扫进行中时任务条归扫描使用，刮削事件不覆盖其文案。
        if (_rescanBusy) return;
        if (_taskQueue is null || _activeTaskId.Length == 0)
        {
            TaskBar.Visibility = Visibility.Collapsed;
            return;
        }

        var task = _taskQueue.GetTask(_activeTaskId);
        if (task is null)
        {
            TaskBar.Visibility = Visibility.Collapsed;
            return;
        }

        TaskTitle.Text = task.Status switch
        {
            VideoTaskStatus.Running => "刮削中",
            VideoTaskStatus.WaitingRetry => "重试中",
            VideoTaskStatus.Completed => "已完成",
            VideoTaskStatus.Failed => "刮削失败",
            VideoTaskStatus.Cancelled => "已取消",
            _ => "等待中",
        };
        var becameFinished = task.Status is VideoTaskStatus.Completed or VideoTaskStatus.Failed or VideoTaskStatus.Cancelled
            && _lastTaskStatus is not (VideoTaskStatus.Completed or VideoTaskStatus.Failed or VideoTaskStatus.Cancelled);
        _lastTaskStatus = task.Status;
        // 刮削批次结束时自动刷新列表，让新元数据/状态徽章立即生效。
        if (becameFinished) Refresh();
        TaskProgress.Value = task.Total > 0 ? task.Completed * 100.0 / task.Total : 0;
        TaskSummary.Text = $"{task.Completed}/{task.Total} 成功{task.SuccessCount} 未匹配{task.NoMatchCount} 失败{task.FailedCount} 跳过{task.SkippedCount}";
        TaskLogList.ItemsSource = task.Logs.ToArray();
        ViewNoMatchButton.Visibility = task.NoMatchCount > 0 && task.Status is VideoTaskStatus.Completed or VideoTaskStatus.Failed or VideoTaskStatus.Cancelled
            ? Visibility.Visible : Visibility.Collapsed;
        RetryFailedButton.Visibility = task.FailedCount > 0 && task.Status is VideoTaskStatus.Completed or VideoTaskStatus.Failed or VideoTaskStatus.Cancelled
            ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = task.Status is VideoTaskStatus.Pending or VideoTaskStatus.WaitingRetry or VideoTaskStatus.Running;
    }

    private void ToggleLogs_Click(object sender, RoutedEventArgs e)
        => TaskLogList.Visibility = TaskLogList.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void StopScrape_Click(object sender, RoutedEventArgs e)
    {
        // 停止按钮同时服务重扫与刮削：谁在跑就停谁。
        if (_rescanBusy)
        {
            _rescanCts?.Cancel();
            return;
        }
        if (_activeTaskId.Length > 0) _taskQueue?.Cancel(_activeTaskId);
    }

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
        else if (sender == NextPageButton && _page < _pageCount) _page++;
        else _page = _pageCount;
        ApplyAndRender();
    }

    private void JumpPageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            JumpToPage();
        }
    }

    private void JumpPage_Click(object sender, RoutedEventArgs e) => JumpToPage();

    private void JumpToPage()
    {
        if (!int.TryParse(JumpPageBox.Text.Trim(), out var page))
        {
            JumpPageBox.Clear();
            return;
        }
        var clamped = Math.Clamp(page, 1, Math.Max(1, _pageCount));
        if (clamped != _page)
        {
            _page = clamped;
            ApplyAndRender();
        }
        JumpPageBox.Clear();
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
        if (_rescanBusy) return;
        _rescanButton.IsEnabled = false;
        _rescanBusy = true;
        _rescanCts = new CancellationTokenSource();
        var ct = _rescanCts.Token;
        // 复用底部任务条显示扫描进度（不定进度条 + 当前目录与计数）。
        TaskBar.Visibility = Visibility.Visible;
        TaskTitle.Text = "正在扫描本地库";
        TaskProgress.IsIndeterminate = true;
        TaskSummary.Text = "准备扫描...";
        TaskLogList.Visibility = Visibility.Collapsed;
        ViewNoMatchButton.Visibility = Visibility.Collapsed;
        RetryFailedButton.Visibility = Visibility.Collapsed;
        StopButton.IsEnabled = true;
        try
        {
            IProgress<string> progress = new Progress<string>(msg => TaskSummary.Text = msg);
            var pendingIds = new List<string>();
            await Task.Run(() =>
            {
                foreach (var path in roots)
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Report($"正在扫描: {path}");
                    foreach (var item in _library.Rescan(path, progress.Report, ct))
                        pendingIds.Add(item.Id);
                }
            }, ct);
            var newCount = pendingIds.Distinct().Count();
            TaskTitle.Text = "扫描完成";
            TaskProgress.IsIndeterminate = false;
            TaskSummary.Text = $"发现 {newCount} 个新文件";
            ToastService.Show($"重新扫描完成，发现 {newCount} 个新文件", ToastKind.Success);
            Refresh();
            if (newCount == 0) return;
            var autoScrape = App.Services.GetRequiredService<ConfigService>().Current.VideoScraping?.AutoScrapeNewFiles ?? true;
            if (autoScrape)
            {
                StartScrape(pendingIds.Distinct().ToList(), autoStarted: true);
            }
            else
            {
                ToastService.Show($"发现 {newCount} 个新文件（自动刮削已关闭）", ToastKind.Info);
            }
        }
        catch (OperationCanceledException)
        {
            ToastService.Show("扫描已取消，视频库未改动", ToastKind.Info);
        }
        catch (Exception ex)
        {
            _logger.Error("[VideoView] 重新扫描根目录失败", ex);
            ToastService.ShowError(ex, "重新扫描失败：");
        }
        finally
        {
            _rescanBusy = false;
            _rescanCts?.Dispose();
            _rescanCts = null;
            _rescanButton.IsEnabled = true;
            TaskProgress.IsIndeterminate = false;
            // 无活动刮削任务时收起任务条；有则交还刮削进度显示。
            if (!HasActiveScrapeTask) TaskBar.Visibility = Visibility.Collapsed;
            else UpdateTaskBarFromQueue();
        }
    }

}
