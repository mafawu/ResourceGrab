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
    private NovelReaderView? _novelReaderView;

    /// <summary>章节详情页缓存：按最近访问 LRU 淘汰，限制常驻内存。</summary>
    private const int MaxCachedChapterViews = 6;
    private readonly Dictionary<string, ChapterView> _chapterViews = new();
    private readonly LinkedList<string> _chapterOrder = new();
    private UserControl? _lastPage;
    private bool _panelVisible;
    private ResourceKind _currentKind = ResourceKind.Manga;

    /// <summary>窗口标题（copymanga 版可覆盖）。</summary>
    protected virtual string WindowTitle => "抓资源";

    private LocalSearchPanel? _searchPanel;
    private LocalComicDetailPanel? _localDetailPanel;
    private NovelSearchPanel NovelSearchPanelView => _novelSearchPanel ??= new NovelSearchPanel();
    private LocalComicDetailPanel LocalDetailPanelView => _localDetailPanel ??= new LocalComicDetailPanel();
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
        UpdatePanelVisibility();

        _session = App.Services.GetRequiredService<SessionService>();
        _sourceManager = App.Services.GetRequiredService<SourceManager>();
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
                PageHost.Content = _novelView ?? new NovelLocalView();
                RightPanelHost.Content = _novelSearchPanel ?? new NovelSearchPanel();
                RightPanelHost.Visibility = Visibility.Visible;
                LeftNavHost.Visibility = Visibility.Collapsed;
                _panelVisible = true;
                UpdatePanelVisibility();
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
                // 在线阅读器返回：回到章节详情页
                PageHost.Content = _lastPage;
                return;
            }
            if (ReferenceEquals(PageHost.Content, _localTabContent) && _localTabContent is ReaderView)
            {
                // 阅读页返回：回到本地漫画列表（右侧恢复本地搜索工具）
                ShowLocalList();
                return;
            }
            HideRightPanel();
            PageHost.Content = _lastPage;
        };

        RefreshSourceBoxForKind(_currentKind);
        _sourceManager.CurrentChanged += () => Dispatcher.Invoke(UpdateNavCapabilities);
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
        UpdatePanelVisibility();
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
            // 小说模式隐藏登录区（本地无需登录）
            if (AccountHost != null) AccountHost.Visibility = isNovel ? Visibility.Collapsed : Visibility.Visible;

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


    // ====================== 导航 ======================

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHost is null)
        {
            return;
        }
        LeftNavHost.Visibility = Visibility.Visible;
        HideRightPanel();
        if (ReferenceEquals(sender, NavSearch))
        {
            // 从详情页等页面进入搜索时，保留返回目标。
            if (!ReferenceEquals(PageHost.Content, _searchView) && PageHost.Content is UserControl page)
                _lastPage = page;
            else
                _lastPage = _searchView;
            PageHost.Content = _searchView;
        }
        else if (ReferenceEquals(sender, NavRank))
        {
            if (_sourceManager.Current is IRankSource)
            {
                _rankBrowseView ??= new RankBrowseView();
                _lastPage = _rankBrowseView;
                PageHost.Content = _rankBrowseView;
                _rankBrowseView.OnShown();
            }
            else
            {
                _rankView ??= new RankView();
                _lastPage = _rankView;
                PageHost.Content = _rankView;
                _rankView.OnShown();
            }
        }
        else if (ReferenceEquals(sender, NavCategory))
        {
            _categoryView ??= new CategoryView();
            _lastPage = _categoryView;
            PageHost.Content = _categoryView;
            _categoryView.OnShown();
        }
        else if (ReferenceEquals(sender, NavFavorite))
        {
            _favoriteView ??= new FavoriteView();
            _lastPage = _favoriteView;
            PageHost.Content = _favoriteView;
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
                _panelVisible = true;
                UpdatePanelVisibility();
                PageHost.Content = _localTabContent;
            }
        }
        else if (ReferenceEquals(sender, NavWeekly))
        {
            _weeklyView ??= new WeeklyView();
            _lastPage = _weeklyView;
            PageHost.Content = _weeklyView;
            _weeklyView.OnShown();
        }
        UpdateTopBarForKind();
    }

    private void OpenComic(string sourceId, string comicId)
    {
        HideRightPanel();
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
        PageHost.Content = view;
    }

    private void OpenRank(RankPeriod period)
    {
        HideRightPanel();
        _rankView ??= new RankView();
        _lastPage = _rankView;
        PageHost.Content = _rankView;
        _rankView.OnShown(period);
    }

    private void OpenOnlineReader(IComicSource source, IReadOnlyList<Chapter> chapters, int startIndex)
    {
        HideRightPanel();
        _lastPage = (UserControl)PageHost.Content;
        PageHost.Content = new OnlineReaderView(source, chapters, startIndex);
    }

    private void OpenSearch(string keyword, string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return;
        }

        HideRightPanel();

        if (!ReferenceEquals(PageHost.Content, _searchView))
        {
            if (PageHost.Content is UserControl page)
            {
                _lastPage = page;
            }

            NavSearch.IsChecked = true;
            PageHost.Content = _searchView;
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
        _panelVisible = true;
        UpdatePanelVisibility();
        _localTabContent = new ReaderView(comic);
        PageHost.Content = _localTabContent;
    }

    /// <summary>本地列表点击卡片：右侧切换到本地漫画详情面板（检查更新/更新下载）。</summary>
    private void OpenLocalDetail(LocalComic comic)
    {
        RightPanelHost.Content = LocalDetailPanelView;
        LocalDetailPanelView.Show(comic);
        _panelVisible = true;
        UpdatePanelVisibility();
    }
    // ====================== 内容源切换 ======================

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceBox.SelectedItem is ComboBoxItem { Tag: IComicSource source })
        {
            _sourceManager.Current = source;
        }
    }

    /// <summary>按当前源能力显隐导航项；切源后把不能由新源服务的页面重置回搜索页。</summary>
    private void UpdateNavCapabilities()
    {
        var info = _sourceManager.Current.Info;
        NavRank.Visibility = info.SupportsRank ? Visibility.Visible : Visibility.Collapsed;
        NavWeekly.Visibility = info.SupportsWeekly ? Visibility.Visible : Visibility.Collapsed;
        NavCategory.Visibility = info.SupportsCategories ? Visibility.Visible : Visibility.Collapsed;
        NavFavorite.Visibility = info.SupportsFavorites ? Visibility.Visible : Visibility.Collapsed;

        var onUnsupportedPage = (NavWeekly.IsChecked == true && !info.SupportsWeekly)
                               || (NavFavorite.IsChecked == true && !info.SupportsFavorites)
                               || (NavRank.IsChecked == true && !info.SupportsRank);
        if (onUnsupportedPage)
        {
            NavSearch.IsChecked = true;
            _lastPage = _searchView;
            PageHost.Content = _searchView;
            return;
        }

        // 排行页按源切换：IRankSource 用通用排行浏览，禁漫保留原排行页
        if (NavRank.IsChecked == true)
        {
            if (_sourceManager.Current is IRankSource && !ReferenceEquals(PageHost.Content, _rankBrowseView))
            {
                _rankBrowseView ??= new RankBrowseView();
                _lastPage = _rankBrowseView;
                PageHost.Content = _rankBrowseView;
                _rankBrowseView.OnShown();
            }
            else if (_sourceManager.Current is not IRankSource && !ReferenceEquals(PageHost.Content, _rankView))
            {
                _rankView ??= new RankView();
                _lastPage = _rankView;
                PageHost.Content = _rankView;
                _rankView.OnShown();
            }
        }

        // 分类页按源切换：支持 ICategorySource 的源用通用分类浏览，禁漫保留主题页
        if (NavCategory.IsChecked == true)
        {
            if (_sourceManager.Current is ICategorySource && !ReferenceEquals(PageHost.Content, _categoryBrowseView))
            {
                _categoryBrowseView ??= new CategoryBrowseView();
                _lastPage = _categoryBrowseView;
                PageHost.Content = _categoryBrowseView;
                _categoryBrowseView.OnShown();
            }
            else if (_sourceManager.Current is not ICategorySource && !ReferenceEquals(PageHost.Content, _categoryView))
            {
                _categoryView ??= new CategoryView();
                _lastPage = _categoryView;
                PageHost.Content = _categoryView;
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
        _panelVisible = !_panelVisible;
        UpdatePanelVisibility();
    }

    private void UpdatePanelVisibility()
    {
        // 视频页等场景可能把整个宿主设为 Collapsed；
        // 这里统一恢复，避免列宽展开后右侧内容仍是空白。
        RightPanelHost.Visibility = _panelVisible ? Visibility.Visible : Visibility.Collapsed;

        if (_panelVisible)
        {
            // 显示侧栏：恢复可拖动范围与宽度（MinWidth 会顶住 Width=0，收起时必须先放开）
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
        PanelSplitter.Visibility = _panelVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>离开阅读页（进入列表/详情等页面）时：隐藏右侧面板，恢复下载队列内容。</summary>
    private void HideRightPanel()
    {
        RightPanelHost.Content = DownloadPanelView;
        _panelVisible = false;
        UpdatePanelVisibility();
    }

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
                PageHost.Content = _lastNovelContent;
                RightPanelHost.Visibility = Visibility.Collapsed;
                LeftNavHost.Visibility = Visibility.Collapsed;
                _panelVisible = false;
                UpdatePanelVisibility();
                UpdateTopBarForKind();
                return;
            }
            _novelView ??= new NovelLocalView();
            if (_novelSearchPanel == null) _novelSearchPanel = new NovelSearchPanel();
            _novelView.SetSearchPanel(_novelSearchPanel);
            _lastPage = _novelView;
            PageHost.Content = _novelView;
            RightPanelHost.Content = _novelSearchPanel;
            RightPanelHost.Visibility = Visibility.Visible;
            LeftNavHost.Visibility = Visibility.Collapsed;
            _panelVisible = true;
            UpdatePanelVisibility();
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
            _videoView ??= new VideoView();
            _lastPage = _videoView;
            PageHost.Content = _videoView;
            RightPanelHost.Visibility = Visibility.Collapsed;
            LeftNavHost.Visibility = Visibility.Collapsed;
            _panelVisible = false;
            UpdatePanelVisibility();
            UpdateTopBarForKind();
            _videoView.OnShown();
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
            _panelVisible = true;
        }
        else
        {
            HideRightPanel();
            content ??= _searchView;
        }
        PageHost.Content = content;
        _lastPage = content;
        UpdatePanelVisibility();
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
        _panelVisible = false;
        UpdatePanelVisibility();
        _novelReaderView ??= new NovelReaderView();
        _novelReaderView.LoadFile(path);
        _lastPage = PageHost.Content as UserControl;
        PageHost.Content = _novelReaderView;
        RightPanelHost.Visibility = Visibility.Collapsed;
        UpdateTopBarForKind();
    }



    private void NovelBackButton_Click(object sender, RoutedEventArgs e) => ExitNovelMode();

    private void ShowLocalList()
    {
        LeftNavHost.Visibility = Visibility.Visible;
        _localView ??= new LocalView();
        _localTabContent = _localView;
        PageHost.Content = _localView;
        RightPanelHost.Content = SearchPanelView;
        _localView.SetSearchPanel(SearchPanelView);
        _panelVisible = true;
        UpdatePanelVisibility();
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


















