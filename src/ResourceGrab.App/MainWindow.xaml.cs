using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using ResourceGrab.App.Common;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Dialogs;
using ResourceGrab.App.Services;
using ResourceGrab.App.Themes;
using ResourceGrab.App.ViewModels;
using ResourceGrab.App.Views;
using ResourceGrab.App.Shell;
using ResourceGrab.App.Shell.Providers;
using ResourceGrab.Core.Services.ShortDrama;
using ResourceGrab.Core.Sources.ShortDrama;
using ResourceGrab.Core;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources;
using Microsoft.Extensions.DependencyInjection;
using Path = System.Windows.Shapes.Path;

namespace ResourceGrab.App;

/// <summary>主窗口：顶部应用栏 + 左侧导航 + 中央内容 + 右侧下载面板。</summary>
public partial class MainWindow : Window
{
    private readonly SessionService _session;
    private readonly SourceManager _sourceManager;
    private readonly ConfigService _config;
    private readonly LocalLibraryService _localLibrary;
    private readonly SearchView _searchView;
    private RankView? _rankView;
    private RankBrowseView? _rankBrowseView;
    private CategoryView? _categoryView;
    private CategoryBrowseView? _categoryBrowseView;
    private FavoriteView? _favoriteView;
    private LocalView? _localView;
    private MangaRecommendView? _mangaRecommendView;
    private UserControl? _localTabContent;
    private WeeklyView? _weeklyView;
    private NovelLocalView? _novelView;
    private VideoView? _videoView;
    private UserControl? _lastMangaContent;
    private UserControl? _lastNovelContent;
    private UserControl? _lastVideoContent;
    private UserControl? _lastPhotobookContent;

    // —— 短剧 ——
    private ShortDramaBrowseView? _dramaBrowseView;
    private ShortDramaDetailView? _dramaDetailView;
    private ShortDramaLocalView? _dramaLocalView;
    private UserControl? _lastDramaContent;
    /// <summary>详情页自己的返回目标（进入详情前的列表/搜索/本地页）；与 Tab 恢复用的 _lastDramaContent 分开。</summary>
    private UserControl? _dramaBackTarget;
    private NovelSearchPanel? _novelSearchPanel;
    private VideoSearchPanel? _videoSearchPanel;
    private NovelReaderView? _novelReaderView;
    private ScrapeToolsView? _scrapeToolsView;
    private AuthorSearchView? _authorSearchView;
    private readonly Dictionary<TaxonomyKind, PhotobookBrowseView> _photobookViews = new();

    /// <summary>「写真」列表页与「随机」页：同一视图的两种模式，各占一条独立路由。</summary>
    private PhotobookGalleryView? _photobookHomeView;
    private PhotobookGalleryView? _photobookRandomView;

    /// <summary>章节详情页缓存：按最近访问 LRU 淘汰，限制常驻内存。</summary>
    private const int MaxCachedChapterViews = 6;
    private readonly Dictionary<string, ChapterView> _chapterViews = new();
    private readonly LinkedList<string> _chapterOrder = new();
    private UserControl? _lastPage;
    // 进入在线阅读器前记录详情页自己的返回目标（搜索/排行等），避免阅读器往返后详情页返回又跳回阅读器
    private UserControl? _pageBeforeOnlineReader;
    private bool _rightPanelVisible;
    private bool _videoDetailPanelOpen;
    private ShellRoute? _currentRoute;
    private UserControl? _currentSidebarPanel;
    private ResourceKind _currentKind = ResourceKind.Manga;
    private readonly ShellController _shellController;
    private readonly Shell.ShellNavigator _navigator;

    /// <summary>窗口标题（copymanga 版可覆盖）。</summary>
    protected virtual string WindowTitle => "抓资源";

    private LocalSearchPanel? _searchPanel;
    private LocalComicDetailPanel? _localDetailPanel;
    private NovelSearchPanel NovelSearchPanelView => _novelSearchPanel ??= new NovelSearchPanel();
    private LocalComicDetailPanel LocalDetailPanelView => _localDetailPanel ??= new LocalComicDetailPanel();
    private VideoSearchPanel VideoSearchPanelView => _videoSearchPanel ??= new VideoSearchPanel();
    private NovelReaderView NovelReaderViewInstance => _novelReaderView ??= new NovelReaderView();
    private DownloadPage? _downloadPage;
    private DownloadPage DownloadPageView => _downloadPage ??= new DownloadPage();
    private DownloadPageViewModel? _downloadPageViewModel;

    private LocalSearchPanel SearchPanelView
    {
        get
        {
            if (_searchPanel is null)
            {
                _searchPanel = new LocalSearchPanel();
                _searchPanel.SearchChanged += (keyword, tags) => _localView?.ApplySearch(keyword, tags);
            }
            return _searchPanel;
        }
    }

    public MainWindow()
    {
        Title = WindowTitle;
        InitializeComponent();
        ApplyRightPanelVisibility();

        // Shell 架构：初始化控制器和导航栏
        _shellController = new ShellController();
        _navigator = new Shell.ShellNavigator(_shellController);
        _navigator.RouteChanged += OnRouteChanged;
        RegisterRoutes();
        InitializeNavRail();
        RightSidebarHost.SidebarCloseRequested += (_, _) => CollapseRightPanel();

        _session = App.Services.GetRequiredService<SessionService>();
        _sourceManager = App.Services.GetRequiredService<SourceManager>();
        RefreshNavRailSections();
        _config = App.Services.GetRequiredService<ConfigService>();
        _localLibrary = App.Services.GetRequiredService<LocalLibraryService>();

        // 下载页视图模型在启动时就创建：事件订阅要覆盖页面未打开时开始的下载，顶栏角标才有数
        _downloadPageViewModel = App.Services.GetRequiredService<DownloadPageViewModel>();
        _downloadPageViewModel.StateChanged += (_, _) => UpdateDownloadBadge();
        UpdateDownloadBadge();

        _searchView = new SearchView();
        _lastPage = _searchView;
        SetPage(_searchView);

        Navigation.OpenComicHandler = OpenComic;
        Navigation.OpenSearchHandler = OpenSearch;
        Navigation.OpenOnlineReaderHandler = OpenOnlineReader;
        Navigation.DownloadedKeysChangedHandler = RefreshDownloadedBadges;
        Navigation.OpenRankHandler = OpenRank;
        Navigation.OpenReaderHandler = OpenReader;
        Navigation.OpenLocalDetailHandler = OpenLocalDetail;
        Navigation.OpenNovelReaderHandler = OpenNovelReader;
        Navigation.CloseLocalDetailHandler = ShowLocalList;
        Navigation.BackHandler = () =>
        {
            if (PageHost.Content is NovelReaderView)
            {
                SetPage(_novelView ?? new NovelLocalView());
                ShowSidebarPanel(NovelSearchPanelView);
                LeftNavHost.Visibility = Visibility.Visible;
                try { (_novelView as ResourceGrab.App.Views.NovelLocalView)?.RefreshHistory(); } catch { }
                UpdateTopBarForKind();
                return;
            }
            if (PageHost.Content is NovelLocalView)
            {
                ExitNovelMode();
                UpdateTopBarForKind();
                return;
            }
            if (PageHost.Content is OnlineReaderView)
            {
                // 在线阅读器返回：回到章节详情页，并恢复详情页自己的返回目标
                SetPage(_lastPage);
                if (_pageBeforeOnlineReader != null) _lastPage = _pageBeforeOnlineReader;
                return;
            }
            if (ReferenceEquals(PageHost.Content, _localTabContent) && _localTabContent is ReaderView)
            {
                // 阅读页返回：回到本地漫画列表（右侧恢复本地搜索工具）
                ShowLocalList();
                return;
            }
            CollapseRightPanel();
            SetPage(_lastPage);
        };

        RefreshSourceBoxForKind(_currentKind);
        _sourceManager.CurrentChanged += () => Dispatcher.Invoke(() =>
        {
            RefreshNavRailSections();
            UpdateNavCapabilities();
        });
        UpdateNavCapabilities();

        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionService.IsLoggedIn) or nameof(SessionService.Username))
            {
                UpdateLoginArea();
            }
            if (e.PropertyName == nameof(SessionService.IsLoggedIn) &&
                ReferenceEquals(PageHost.Content, _favoriteView))
            {
                // 启动时登录异步恢复完成：若当前停在收藏页（此前显示"请先登录"），立即刷新收藏列表
                _favoriteView?.Refresh();
            }
        };
        UpdateLoginArea();
        UpdateThemeIcon();
        ApplyRightPanelVisibility();
        UpdateTopBarForKind();

        Loaded += (_, _) => ToastService.ShowHandler = (message, kind) => Snackbars.Show(message, kind);
        Closed += (_, _) => ToastService.ShowHandler = null;
    }

    private void RefreshSourceBoxForKind(ResourceKind kind)
    {
        SourceBox.Items.Clear();
        var filtered = _sourceManager.Sources.Where(s => s.Info.Kind == kind).ToList();
        if (filtered.Count == 0)
        {
            SourceBox.Items.Add(new ComboBoxItem { Content = $"{KindLabel(kind)}源 · 敬请期待", IsEnabled = false });
            SourceBox.IsEnabled = false;
        }
        else
        {
            SourceBox.IsEnabled = true;
            foreach (var source in filtered)
                SourceBox.Items.Add(new ComboBoxItem { Content = source.Info.DisplayName, Tag = source });
            var preferred = filtered.FirstOrDefault(s => ReferenceEquals(s, _sourceManager.Current)) ?? filtered[0];
            _sourceManager.Current = preferred;
            SourceBox.SelectedItem = SourceBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, preferred));
        }
    }


    private bool IsNovelMode => PageHost != null && (PageHost.Content is NovelLocalView || PageHost.Content is NovelReaderView);
    private bool IsVideoMode => PageHost != null && PageHost.Content is VideoView;
    private bool IsPhotobookMode => PageHost != null && PageHost.Content is ResourceGrab.App.Views.PhotobookBrowseView;
    private bool IsDramaMode => PageHost?.Content is ShortDramaBrowseView or ShortDramaDetailView or ShortDramaLocalView;

    /// <summary>一级类型的中文名，用于顶栏与源下拉文案。</summary>
    private static string KindLabel(ResourceKind kind) => kind switch
    {
        ResourceKind.Video => "视频",
        ResourceKind.Novel => "小说",
        ResourceKind.Photobook => "写真",
        ResourceKind.ShortDrama => "短剧",
        _ => "漫画",
    };

    /// <summary>
    /// 写真维度路由是否由当前源支持。没有写真源时不作判定 —— 页面自会提示"未接入"，
    /// 否则每次切源都会被判为不支持而反复回退。
    /// </summary>
    private bool PhotobookRouteSupported(string routeId)
    {
        if (_sourceManager.Current.Info.Kind != ResourceKind.Photobook) return true;

        // 「写真」列表页所有写真源都具备；「随机」取决于源是否实现了随机能力。
        // 这两条必须先判 —— 否则 routeId 的 "home"/"random" 会被当成维度名解析失败而误判为不支持。
        if (routeId == PhotobookNavSectionProvider.HomeRouteId) return true;
        if (routeId == PhotobookNavSectionProvider.RandomRouteId)
            return _sourceManager.Current is IRandomPhotobookSource;

        if (_sourceManager.Current is not ITaxonomySource) return false;
        var dimension = routeId["photobook.".Length..];
        return Enum.TryParse<TaxonomyKind>(dimension, ignoreCase: true, out var kind)
               && _sourceManager.Current.Info.Taxonomies.Contains(kind);
    }

    private void UpdateTopBarForKind()
    {
        try
        {
            var isNovel = IsNovelMode;
            var isDownload = IsDownloadPage;
            if (MangaTopActions != null) MangaTopActions.Visibility = (!isNovel && !isDownload && _currentKind == ResourceKind.Manga) ? Visibility.Visible : Visibility.Collapsed;
            if (VideoTopActions != null) VideoTopActions.Visibility = (!isNovel && !isDownload && _currentKind == ResourceKind.Video) ? Visibility.Visible : Visibility.Collapsed;
            if (PhotobookTopActions != null) PhotobookTopActions.Visibility = (!isNovel && !isDownload && _currentKind == ResourceKind.Photobook) ? Visibility.Visible : Visibility.Collapsed;
            if (DramaTopActions != null) DramaTopActions.Visibility = (!isNovel && !isDownload && _currentKind == ResourceKind.ShortDrama) ? Visibility.Visible : Visibility.Collapsed;
            if (DownloadTopActions != null) DownloadTopActions.Visibility = isDownload ? Visibility.Visible : Visibility.Collapsed;
            // 下载页没有侧栏，隐藏侧栏开关
            if (PanelToggleButton != null) PanelToggleButton.Visibility = isDownload ? Visibility.Collapsed : Visibility.Visible;

            // 漫画与写真都是多源站点，需要源下拉；视频/短剧/小说当前无源（短剧是单源，线路在页面内换）。
            var supportsSourceSwitcher = _currentKind is ResourceKind.Manga or ResourceKind.Photobook;
            if (SourceSwitcherLabel != null) SourceSwitcherLabel.Text = $"{KindLabel(_currentKind)}源";
            if (SourceBox != null) SourceBox.ToolTip = $"当前{KindLabel(_currentKind)}源（影响搜索 / 排行 / 分类 / 收藏）";
            if (SourceSwitcherHost != null)
            {
                SourceSwitcherHost.Visibility =
                    LeftNavHost.Visibility == Visibility.Visible &&
                    !isNovel &&
                    supportsSourceSwitcher
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
            // 漫画源需要账号；视频/小说/写真为本地或免登流程。
            if (AccountHost != null) AccountHost.Visibility = (!isDownload && _currentKind == ResourceKind.Manga) ? Visibility.Visible : Visibility.Collapsed;

        } catch {}
    }


    private void KindPill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } rb) return;

        // 「下载」是跨媒体的独立页面，不参与 ResourceKind 切换（进入由 Checked 事件处理）
        if (tag == "Download") return;

        if (!Enum.TryParse<ResourceKind>(tag, out var kind)) return;

        var wasDownload = IsDownloadPage;
        if (PageHost?.Content is UserControl leaving && !wasDownload)
        {
            if (IsNovelMode) _lastNovelContent = leaving;
            else if (IsVideoMode) _lastVideoContent = leaving;
            else if (IsPhotobookMode) _lastPhotobookContent = leaving;
            else if (IsDramaMode) _lastDramaContent = leaving;
            else _lastMangaContent = leaving;
        }
        _currentKind = kind;
        RefreshNavRailSections();
        RefreshSourceBoxForKind(kind);
        UpdateNavCapabilities();

        // 从下载页切回某个媒体：恢复进入下载页之前的那一页
        // （阅读器/详情页不在路由里，只按路由恢复会退回列表，所以优先还原保存的内容）
        if (wasDownload)
        {
            LeftNavHost.Visibility = Visibility.Visible;
            var saved = SavedContentFor(kind);
            if (_downloadReturnKind == kind && saved is not null)
            {
                // 先按路由恢复导航态（页签、返回栈、右栏策略），再把内容覆盖回进入前那一页
                if (!_navigator.RestoreLast(ToMediaKind(kind)))
                    _navigator.GoTo(DefaultRouteFor(kind));
                if (saved is ReaderView) ShowSidebarPanel(LocalDetailPanelView);
                SetPage(saved);
                _lastPage = saved;
                UpdateTopBarForKind();
                return;
            }
            UpdateTopBarForKind();
            if (!_navigator.RestoreLast(ToMediaKind(kind)))
                _navigator.GoTo(DefaultRouteFor(kind));
            return;
        }

        if (IsNovelMode && kind == ResourceKind.Manga) { ExitNovelMode(); return; }
        if (IsVideoMode && kind == ResourceKind.Manga) { ExitVideoMode(); return; }
        if (IsPhotobookMode && kind == ResourceKind.Manga) { RestoreMangaContent(); return; }
        if (IsDramaMode && kind == ResourceKind.Manga) { RestoreMangaContent(); return; }
        if (kind == ResourceKind.Novel && !IsNovelMode)
        {
            // 首次进入小说走默认路由；非首次恢复该类型最近的页面状态
            if (!_navigator.RestoreLast(MediaKind.Novel))
                _navigator.GoTo("novel.index");
            return;
        }
        if (kind == ResourceKind.Video && !IsVideoMode)
        {
            // 首次进入视频走默认路由（在线搜索 + 隐藏边栏）；非首次恢复最近的子页
            if (!_navigator.RestoreLast(MediaKind.Video))
                _navigator.GoTo("video.online");
            return;
        }
        if (kind == ResourceKind.Photobook && !IsPhotobookMode)
        {
            // 首次进入写真走默认路由；非首次恢复该类型最近的子页
            if (!_navigator.RestoreLast(MediaKind.Photobook))
                _navigator.GoTo(PhotobookNavSectionProvider.HomeRouteId);
            return;
        }
        if (kind == ResourceKind.ShortDrama && !IsDramaMode)
        {
            // 首次进入短剧走推荐页；非首次先恢复导航态（页签/右栏），
            // 再把上次离开时的内容覆盖回来——在线详情页不在路由里，只按路由恢复会退回推荐列表。
            var savedDrama = _lastDramaContent;
            if (!_navigator.RestoreLast(MediaKind.ShortDrama))
                _navigator.GoTo(ShortDramaNavSectionProvider.RecommendRouteId);
            if (savedDrama is not null && !ReferenceEquals(PageHost?.Content, savedDrama))
            {
                SetPage(savedDrama);
                _lastPage = savedDrama;
                if (savedDrama is ShortDramaLocalView local) local.OnShown();
            }
            UpdateTopBarForKind();
            return;
        }
        UpdateTopBarForKind();
    }

    /// <summary>当前是否停在下载页（沉浸式页面，隐藏左右栏）。</summary>
    private bool IsDownloadPage => _currentRoute?.Kind == MediaKind.Download;

    private static MediaKind ToMediaKind(ResourceKind kind) => kind switch
    {
        ResourceKind.Video => MediaKind.Video,
        ResourceKind.Novel => MediaKind.Novel,
        ResourceKind.Photobook => MediaKind.Photobook,
        ResourceKind.ShortDrama => MediaKind.ShortDrama,
        _ => MediaKind.Manga,
    };

    private static string DefaultRouteFor(ResourceKind kind) => kind switch
    {
        ResourceKind.Video => "video.online",
        ResourceKind.Novel => "novel.index",
        ResourceKind.Photobook => PhotobookNavSectionProvider.HomeRouteId,
        ResourceKind.ShortDrama => ShortDramaNavSectionProvider.RecommendRouteId,
        _ => "manga.search",
    };



    // ====================== Shell NavRail ======================

    private void InitializeNavRail()
    {
        NavRailControl.ItemClicked += OnNavItemClicked;
        RefreshNavRailSections();
    }

    /// <summary>按当前媒体类型刷新左侧导航栏的分组数据。</summary>
    private void RefreshNavRailSections()
    {
        if (_sourceManager is null) return;
        INavSectionProvider provider = _currentKind switch
        {
            ResourceKind.Video => new VideoNavSectionProvider(),
            ResourceKind.Novel => new NovelNavSectionProvider(),
            ResourceKind.Photobook => new PhotobookNavSectionProvider(_sourceManager.Current),
            ResourceKind.ShortDrama => new ShortDramaNavSectionProvider(),
            _ => new MangaNavSectionProvider(_sourceManager.Current.Info),
        };
        NavRailControl.Sections = provider.GetSections();
    }

    private void OnNavItemClicked(object? sender, NavItem item)
    {
        if (!item.IsEnabled) return;
        if (!_navigator.GoTo(item.Id))
        {
            // 未注册的路由：回退搜索页并记录，避免静默失败
            try { App.Services.GetRequiredService<ResourceGrab.Core.Logging.ILogger>().Warn($"[Shell] 未注册的导航路由: {item.Id}"); } catch { }
            _navigator.GoTo("manga.search");
        }
    }

    /// <summary>路由切换：替换中间内容，按路由声明的策略更新右侧边栏。</summary>
    private void OnRouteChanged(object? sender, ShellRoute route)
    {
        _currentRoute = route;
        // 下载页是「沉浸式」的：占满左中右，导航栏与右栏都收起
        var isDownload = route.Kind == Shell.MediaKind.Download;
        LeftNavHost.Visibility = isDownload ? Visibility.Collapsed : Visibility.Visible;
        SetPage(route.CreateView());
        switch (route.SidebarPolicy.Mode)
        {
            case SidebarMode.Filter:
            case SidebarMode.Context:
            case SidebarMode.Task:
                if (PanelFor(route.SidebarPolicy.PanelId) is { } panel)
                    ShowSidebarPanel(panel);
                else
                    CollapseRightPanel();
                break;
            default:
                CollapseRightPanel();
                break;
        }
        if (isDownload)
        {
            // 右栏强制收起（下载页没有侧栏），并同步顶栏页签选中态
            _rightPanelVisible = false;
            ApplyRightPanelVisibility();
            if (KindDownloadPill is not null && KindDownloadPill.IsChecked != true)
            {
                _syncingKindPill = true;
                KindDownloadPill.IsChecked = true;
                _syncingKindPill = false;
            }
        }
        // NavRail 的分组是异步重建的（RadioButton 容器在布局期生成），
        // 需等渲染之后再同步选中态，否则会被 Rebuild 的“默认选第一项”覆盖。
        // 不能只给写真做：短剧的频道/上新/榜单/本地、视频子页等同样需要按当前路由回选。
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (_currentRoute is { } currentRoute)
                    NavRailControl.SelectItem(currentRoute.RouteId);
            }));
        UpdateTopBarForKind();
    }

    private void NavigateToSearch() => _navigator.GoTo("manga.search");
    private void NavigateToRank() => _navigator.GoTo("manga.rank");
    private void NavigateToCategory() => _navigator.GoTo("manga.category");
    private void NavigateToWeekly() => _navigator.GoTo("manga.weekly");
    private void NavigateToLocal() => _navigator.GoTo("manga.local");
    private void NavigateToFavorites() => _navigator.GoTo("manga.favorites");
    private void NavigateToScrapeTools() => _navigator.GoTo("manga.scrape");

    /// <summary>从刮削工具页跳转到视频演员工具（跨媒体类型）。</summary>
    public void OpenVideoActorsTool() => _navigator.GoTo("video.actors");

    // ====================== Route Registration ======================

    private void RegisterRoutes()
    {
        // 漫画
        _navigator.Register("manga.search", () => { EnsureSearchView(); return _searchView; });
        _navigator.Register("manga.author", () =>
        {
            _authorSearchView ??= new AuthorSearchView();
            return _authorSearchView;
        });
        _navigator.Register("manga.rank", () =>
        {
            if (_sourceManager.Current is IRankSource)
            {
                _rankBrowseView ??= new RankBrowseView();
                _rankBrowseView.OnShown();
                return _rankBrowseView;
            }
            _rankView ??= new RankView();
            _rankView.OnShown();
            return _rankView;
        });
        _navigator.Register("manga.category", () =>
        {
            if (_sourceManager.Current is ICategorySource)
            {
                _categoryBrowseView ??= new CategoryBrowseView();
                _categoryBrowseView.OnShown();
                return _categoryBrowseView;
            }
            _categoryView ??= new CategoryView();
            _categoryView.OnShown();
            return _categoryView;
        });
        _navigator.Register("manga.weekly", () =>
        {
            _weeklyView ??= new WeeklyView();
            _weeklyView.OnShown();
            return _weeklyView;
        });
        _navigator.Register("manga.favorites", () =>
        {
            _favoriteView ??= new FavoriteView();
            _favoriteView.OnShown();
            return _favoriteView;
        });
        _navigator.Register("manga.local", () => { ShowLocalList(); return _localView!; },
            new SidebarPolicy(SidebarMode.Filter, "filter.manga"));
        _navigator.Register("manga.recommend", () =>
        {
            _mangaRecommendView ??= new MangaRecommendView();
            _mangaRecommendView.OnShown();
            return _mangaRecommendView;
        });
        _navigator.Register("manga.scrape", () =>
        {
            _scrapeToolsView ??= new ScrapeToolsView();
            _scrapeToolsView.OnShown();
            return _scrapeToolsView;
        });

        // 写真：六个维度共用 PhotobookBrowseView，但各自一条独立路线。
        // 路由没有参数化机制，且 CreateView 返回缓存实例，所以必须一维度一路由 Id。
        foreach (var dimension in Enum.GetValues<TaxonomyKind>())
        {
            var kind = dimension;
            _navigator.Register(PhotobookNavSectionProvider.RouteId(kind), () =>
            {
                if (!_photobookViews.TryGetValue(kind, out var view))
                    _photobookViews[kind] = view = new PhotobookBrowseView(kind);
                view.OnShown();
                return view;
            });
        }

        // 写真：列表页与随机页共用 PhotobookGalleryView 的两种模式，同样各占一条路由。
        _navigator.Register(PhotobookNavSectionProvider.HomeRouteId, () =>
        {
            _photobookHomeView ??= new PhotobookGalleryView(PhotobookGalleryMode.Home);
            _photobookHomeView.OnShown();
            return _photobookHomeView;
        });
        _navigator.Register(PhotobookNavSectionProvider.RandomRouteId, () =>
        {
            _photobookRandomView ??= new PhotobookGalleryView(PhotobookGalleryMode.Random);
            _photobookRandomView.OnShown();
            return _photobookRandomView;
        });

        // 视频：子页暂为 VideoView 内部页签（SwitchNav），路由化后统一由 NavRail 驱动
        _navigator.Register("video.online", () =>
        {
            OpenVideoView();
            _videoView?.SwitchNav("search");
            return _videoView!;
        });
        _navigator.Register("video.online-recommend", () =>
        {
            OpenVideoView();
            _videoView?.SwitchNav("online-recommend");
            return _videoView!;
        });
        _navigator.Register("video.local", () =>
        {
            OpenVideoView();
            _videoView?.SwitchNav("local");
            return _videoView!;
        }, new SidebarPolicy(SidebarMode.Filter, "filter.video"));
        _navigator.Register("video.recommend", () =>
        {
            OpenVideoView();
            _videoView?.SwitchNav("recommend");
            return _videoView!;
        });
        _navigator.Register("video.tasks", () =>
        {
            OpenVideoView();
            _videoView?.SwitchNav("tasks");
            return _videoView!;
        });
        _navigator.Register("video.actors", () =>
        {
            OpenVideoView();
            _videoView?.SwitchNav("actors");
            return _videoView!;
        });

        // 短剧：单源多入口，视图单实例 + ShowMode 切来源（与 VideoView.SwitchNav 同构）
        _navigator.Register(ShortDramaNavSectionProvider.RecommendRouteId, () =>
        {
            ShowDramaBrowse(DramaBrowseMode.Recommend);
            return _dramaBrowseView!;
        });
        _navigator.Register(ShortDramaNavSectionProvider.NewestRouteId, () =>
        {
            ShowDramaBrowse(DramaBrowseMode.Newest);
            return _dramaBrowseView!;
        });
        _navigator.Register(ShortDramaNavSectionProvider.RankRouteId, () =>
        {
            ShowDramaBrowse(DramaBrowseMode.Rank);
            return _dramaBrowseView!;
        });
        foreach (var (slug, name) in ShortDramaNavSectionProvider.Channels)
        {
            var channelSlug = slug;
            var channelName = name;
            _navigator.Register(ShortDramaNavSectionProvider.ChannelRouteId(channelSlug), () =>
            {
                ShowDramaBrowse(DramaBrowseMode.Channel, channelSlug, channelName);
                return _dramaBrowseView!;
            });
        }
        _navigator.Register(ShortDramaNavSectionProvider.LocalRouteId, () =>
        {
            ShowDramaLocal();
            return _dramaLocalView!;
        });

        // 下载页：跨媒体共用一个视图，独立 MediaKind（返回栈与各媒体的「最近路由」互不干扰）
        _navigator.Register("download.page", () => DownloadPageView, new SidebarPolicy(SidebarMode.Hidden));
        _navigator.Register("novel.index", () => { OpenNovelLocal(); return _novelView!; },
            new SidebarPolicy(SidebarMode.Filter, "filter.novel"));
        _navigator.Register("novel.reader", () => { NovelReaderViewInstance.LoadFile(""); return NovelReaderViewInstance; });
    }

    private void EnsureSearchView()
    {
        if (_searchView == null)
            throw new InvalidOperationException("SearchView not initialized");
    }

    /// <summary>统一的页面切换方法：设置 PageHost 内容并跟踪返回栈。</summary>
    private void SetPage(UserControl page)
    {
        if (PageHost.Content is UserControl current && !ReferenceEquals(current, page))
        {
            if (current is ShortDramaDetailView oldDrama) oldDrama.ExitPageFocus();
            _lastPage = current;
        }
        // 离开视频页时清掉内置详情栏状态，避免右栏插槽一直被隐藏
        if (!ReferenceEquals(page, _videoView)) _videoDetailPanelOpen = false;
        PageHost.Content = page;
    }

    // ====================== 导航 ======================

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHost is null)
        {
            return;
        }
        LeftNavHost.Visibility = Visibility.Visible;
        CollapseRightPanel();
        if (ReferenceEquals(sender, NavSearch))
        {
            // 从详情页等页面进入搜索时，保留返回目标。
            if (!ReferenceEquals(PageHost.Content, _searchView) && PageHost.Content is UserControl page)
                _lastPage = page;
            else
                _lastPage = _searchView;
            SetPage(_searchView);
        }
        else if (ReferenceEquals(sender, NavRank))
        {
            if (_sourceManager.Current is IRankSource)
            {
                _rankBrowseView ??= new RankBrowseView();
                _lastPage = _rankBrowseView;
                SetPage(_rankBrowseView);
                _rankBrowseView.OnShown();
            }
            else
            {
                _rankView ??= new RankView();
                _lastPage = _rankView;
                SetPage(_rankView);
                _rankView.OnShown();
            }
        }
        else if (ReferenceEquals(sender, NavCategory))
        {
            _categoryView ??= new CategoryView();
            _lastPage = _categoryView;
            SetPage(_categoryView);
            _categoryView.OnShown();
        }
        else if (ReferenceEquals(sender, NavFavorite))
        {
            _favoriteView ??= new FavoriteView();
            _lastPage = _favoriteView;
            SetPage(_favoriteView);
            _favoriteView.OnShown();
        }
        else if (ReferenceEquals(sender, NavLocal))
        {
            _localView ??= new LocalView();
            _localTabContent ??= _localView;
            _lastPage = _localView;
            if (ReferenceEquals(_localTabContent, _localView))
            {
                ShowLocalList();
            }
            else
            {
                // 本地页签停留在阅读页：恢复右侧漫画详情面板
                ShowSidebarPanel(LocalDetailPanelView);
                SetPage(_localTabContent);
            }
        }
        else if (ReferenceEquals(sender, NavWeekly))
        {
            _weeklyView ??= new WeeklyView();
            _lastPage = _weeklyView;
            SetPage(_weeklyView);
            _weeklyView.OnShown();
        }
        UpdateTopBarForKind();
    }

    private void OpenComic(string sourceId, string comicId)
    {
        CollapseRightPanel();
        var source = _sourceManager.Get(sourceId);
        var key = $"{source.Info.Id}:{comicId}";
        // 复用已打开的详情页，保持章节列表/滚动位置/选择状态；缓存按 LRU 淘汰，限制内存
        if (!_chapterViews.TryGetValue(key, out var view))
        {
            if (_chapterViews.Count >= MaxCachedChapterViews)
            {
                var oldest = _chapterOrder.First!.Value;
                _chapterOrder.RemoveFirst();
                _chapterViews.Remove(oldest);
            }
            view = new ChapterView(source, comicId);
            _chapterViews[key] = view;
        }
        else
        {
            _chapterOrder.Remove(key);
        }
        _chapterOrder.AddLast(key);
        SetPage(view);
    }

    private void OpenRank(RankPeriod period)
    {
        CollapseRightPanel();
        _rankView ??= new RankView();
        _lastPage = _rankView;
        SetPage(_rankView);
        _rankView.OnShown(period);
    }

    private void OpenOnlineReader(IComicSource source, IReadOnlyList<Chapter> chapters, int startIndex)
    {
        CollapseRightPanel();
        _pageBeforeOnlineReader = _lastPage;
        _lastPage = (UserControl)PageHost.Content;
        SetPage(new OnlineReaderView(source, chapters, startIndex));
    }

    private void OpenSearch(string keyword, string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return;
        }

        CollapseRightPanel();

        if (!ReferenceEquals(PageHost.Content, _searchView))
        {
            if (PageHost.Content is UserControl page)
            {
                _lastPage = page;
            }

            NavSearch.IsChecked = true;
            SetPage(_searchView);
        }

        _searchView.Search(keyword.Trim(), sourceId);
    }

    private async void RefreshDownloadedBadges()
    {
        try
        {
            var dir = _config.Current.DownloadDir;
            var keys = await Task.Run(() => _localLibrary.GetDownloadedKeys(dir));
            _searchView.RefreshDownloadedKeys(keys);
            _rankBrowseView?.RefreshDownloadedKeys(keys);
            _categoryBrowseView?.RefreshDownloadedKeys(keys);
            _rankView?.RefreshDownloadedKeys(keys);
            _categoryView?.RefreshDownloadedKeys(keys);
            _favoriteView?.RefreshDownloadedKeys(keys);
            _weeklyView?.RefreshDownloadedKeys(keys);
        }
        catch
        {
            // 徽章刷新失败不影响下载任务。
        }
    }

    private void OpenReader(LocalComic comic)
    {
        _localTabContent = new ReaderView(comic);
        SetPage(_localTabContent);
        // 阅读页右栏固定为漫画详情；SetPage 之后再展开，避免被其它收起逻辑回落成标签筛选
        ShowSidebarPanel(LocalDetailPanelView);
        LocalDetailPanelView.Show(comic, true);
    }

    /// <summary>本地列表点击卡片：右侧切换到本地漫画详情面板（检查更新/更新下载）。</summary>
    private void OpenLocalDetail(LocalComic comic)
    {
        ShowSidebarPanel(LocalDetailPanelView);
        LocalDetailPanelView.Show(comic);
    }
    // ====================== 内容源切换 ======================

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceBox.SelectedItem is ComboBoxItem { Tag: IComicSource source })
        {
            _sourceManager.Current = source;
        }
    }

    /// <summary>按当前源能力刷新导航；切到不支持的能力时回退搜索页。</summary>
    private void UpdateNavCapabilities()
    {
        var info = _sourceManager.Current.Info;
        var selectedId = NavRailControl.SelectedItemId;

        var onUnsupportedPage =
            (selectedId == "manga.rank" && !info.SupportsRank) ||
            (selectedId == "manga.category" && !info.SupportsCategories) ||
            (selectedId == "manga.weekly" && !info.SupportsWeekly) ||
            (selectedId == "manga.favorites" && !info.SupportsFavorites) ||
            (selectedId is not null && selectedId.StartsWith("photobook.") && !PhotobookRouteSupported(selectedId));

        if (onUnsupportedPage)
        {
            // 回退到当前类型的安全落点（漫画→搜索；写真→首个受支持维度），
            // 用 ReplaceCurrent 不压栈，避免返回时又落到不支持页
            if (_currentKind == ResourceKind.Photobook)
            {
                // 写真回退到「写真」列表页（所有写真源都可用）
                var fallbackId = PhotobookNavSectionProvider.DefaultRouteId;
                if (_navigator.ReplaceCurrent(fallbackId))
                {
                    NavRailControl.SelectItem(fallbackId);
                    return;
                }
            }
            NavRailControl.SelectItem("manga.search");
            selectedId = "manga.search";
            _lastPage = _searchView;
            SetPage(_searchView);
            return;
        }

        if (selectedId == "manga.rank")
        {
            if (_sourceManager.Current is IRankSource && !ReferenceEquals(PageHost.Content, _rankBrowseView))
            {
                _rankBrowseView ??= new RankBrowseView();
                _lastPage = _rankBrowseView;
                SetPage(_rankBrowseView);
                _rankBrowseView.OnShown();
            }
            else if (_sourceManager.Current is not IRankSource && !ReferenceEquals(PageHost.Content, _rankView))
            {
                _rankView ??= new RankView();
                _lastPage = _rankView;
                SetPage(_rankView);
                _rankView.OnShown();
            }
        }

        if (selectedId == "manga.category")
        {
            if (_sourceManager.Current is ICategorySource && !ReferenceEquals(PageHost.Content, _categoryBrowseView))
            {
                _categoryBrowseView ??= new CategoryBrowseView();
                _lastPage = _categoryBrowseView;
                SetPage(_categoryBrowseView);
                _categoryBrowseView.OnShown();
            }
            else if (_sourceManager.Current is not ICategorySource && !ReferenceEquals(PageHost.Content, _categoryView))
            {
                _categoryView ??= new CategoryView();
                _lastPage = _categoryView;
                SetPage(_categoryView);
                _categoryView.OnShown();
            }
        }
    }

    // ====================== 顶栏操作 ======================

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon()
    {
        ThemeIcon.Data = ThemeManager.IsDark ? Icons.Moon : Icons.Sun;
    }

    private void OpenConfigDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataDir);
            Process.Start("explorer.exe", AppPaths.AppDataDir);
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex, "打开配置目录失败：");
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>顶栏下载入口（与 漫画/视频 同组的页签）。</summary>
    private void OpenDownloadPage()
    {
        // 进入下载页前记住当前页：阅读器/详情页不是路由，靠路由恢复会退回列表
        if (!IsDownloadPage && PageHost?.Content is UserControl leaving)
        {
            if (IsNovelMode) _lastNovelContent = leaving;
            else if (IsVideoMode) _lastVideoContent = leaving;
            else if (IsPhotobookMode) _lastPhotobookContent = leaving;
            else if (IsDramaMode) _lastDramaContent = leaving;
            else _lastMangaContent = leaving;
            _downloadReturnKind = _currentKind;
        }
        _navigator.GoTo("download.page");
    }

    /// <summary>进入下载页时所在的媒体类型；null 表示不是从媒体页进入的。</summary>
    private ResourceKind? _downloadReturnKind;

    private UserControl? SavedContentFor(ResourceKind kind) => kind switch
    {
        ResourceKind.Video => _lastVideoContent,
        ResourceKind.Novel => _lastNovelContent,
        ResourceKind.Photobook => _lastPhotobookContent,
        ResourceKind.ShortDrama => _lastDramaContent,
        _ => _lastMangaContent,
    };

    /// <summary>同步页签选中态时置位，避免 Checked 递归触发导航。</summary>
    private bool _syncingKindPill;

    /// <summary>下载页签被选中：鼠标、键盘、UI 自动化都会走到这里。</summary>
    private void KindDownloadPill_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingKindPill) return;
        OpenDownloadPage();
    }

    /// <summary>顶栏角标：进行中 + 排队数量，0 时隐藏。</summary>
    private void UpdateDownloadBadge()
    {
        var count = _downloadPageViewModel?.RunningTotal ?? 0;
        DownloadBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadBadgeText.Text = count > 99 ? "99+" : count.ToString();
    }

    private void PanelToggle_Click(object sender, RoutedEventArgs e)
    {
        _rightPanelVisible = !_rightPanelVisible;
        if (!_rightPanelVisible)
        {
            ApplyRightPanelVisibility();
            return;
        }
        // 展开时确保右栏与当前页面完全对应（本地=标签/详情、视频本地=视频筛选、小说本地=小说筛选），
        // 不会残留上一页的面板；当前页面没有专属面板时保持收起。
        var panel = ResolveSidebarPanelForCurrentPage(keepCurrent: true);
        if (panel is null)
        {
            _rightPanelVisible = false;
            ApplyRightPanelVisibility();
            return;
        }
        SetSidebarPanel(panel, visible: true);
    }

    /// <summary>只控制右侧面板列的展开收起，不影响左侧导航。</summary>
    private void ApplyRightPanelVisibility()
    {
        // 视频页打开了内置详情栏时，右栏插槽必须保持隐藏，避免两个面板叠加
        var visible = _rightPanelVisible && !_videoDetailPanelOpen;
        RightPanelHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            PanelColumn.MinWidth = 300;
            PanelColumn.MaxWidth = 440;
            PanelColumn.Width = new GridLength(348);
        }
        else
        {
            PanelColumn.MinWidth = 0;
            PanelColumn.MaxWidth = 420;
            PanelColumn.Width = new GridLength(0);
        }
        PanelSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>只控制左侧导航栏显隐，不影响右侧面板。</summary>
    private void ShowLeftRail() => LeftNavHost.Visibility = Visibility.Visible;
    private void HideLeftRail() => LeftNavHost.Visibility = Visibility.Collapsed;

    /// <summary>设置右栏内容并展开（不影响左栏）。内容必须是 ISidebarPanel，由 SidebarHost 统一承载。</summary>
    private void ShowSidebarPanel(UserControl panel) => SetSidebarPanel(panel, visible: true);

    /// <summary>设置右栏内容与显隐，并记录当前面板。</summary>
    private void SetSidebarPanel(UserControl panel, bool visible)
    {
        if (panel is ISidebarPanel sidebarPanel)
            RightSidebarHost.Show(sidebarPanel);
        else
            RightSidebarHost.Show("", null, panel);
        _currentSidebarPanel = panel;
        _rightPanelVisible = visible;
        ApplyRightPanelVisibility();
    }

    /// <summary>当前页面允许出现的右栏面板 Id 列表（首个为该页默认面板）。</summary>
    private IReadOnlyList<string> ValidSidebarPanelIdsForCurrentPage()
    {
        var content = PageHost?.Content;
        if (content is ReaderView) return new[] { "context.manga.local" };
        if (content is LocalView) return new[] { "filter.manga", "context.manga.local" };
        if (content is MangaRecommendView) return new[] { "context.manga.local" };
        if (content is NovelLocalView) return new[] { "filter.novel" };
        if (content is VideoView)
        {
            // 视频是单视图多子页，子页由路由策略区分；非本地子页没有专属面板
            var panelId = _currentRoute?.SidebarPolicy.PanelId;
            return string.IsNullOrWhiteSpace(panelId) ? Array.Empty<string>() : new[] { panelId };
        }
        // 下载页等没有专属侧栏的页面：不再兜底到下载面板（下载已升级为独立页面）
        return Array.Empty<string>();
    }

    /// <summary>
    /// 按当前页面解析右栏面板；keepCurrent=true 且当前面板仍属于该页时沿用
    /// （用于本地页在「标签/详情」之间保持用户当前选择）。无面板时返回 null。
    /// </summary>
    private UserControl? ResolveSidebarPanelForCurrentPage(bool keepCurrent)
    {
        var ids = ValidSidebarPanelIdsForCurrentPage();
        if (keepCurrent && _currentSidebarPanel is { } current)
        {
            var currentId = (current as ISidebarPanel)?.PanelId;
            if (currentId is not null && ids.Contains(currentId)) return current;
        }
        foreach (var id in ids)
        {
            if (PanelFor(id) is { } panel) return panel;
        }
        return null;
    }

    /// <summary>按路由声明的 PanelId 解析右栏面板。</summary>
    private UserControl? PanelFor(string? panelId) => panelId switch
    {
        "filter.manga" => SearchPanelView,
        "filter.novel" => NovelSearchPanelView,
        "filter.video" => VideoSearchPanelView,
        "context.manga.local" => LocalDetailPanelView,
        _ => null,
    };

    /// <summary>隐藏右栏，并把内容复位为当前页面对应的面板（不影响左栏）。</summary>
    private void CollapseRightPanel()
    {
        var panel = ResolveSidebarPanelForCurrentPage(keepCurrent: true);
        if (panel is null)
        {
            _rightPanelVisible = false;
            ApplyRightPanelVisibility();
            return;
        }
        SetSidebarPanel(panel, visible: false);
    }

    /// <summary>视频本地详情开关：打开时收起右侧本地搜索面板，由 VideoView 内置详情栏顶替其位置；关闭时恢复。</summary>
    private void SetVideoDetailPanelOpen(bool open)
    {
        if (!ReferenceEquals(PageHost.Content, _videoView)) return;
        _videoDetailPanelOpen = open;
        if (open)
        {
            RightPanelHost.Visibility = Visibility.Collapsed;
            PanelSplitter.Visibility = Visibility.Collapsed;
            PanelColumn.MinWidth = 0;
            PanelColumn.MaxWidth = 440;
            PanelColumn.Width = new GridLength(0);
        }
        else
        {
            ApplyRightPanelVisibility();
        }
    }


    /// <summary>离开阅读页（进入列表/详情等页面）时：隐藏右侧面板，恢复下载队列内容。</summary>
    public async Task TriggerLocalRefreshAsync()
    {
        if (_localView is not null)
        {
            await _localView.RequestRefreshAsync();
        }
        else
        {
            _localView = new LocalView();
            await _localView.RequestRefreshAsync();
        }
    }

    public async Task<bool> OpenLocalDirsDialogAsync(Window owner)
    {
        _localView ??= new LocalView();
        return await _localView.OpenManageDirsDialogAsync(owner);
    }

    /// <summary>进入小说独立页：隐藏左侧栏，右侧为小说筛选。</summary>
private void OpenNovelLocal()
    {
        try
        {
            if (_lastNovelContent is NovelReaderView)
            {
                SetPage(_lastNovelContent);
                RightPanelHost.Visibility = Visibility.Collapsed;
                LeftNavHost.Visibility = Visibility.Visible;
                _rightPanelVisible = false;
                ApplyRightPanelVisibility();
                UpdateTopBarForKind();
                return;
            }
            _novelView ??= new NovelLocalView();
            if (_novelSearchPanel == null) _novelSearchPanel = new NovelSearchPanel();
            _novelView.SetSearchPanel(_novelSearchPanel);
            _lastPage = _novelView;
            SetPage(_novelView);
            ShowSidebarPanel(_novelSearchPanel);
            LeftNavHost.Visibility = Visibility.Visible;
            UpdateTopBarForKind();
        }
        catch (Exception ex)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop), "jm_crash.log"), "[" + DateTime.Now + "] OpenNovelLocal " + ex + "\r\n"); } catch { }
            System.Windows.MessageBox.Show("打开小说页失败：" + ex.Message + "\r\n\r\n" + ex.StackTrace, "错误");
        }
    }

    private void OpenVideoView()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _videoView ??= new VideoView();
            App.Services.GetRequiredService<ResourceGrab.Core.Logging.ILogger>().Info($"[VideoView] 首次创建 VideoView 耗时 {sw.ElapsedMilliseconds} ms");
            sw.Restart();
            _videoView.SetSearchPanel(VideoSearchPanelView);
            _videoView.DetailPanelToggled -= SetVideoDetailPanelOpen;
            _videoView.DetailPanelToggled += SetVideoDetailPanelOpen;
            _lastPage = _videoView;
            SetPage(_videoView);
            ShowSidebarPanel(VideoSearchPanelView);
            LeftNavHost.Visibility = Visibility.Visible;
            // 侧栏显隐不再在此强制：首次进入由 OnShown 首次分支触发 DetailPanelToggled(true)
            // 隐藏；非首次进入由路由工厂 SwitchNav(子页) 按目标子页状态恢复。
            UpdateTopBarForKind();
            _videoView.OnShown();
            App.Services.GetRequiredService<ResourceGrab.Core.Logging.ILogger>().Info($"[VideoView] OpenVideoView 总耗时 {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop), "jm_crash.log"),
                    "[" + DateTime.Now + "] OpenVideoView " + ex + "\r\n");
            }
            catch { }
            System.Windows.MessageBox.Show("打开视频页失败：" + ex.Message + "\r\n\r\n" + ex.StackTrace, "错误");
        }
    }

    private void ExitVideoMode()
    {
        KindMangaPill.IsChecked = true;
        RestoreMangaContent();
    }

    // ====================== 短剧 ======================

    private ShortDramaBrowseView DramaBrowseView
    {
        get
        {
            if (_dramaBrowseView is null)
            {
                _dramaBrowseView = new ShortDramaBrowseView();
                // 卡片点击 → 详情页；悬停播放键同样进详情（详情页会自动取流播放）
                _dramaBrowseView.OpenRequested += (_, summary) => OpenDramaDetail(summary);
                _dramaBrowseView.PlayRequested += (_, summary) => OpenDramaDetail(summary);
            }
            return _dramaBrowseView;
        }
    }

    private ShortDramaDetailView DramaDetailView
    {
        get
        {
            if (_dramaDetailView is null)
            {
                _dramaDetailView = new ShortDramaDetailView();
                _dramaDetailView.BackRequested += (_, _) => BackFromDramaDetail();
                _dramaDetailView.PageFocusChanged += (_, on) =>
                {
                    // 页面全屏：收起左侧导航，让播放区吃掉整行；退出时恢复
                    LeftNavHost.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
                    if (!on) UpdateTopBarForKind();
                };
                _dramaDetailView.RelatedOpenRequested += (_, summary) => OpenDramaDetail(summary);
            }
            return _dramaDetailView;
        }
    }

    private ShortDramaLocalView DramaLocalView
    {
        get
        {
            if (_dramaLocalView is null)
            {
                _dramaLocalView = new ShortDramaLocalView();
                _dramaLocalView.OpenRequested += (_, local) => OpenDramaLocal(local);
            }
            return _dramaLocalView;
        }
    }

    /// <summary>切换短剧列表来源（推荐/上新/榜单/频道）。</summary>
    private void ShowDramaBrowse(DramaBrowseMode mode, string? scope = null, string? title = null)
    {
        _lastDramaContent = null;
        LeftNavHost.Visibility = Visibility.Visible;
        DramaBrowseView.ShowMode(mode, scope, title);
        UpdateTopBarForKind();
    }

    private void ShowDramaLocal()
    {
        _lastDramaContent = null;
        LeftNavHost.Visibility = Visibility.Visible;
        DramaLocalView.OnShown();
        UpdateTopBarForKind();
    }

    /// <summary>打开在线详情（不注册为独立路由：返回目标是列表页，用 _lastDramaContent 记录）。</summary>
    private void OpenDramaDetail(ShortDramaSummary summary)
    {
        if (PageHost.Content is UserControl current && !ReferenceEquals(current, DramaDetailView))
        {
            _lastDramaContent = current;
            _dramaBackTarget = current;
        }
        var view = DramaDetailView;
        SetPage(view);
        view.LoadAsync(summary);
        UpdateTopBarForKind();
    }

    /// <summary>打开本地详情（离线播放）。</summary>
    private void OpenDramaLocal(LocalDrama local)
    {
        if (PageHost.Content is UserControl current && !ReferenceEquals(current, DramaDetailView))
        {
            _lastDramaContent = current;
            _dramaBackTarget = current;
        }
        var view = DramaDetailView;
        SetPage(view);
        view.LoadLocal(local);
        UpdateTopBarForKind();
    }

    private void BackFromDramaDetail()
    {
        DramaDetailView.ExitPageFocus();
        var target = _dramaBackTarget;
        _dramaBackTarget = null;
        if (target is not null && !ReferenceEquals(target, DramaDetailView))
        {
            SetPage(target);
            _lastDramaContent = target;
            return;
        }
        _lastDramaContent = null;
        _navigator.GoTo(ShortDramaNavSectionProvider.RecommendRouteId);
    }

    /// <summary>恢复上次离开时的漫画页面（搜索/本地列表/阅读器等），保持状态不丢失。</summary>
    private void RestoreMangaContent()
    {
        LeftNavHost.Visibility = Visibility.Visible;
        var content = _lastMangaContent;
        if (content is ReaderView)
        {
            ShowSidebarPanel(LocalDetailPanelView);
        }
        else
        {
            CollapseRightPanel();
            content ??= _searchView;
        }
        SetPage(content);
        _lastPage = content;
        ApplyRightPanelVisibility();
        UpdateTopBarForKind();
    }

    private void ExitNovelMode()
    {
        KindMangaPill.IsChecked = true;
        RestoreMangaContent();
    }

    private void OpenNovelReader(string path)
    {
        LeftNavHost.Visibility = Visibility.Collapsed;
        _rightPanelVisible = false;
        ApplyRightPanelVisibility();
        _novelReaderView ??= new NovelReaderView();
        _novelReaderView.LoadFile(path);
        _lastPage = PageHost.Content as UserControl;
        SetPage(_novelReaderView);
        RightPanelHost.Visibility = Visibility.Collapsed;
        UpdateTopBarForKind();
    }



    private void NovelBackButton_Click(object sender, RoutedEventArgs e) => ExitNovelMode();

    private void ShowLocalList()
    {
        LeftNavHost.Visibility = Visibility.Visible;
        _localView ??= new LocalView();
        _localTabContent = _localView;
        SetPage(_localView);
        ShowSidebarPanel(SearchPanelView);
        _localView.SetSearchPanel(SearchPanelView);
        _localView.OnShown();
    }

    // ====================== 窗口控制 ======================

    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public WinRect RcMonitor;
        public WinRect RcWork;
        public uint DwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public WinPoint PtReserved;
        public WinPoint PtMaxSize;
        public WinPoint PtMaxPosition;
        public WinPoint PtMinTrackSize;
        public WinPoint PtMaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    // ===== DWM 窗口圆角（Windows 11）=====
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int DwmwcpDoNotRound = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 无边框窗口：拦截 WM_GETMINMAXINFO，让最大化恰好填满当前显示器工作区（不遮挡任务栏）
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowProc);
            // 窗口本身圆角：让 DWM 裁剪窗口四角，与外层霓虹边框圆角完全重合（仅 Windows 11 生效）
            var cornerPreference = DwmwcpRound;
            _ = DwmSetWindowAttribute(source.Handle, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));
        }
    }

    private void UpdateWindowCorner()
    {
        // 最大化时窗口铺满工作区，取消圆角避免四角露桌面；还原时恢复圆角
        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }
        var preference = WindowState == WindowState.Maximized ? DwmwcpDoNotRound : DwmwcpRound;
        _ = DwmSetWindowAttribute(source.Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                mmi.PtMaxPosition.X = info.RcWork.Left;
                mmi.PtMaxPosition.Y = info.RcWork.Top;
                mmi.PtMaxSize.X = info.RcWork.Right - info.RcWork.Left;
                mmi.PtMaxSize.Y = info.RcWork.Bottom - info.RcWork.Top;
                Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => SystemCommands.MinimizeWindow(this);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => SystemCommands.CloseWindow(this);

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeIcon();
        UpdateWindowCorner();
    }

    private void UpdateMaximizeIcon()
    {
        if (MaximizeIcon is null)
        {
            return;
        }
        var maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Data = maximized ? Icons.Restore : Icons.Maximize;
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
    }
    // ====================== 登录区 ======================

    /// <summary>当前源支持登录（有收藏能力）时才显示登录区；纯免登录源（copymanga）隐藏。</summary>
    private bool ShowLoginArea => _sourceManager.Current.Info.SupportsFavorites;

    private void UpdateLoginArea()
    {
        AccountHost.Children.Clear();
        if (!ShowLoginArea)
        {
            return;
        }

        if (_session.IsLoggedIn)
        {
            // 已登录：头像圈 + 用户名 + 退出按钮，横向排列填满侧栏宽度
            var row = new DockPanel { LastChildFill = true };

            var initial = (_session.Username ?? "?").Substring(0, 1).ToUpperInvariant();
            var avatarBorder = new Border
            {
                Width = 30, Height = 30,
                CornerRadius = new CornerRadius(15),
                VerticalAlignment = VerticalAlignment.Center,
            };
            avatarBorder.SetResourceReference(Border.BackgroundProperty, "PrimaryBrush");
            var avatarText = new TextBlock
            {
                Text = initial, FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            avatarBorder.Child = avatarText;
            DockPanel.SetDock(avatarBorder, Dock.Left);

            var nameLabel = new TextBlock
            {
                Text = _session.Username ?? "已登录",
                FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var logoutBtn = new Button
            {
                Style = (Style)FindResource("IconButtonStyle"),
                ToolTip = "退出登录",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                Content = new Path
                {
                    Data = Icons.SignOut, StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Stretch = Stretch.Uniform, Width = 14, Height = 14,
                },
            };
            ((Path)logoutBtn.Content).SetBinding(Path.StrokeProperty, new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1),
            });
            logoutBtn.Click += Logout_Click;
            DockPanel.SetDock(logoutBtn, Dock.Right);

            row.Children.Add(avatarBorder);
            row.Children.Add(logoutBtn);
            row.Children.Add(nameLabel);
            AccountHost.Children.Add(row);
        }
        else
        {
            // 未登录：用户图标圆底 + 登录文字，整块可点
            var loginBtn = new Button
            {
                Cursor = System.Windows.Input.Cursors.Hand,
                Style = (Style)FindResource("AccountButtonStyle"),
            };
            loginBtn.Click += Login_Click;
            ToolTipService.SetToolTip(loginBtn, "登录以同步收藏");

            var innerPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var iconHolder = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(-2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var iconPath = new Path
            {
                Data = (Geometry)FindResource("IconUser"),
                StrokeThickness = 1.55,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.Uniform,
                Width = 15, Height = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            iconPath.SetBinding(Path.StrokeProperty, new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1),
            });
            iconHolder.Child = iconPath;
            innerPanel.Children.Add(iconHolder);

            var loginText = new TextBlock
            {
                Text = "登录", FontSize = 13, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            loginText.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1),
            });
            innerPanel.Children.Add(loginText);

            loginBtn.Content = innerPanel;
            loginBtn.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
            AccountHost.Children.Add(loginBtn);
        }
    }

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.LoginDialog { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            UpdateLoginArea();
            ToastService.Show($"欢迎回来，{_session.Username}", ToastKind.Success);
            _favoriteView?.Refresh();
        }
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        _session.Logout();
        UpdateLoginArea();
        ToastService.Show("已退出登录", ToastKind.Info);
        _favoriteView?.Refresh();
    }
}
