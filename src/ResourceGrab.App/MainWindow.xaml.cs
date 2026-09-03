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
    private UserControl? _localTabContent;
    private WeeklyView? _weeklyView;
    private NovelLocalView? _novelView;
    private VideoView? _videoView;
    private UserControl? _lastMangaContent;
    private UserControl? _lastNovelContent;
    private UserControl? _lastVideoContent;
    private NovelSearchPanel? _novelSearchPanel;
    private VideoSearchPanel? _videoSearchPanel;
    private NovelReaderView? _novelReaderView;
    private ScrapeToolsView? _scrapeToolsView;

    /// <summary>章节详情页缓存：按最近访问 LRU 淘汰，限制常驻内存。</summary>
    private const int MaxCachedChapterViews = 6;
    private readonly Dictionary<string, ChapterView> _chapterViews = new();
    private readonly LinkedList<string> _chapterOrder = new();
    private UserControl? _lastPage;
    // 进入在线阅读器前记录详情页自己的返回目标（搜索/排行等），避免阅读器往返后详情页返回又跳回阅读器
    private UserControl? _pageBeforeOnlineReader;
    private bool _rightPanelVisible;
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
        RegisterRoutes();
        InitializeNavRail();

        _session = App.Services.GetRequiredService<SessionService>();
        _sourceManager = App.Services.GetRequiredService<SourceManager>();
        RefreshNavRailSections();
        _config = App.Services.GetRequiredService<ConfigService>();
        _localLibrary = App.Services.GetRequiredService<LocalLibraryService>();
        _searchView = new SearchView();
        _lastPage = _searchView;
        PageHost.Content = _searchView;

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
                RightPanelHost.Content = _novelSearchPanel ?? new NovelSearchPanel();
                RightPanelHost.Visibility = Visibility.Visible;
                LeftNavHost.Visibility = Visibility.Collapsed;
                _rightPanelVisible = true;
                ApplyRightPanelVisibility();
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
            SourceBox.Items.Add(new ComboBoxItem { Content = kind == ResourceKind.Novel ? "小说源 · 敬请期待" : "视频源 · 敬请期待", IsEnabled = false });
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

    private void UpdateTopBarForKind()
    {
        try
        {
            var isNovel = IsNovelMode;
            if (MangaTopActions != null) MangaTopActions.Visibility = (!isNovel && _currentKind == ResourceKind.Manga) ? Visibility.Visible : Visibility.Collapsed;
            if (VideoTopActions != null) VideoTopActions.Visibility = (!isNovel && _currentKind == ResourceKind.Video) ? Visibility.Visible : Visibility.Collapsed;

            if (SourceSwitcherHost != null)
            {
                SourceSwitcherHost.Visibility =
                    LeftNavHost.Visibility == Visibility.Visible &&
                    !isNovel &&
                    _currentKind == ResourceKind.Manga
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
            // 漫画源需要账号；视频和小说为本地/免登流程。
            if (AccountHost != null) AccountHost.Visibility = _currentKind == ResourceKind.Manga ? Visibility.Visible : Visibility.Collapsed;

        } catch {}
    }


    private void KindPill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } rb && Enum.TryParse<ResourceKind>(tag, out var kind))
        {
            if (PageHost?.Content is UserControl leaving)
            {
                if (IsNovelMode) _lastNovelContent = leaving;
                else if (IsVideoMode) _lastVideoContent = leaving;
                else _lastMangaContent = leaving;
            }
            _currentKind = kind;
            RefreshNavRailSections();
            RefreshSourceBoxForKind(kind);
            UpdateNavCapabilities();
            if (IsNovelMode && kind == ResourceKind.Manga) { ExitNovelMode(); return; }
            if (IsVideoMode && kind == ResourceKind.Manga) { ExitVideoMode(); return; }
            if (kind == ResourceKind.Novel && !IsNovelMode) { OpenNovelLocal(); return; }
            if (kind == ResourceKind.Video && !IsVideoMode) { OpenVideoView(); return; }
            UpdateTopBarForKind();
            if (kind != ResourceKind.Manga)
            {
                var name = kind == ResourceKind.Novel ? "小说" : "视频";
                try { Snackbars.Show($"{name}源整合开发中，已预留UI", Services.ToastKind.Info); } catch {}
            }
        }
    }



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
            _ => new MangaNavSectionProvider(_sourceManager.Current.Info),
        };
        NavRailControl.Sections = provider.GetSections();
    }

    private void OnNavItemClicked(object? sender, NavItem item)
    {
        if (!item.IsEnabled) return;
        switch (item.Id)
        {
            case "manga.search":
                NavigateToSearch(); break;
            case "manga.rank":
                NavigateToRank(); break;
            case "manga.category":
                NavigateToCategory(); break;
            case "manga.weekly":
                NavigateToWeekly(); break;
            case "manga.local":
                NavigateToLocal(); break;
            case "manga.favorites":
                NavigateToFavorites(); break;
            case "manga.scrape":
                NavigateToScrapeTools(); break;
            case "video.online":
                ShowVideoWithNav("search"); break;
            case "video.local":
                ShowVideoWithNav("local"); break;
            case "video.tasks":
                ShowVideoWithNav("tasks"); break;
            case "video.recommend":
                ShowVideoWithNav("recommend"); break;
            case "video.actors":
                ShowVideoWithNav("actors"); break;
            case "novel.index":
                OpenNovelLocal(); break;
        }
    }

    private void NavigateToSearch() { LeftNavHost.Visibility = Visibility.Visible; CollapseRightPanel(); PageHost.Content = _searchView; }
    private void NavigateToRank()
    {
        LeftNavHost.Visibility = Visibility.Visible; CollapseRightPanel();
        if (_sourceManager.Current is IRankSource) { _rankBrowseView ??= new RankBrowseView(); PageHost.Content = _rankBrowseView; _rankBrowseView.OnShown(); }
        else { _rankView ??= new RankView(); PageHost.Content = _rankView; _rankView.OnShown(); }
    }
    private void NavigateToCategory()
    {
        LeftNavHost.Visibility = Visibility.Visible; CollapseRightPanel();
        if (_sourceManager.Current is ICategorySource) { _categoryBrowseView ??= new CategoryBrowseView(); PageHost.Content = _categoryBrowseView; _categoryBrowseView.OnShown(); }
        else { _categoryView ??= new CategoryView(); PageHost.Content = _categoryView; _categoryView.OnShown(); }
    }
    private void NavigateToWeekly()
    {
        LeftNavHost.Visibility = Visibility.Visible; CollapseRightPanel();
        _weeklyView ??= new WeeklyView(); PageHost.Content = _weeklyView; _weeklyView.OnShown();
    }
    private void NavigateToLocal() { ShowLocalList(); }
    private void NavigateToFavorites()
    {
        LeftNavHost.Visibility = Visibility.Visible; CollapseRightPanel();
        _favoriteView ??= new FavoriteView(); PageHost.Content = _favoriteView; _favoriteView.OnShown();
    }
    private void NavigateToScrapeTools()
    {
        LeftNavHost.Visibility = Visibility.Visible;
        CollapseRightPanel();
        _scrapeToolsView ??= new ScrapeToolsView();
        _scrapeToolsView.OnShown();
        SetPage(_scrapeToolsView);
    }

    private void ShowVideoWithNav(string nav)
    {
        OpenVideoView();
        _videoView?.SwitchNav(nav);
        // 刮削任务和推荐页不需要右侧筛选面板
        if (nav is "tasks" or "recommend")
        {
            RightPanelHost.Visibility = Visibility.Collapsed;
            _rightPanelVisible = false;
            ApplyRightPanelVisibility();
        }
    }
    /// <summary>从刮削工具页跳转到视频演员工具（跨媒体类型）。</summary>
    public void OpenVideoActorsTool()
    {
        OpenVideoView();
        _videoView?.SwitchNav("actors");
    }

    // ====================== Route Registration ======================

    private void RegisterRoutes()
    {
        _navigator.Register("manga.search", () => { EnsureSearchView(); return _searchView; });
        _navigator.Register("manga.rank", () => { _rankView ??= new RankView(); return _rankView; });
        _navigator.Register("manga.category", () => { _categoryView ??= new CategoryView(); return _categoryView; });
        _navigator.Register("manga.local", () => { _localView ??= new LocalView(); return _localView; });
        _navigator.Register("manga.favorites", () => { _favoriteView ??= new FavoriteView(); return _favoriteView; });
        _navigator.Register("video.library", () => { _videoView ??= new VideoView(); return _videoView; });
        _navigator.Register("novel.index", () => { _novelView ??= new NovelLocalView(); return _novelView; });
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
            _lastPage = current;
        }
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
                RightPanelHost.Content = LocalDetailPanelView;
                _rightPanelVisible = true;
                ApplyRightPanelVisibility();
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
        RightPanelHost.Content = LocalDetailPanelView;
        LocalDetailPanelView.Show(comic, true);
        _rightPanelVisible = true;
        ApplyRightPanelVisibility();
        _localTabContent = new ReaderView(comic);
        SetPage(_localTabContent);
    }

    /// <summary>本地列表点击卡片：右侧切换到本地漫画详情面板（检查更新/更新下载）。</summary>
    private void OpenLocalDetail(LocalComic comic)
    {
        RightPanelHost.Content = LocalDetailPanelView;
        LocalDetailPanelView.Show(comic);
        _rightPanelVisible = true;
        ApplyRightPanelVisibility();
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
            (selectedId == "manga.favorites" && !info.SupportsFavorites);

        if (onUnsupportedPage)
        {
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

    private void PanelToggle_Click(object sender, RoutedEventArgs e)
    {
        _rightPanelVisible = !_rightPanelVisible;
        ApplyRightPanelVisibility();
    }

    /// <summary>只控制右侧面板列的展开收起，不影响左侧导航。</summary>
    private void ApplyRightPanelVisibility()
    {
        RightPanelHost.Visibility = _rightPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        if (_rightPanelVisible)
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
        PanelSplitter.Visibility = _rightPanelVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>只控制左侧导航栏显隐，不影响右侧面板。</summary>
    private void ShowLeftRail() => LeftNavHost.Visibility = Visibility.Visible;
    private void HideLeftRail() => LeftNavHost.Visibility = Visibility.Collapsed;

    /// <summary>设置右栏内容并展开（不影响左栏）。</summary>
    private void ShowRightContent(UserControl content)
    {
        RightPanelHost.Content = content;
        _rightPanelVisible = true;
        ApplyRightPanelVisibility();
    }

    /// <summary>隐藏右栏并恢复默认内容（不影响左栏）。</summary>
    private void CollapseRightPanel()
    {
        RightPanelHost.Content = DownloadPanelView;
        _rightPanelVisible = false;
        ApplyRightPanelVisibility();
    }

    /// <summary>视频本地详情开关：打开时收起右侧本地搜索面板，由 VideoView 内置详情栏顶替其位置；关闭时恢复。</summary>
    private void SetVideoDetailPanelOpen(bool open)
    {
        if (!ReferenceEquals(PageHost.Content, _videoView)) return;
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
            RightPanelHost.Content = _novelSearchPanel;
            RightPanelHost.Visibility = Visibility.Visible;
            LeftNavHost.Visibility = Visibility.Visible;
            _rightPanelVisible = true;
            ApplyRightPanelVisibility();
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
            RightPanelHost.Content = VideoSearchPanelView;
            LeftNavHost.Visibility = Visibility.Visible;
            _rightPanelVisible = true;
            ApplyRightPanelVisibility();
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

    /// <summary>恢复上次离开时的漫画页面（搜索/本地列表/阅读器等），保持状态不丢失。</summary>
    private void RestoreMangaContent()
    {
        LeftNavHost.Visibility = Visibility.Visible;
        var content = _lastMangaContent;
        if (content is ReaderView)
        {
            RightPanelHost.Content = LocalDetailPanelView;
            _rightPanelVisible = true;
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
        RightPanelHost.Content = SearchPanelView;
        _localView.SetSearchPanel(SearchPanelView);
        _rightPanelVisible = true;
        ApplyRightPanelVisibility();
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
            var loginBtn = new Button { Cursor = System.Windows.Input.Cursors.Hand };
            loginBtn.Click += Login_Click;
            ToolTipService.SetToolTip(loginBtn, "登录以同步收藏");

            var innerPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var iconHolder = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(9),
                BorderBrush = (Brush)FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
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
