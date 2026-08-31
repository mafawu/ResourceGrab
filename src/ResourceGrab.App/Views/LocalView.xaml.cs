using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Dialogs;
using ResourceGrab.App.Services;
using ResourceGrab.App.ViewModels;
using ResourceGrab.Core;
using ResourceGrab.Core.Http;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Views;

/// <summary>
/// 本地模式：扫描配置的本地目录，展示已下载漫画（封面 + 名字 + 标签），
/// 点击卡片或悬停按钮打开所在文件夹。
/// 性能设计：后台线程一次性扫描全部目录（仅枚举目录，不统计图片文件），
/// UI 分页渲染（默认每页 30 张，可调 20/30/40），分格大小可缩放。
/// </summary>
public partial class LocalView : CardGridViewBase
{
    private const int DefaultPageSize = 30;

    private readonly ConfigService _config;
    private readonly LocalLibraryService _localLibrary;
    private readonly SourceManager _sourceManager;
    private readonly ComicUserDataService? _userData;
    private readonly ILogger? _logger;
    private string _filterSourceId = "";

    private bool _hasLoaded;
    private Dictionary<string, List<LocalComic>>? _cacheRoots;
    private List<LocalComic> _allComics = new();
    private List<LocalComic> _filtered = new();
    private int _page = 1;
    private int _pageSize = DefaultPageSize;
    private int _pageCount = 1;
    private bool _loading;
    private bool _backfilling;
    private string _keyword = "";
    private string _sortTag = "ModifiedDesc";
    private HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _excludedTags = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _includedAuthors = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _excludedAuthors = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<int> _includedRatings = new();
    private HashSet<int> _excludedRatings = new();
    private LocalSearchPanel? _searchPanel;
    private readonly HashSet<string> _translationAttempted = new(StringComparer.OrdinalIgnoreCase);

    public LocalView()
    {
        InitializeComponent();
        _config = App.Services.GetRequiredService<ConfigService>();
        _localLibrary = App.Services.GetRequiredService<LocalLibraryService>();
        _sourceManager = App.Services.GetRequiredService<SourceManager>();
        _userData = App.Services.GetService(typeof(ComicUserDataService)) as ComicUserDataService;
        _logger = App.Services.GetService(typeof(ILogger)) as ILogger;
        SourceFilterBox.Items.Add(new ComboBoxItem { Content = "全部", Tag = "", IsSelected = true });
        foreach (var src in _sourceManager.Sources)
        {
            SourceFilterBox.Items.Add(new ComboBoxItem { Content = src.Info.DisplayName, Tag = src.Info.Id });
        }
        PageSizeBox.Items.Add(new ComboBoxItem { Content = "每页 20", Tag = "20" });
        PageSizeBox.Items.Add(new ComboBoxItem { Content = "每页 30", Tag = "30", IsSelected = true });
        PageSizeBox.Items.Add(new ComboBoxItem { Content = "每页 40", Tag = "40" });
    }

    /// <summary>切换到此页时调用：仅首次读取上次扫描的缓存（不扫描磁盘），之后保持已浏览状态；路径扫描只通过「刷新」按钮确认执行。</summary>
    public void OnShown()
    {
        if (_loading || _hasLoaded)
        {
            return;
        }
        _ = LoadFromCacheOnlyAsync();
    }

    /// <summary>首次进入时只读取缓存展示，不枚举任何磁盘路径；真正的路径扫描只由「刷新」按钮触发。</summary>
    private async Task LoadFromCacheOnlyAsync()
    {
        if (_loading || _hasLoaded)
        {
            return;
        }
        _loading = true;
        try
        {
            var dirs = _config.Current.LocalDirs
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger?.Info($"[LocalView] 配置目录数: {dirs.Count}");
            if (dirs.Count == 0)
            {
                _allComics = new List<LocalComic>();
                _filtered = new List<LocalComic>();
                _page = 1;
                LocalItems.ItemsSource = null;
                ShowState(State.NoDirs);
                _hasLoaded = true;
                _searchPanel?.SetFilters(new Dictionary<string,int>(), new Dictionary<string,int>(), new Dictionary<int,int>());
                return;
            }

            var cachePath = AppPaths.LocalLibraryCachePath;
            var cacheRoots = await Task.Run(() => _localLibrary.LoadCache(cachePath));
            _cacheRoots = cacheRoots;

            var comics = dirs
                .Where(cacheRoots.ContainsKey)
                .SelectMany(dir => cacheRoots[dir])
                .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderByDescending(c => c.ModifiedAt)
                .ToList();

            _allComics = comics;
            // 旧缓存缺热度字段时就地回填，避免重启后仍看不到浏览/点赞/评论
            var needPatch = comics.Any(c => c.HasMetadata && string.IsNullOrWhiteSpace(c.TotalViews));
            if (needPatch)
            {
                var patched = await Task.Run(() => PatchMissingViews(comics));
                if (patched)
                {
                    try
                    {
                        foreach (var dir in dirs.Where(cacheRoots.ContainsKey))
                            cacheRoots[dir] = comics.Where(c => c.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.ModifiedAt).ToList();
                        await Task.Run(() => _localLibrary.SaveCache(cachePath, cacheRoots));
                    }
                    catch { }
                }
            }
            _hasLoaded = true;
            if (comics.Count == 0)
            {
                _filtered = new List<LocalComic>();
                _page = 1;
                LocalItems.ItemsSource = null;
                EmptyTitleText.Text = "所选目录下还没有已下载的漫画";
                EmptySubtitleText.Text = "点击「刷新」按钮扫描本地目录";
                ShowState(State.Empty);
                _searchPanel?.SetFilters(new Dictionary<string,int>(), new Dictionary<string,int>(), new Dictionary<int,int>());
                return;
            }

            RebuildFilter();
            _searchPanel?.SetFilters(BuildTagCounts(), BuildAuthorCounts(), BuildRatingCounts());
        }
        catch (Exception ex)
        {
            _allComics = new List<LocalComic>();
            _filtered = new List<LocalComic>();
            _page = 1;
            LocalItems.ItemsSource = null;
            ShowState(State.Empty);
            try { ToastService.ShowError(ex); } catch { }
        }
        finally
        {
            _loading = false;
        }
    }

    public Task RequestRefreshAsync()
    {
        return LoadAsync(incremental: false);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "手动刷新将对全部本地目录进行全量重新扫描，目录较多时可能较慢。是否继续？",
            "全量重新扫描",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            _ = LoadAsync(incremental: false);
        }
    }

    public async Task<bool> OpenManageDirsDialogAsync(Window owner)
    {
        var dialog = new LocalDirsDialog { Owner = owner };
        if (dialog.ShowDialog() == true)
        {
            await LoadAsync(incremental: false);
            return true;
        }
        return false;
    }

    private async void ManageDirsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LocalDirsDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            await LoadAsync(incremental: false);
        }
    }

    /// <summary>
    /// 补全已有漫画缺失的元数据字段：按 album.json 中的 ID 从服务器拉取最新详情
    /// （浏览量/点赞/发布日期/章节/相关推荐/收藏状态等）写回 album.json。
    /// 仅处理本地已有漫画，不触发路径扫描；保留已翻译的中文名。
    /// </summary>
    private async void BackfillButton_Click(object sender, RoutedEventArgs e)
    {
        if (_backfilling)
        {
            return;
        }

        var candidates = _allComics.Where(c => c.AlbumId is > 0).ToList();
        if (candidates.Count == 0)
        {
            ToastService.Show("没有可补全的漫画（缺少专辑 ID）", ToastKind.Info);
            return;
        }

        var result = MessageBox.Show(
            $"将从服务器获取 {candidates.Count} 部已有漫画的完整元数据（浏览量/点赞/发布日期/章节/相关推荐等），\n耗时取决于数量，期间请保持网络通畅。是否继续？",
            "补全元数据",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _backfilling = true;
        var client = App.Services.GetRequiredService<JmHttpClient>();
        var total = candidates.Count;
        var done = 0;
        var skipped = 0;
        var failed = 0;
        using var semaphore = new SemaphoreSlim(2);
        var logPath = AppPaths.MetadataBackfillLogPath;
        try { File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 开始补全元数据，共 {total} 部\r\n"); } catch { }
        try
        {
            var tasks = candidates.Select(async comic =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // 已补全过则跳过：元数据有 total_views 且本地已有 comments.json 则视为已补全
                    var existing = _localLibrary.ReadMetadata(comic.Path);
                    var hasComments = _localLibrary.LoadLocalComments(comic.Path) is { } cc && cc.comments.Count > 0;
                    var metaDone = existing is not null && !string.IsNullOrEmpty(existing.TotalViews);
                    if (metaDone && hasComments)
                    {
                        Interlocked.Increment(ref skipped);
                        return;
                    }
                    var needMeta = !metaDone;
                    var needComments = !hasComments;
                    if (needMeta)
                    {
                        var album = await client.GetAlbumAsync(comic.AlbumId!.Value);
                        _localLibrary.SaveMetadataFromApi(comic.Path, AlbumBuilder.Build(album, _config.Current.DownloadDir));
                    }
                    if (needComments)
                    {
                        try { await _localLibrary.FetchAndSaveAllCommentsAsync(client, comic.AlbumId!.Value, comic.Path); } catch (Exception cex) { try { File.AppendAllText(logPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] 评论落盘失败 ID=" + comic.AlbumId + " " + cex.Message + "\r\n"); } catch { } }
                    }
                    try { File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 成功 ID={comic.AlbumId} 路径={comic.Path}\r\n"); } catch { }
                    await Task.Delay(200);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    try { File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 失败 ID={comic.AlbumId} 路径={comic.Path}\r\n{ex}\r\n"); } catch { }
                }
                finally
                {
                    var processed = Interlocked.Increment(ref done);
                    Dispatcher.Invoke(() => LocalCount.Text = $"补全元数据 {processed}/{total}");
                    semaphore.Release();
                }
            });
            await Task.WhenAll(tasks);
        }
        finally
        {
            _backfilling = false;
        }

        var updated = done - skipped - failed;
        LocalCount.Text = _allComics.Count == 0 ? "" : $"共 {_allComics.Count} 部";
        var summary = $"元数据补全完成：更新 {updated} 部";
        if (skipped > 0)
        {
            summary += $"，跳过 {skipped} 部";
        }
        if (failed > 0)
        {
            summary += $"，失败 {failed} 部";
        }
        ToastService.Show(summary, failed > 0 ? ToastKind.Error : ToastKind.Success);
    }

    /// <summary>
    /// 加载本地漫画：incremental=true 时复用缓存做增量扫描，false 时全量重新扫描全部目录。
    /// 当前仅在用户手动「刷新/重新扫描」或确认目录配置变更后调用。
    /// </summary>
    private async Task LoadAsync(bool incremental = true)
    {
        if (_loading)
        {
            return;
        }
        _loading = true;
        ShowState(State.Loading);
        _logger?.Info($"[LocalView] LoadAsync 开始, incremental={incremental}");
        try
        {
            var dirs = _config.Current.LocalDirs
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger?.Info($"[LocalView] 配置目录数: {dirs.Count}");
            if (dirs.Count == 0)
            {
                _allComics = new List<LocalComic>();
                _filtered = new List<LocalComic>();
                _page = 1;
                LocalItems.ItemsSource = null;
                ShowState(State.NoDirs);
                _hasLoaded = true;
                _searchPanel?.SetFilters(new Dictionary<string,int>(), new Dictionary<string,int>(), new Dictionary<int,int>());
                return;
            }

            var cachePath = AppPaths.LocalLibraryCachePath;
            var cacheRoots = await Task.Run(() => _localLibrary.LoadCache(cachePath));
            var comics = await Task.Run(() =>
            {
                var merged = new List<LocalComic>();
                foreach (var dir in dirs)
                {
                    cacheRoots.TryGetValue(dir, out var cached);
                    var scanned = incremental
                        ? _localLibrary.ScanIncremental(dir, cached ?? new List<LocalComic>())
                        : _localLibrary.Scan(dir, countImages: true);
                    cacheRoots[dir] = scanned;
                    merged.AddRange(scanned);
                }
                return merged
                    .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderByDescending(c => c.ModifiedAt)
                    .ToList();
            });
            _logger?.Info($"[LocalView] 扫描完成, 漫画数: {comics.Count}");
            var backfilled = await Task.Run(() => _localLibrary.BackfillExtractedNames(comics));
            await Task.Run(() => _localLibrary.SaveCache(cachePath, cacheRoots));
            _cacheRoots = cacheRoots;

            _allComics = comics;
            _hasLoaded = true;
            RebuildFilter();
            _searchPanel?.SetFilters(BuildTagCounts(), BuildAuthorCounts(), BuildRatingCounts());
            _ = TranslateMissingNamesAsync();
        }
        catch (Exception ex)
        {
            _allComics = new List<LocalComic>();
            _filtered = new List<LocalComic>();
            _page = 1;
            LocalItems.ItemsSource = null;
            ShowState(State.Empty);
            try { ToastService.ShowError(ex); } catch { }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>对缺少中文名的漫画调用翻译接口（需在 config.json 配置 titleTranslate），结果写回缓存与 album.json。</summary>
    private async Task TranslateMissingNamesAsync()
    {
        try
        {
        var options = _config.Current.TitleTranslate;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return;
        }

        var missing = _allComics.Where(c => string.IsNullOrWhiteSpace(c.NameCn) || TitleTranslator.LooksUnfinished(c.NameCn)).ToList();
        missing = missing.Where(c => _translationAttempted.Add(c.Path)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var translated = await Task.Run(() => _localLibrary.ApplyTranslationsAsync(missing));
        if (translated <= 0)
        {
            return;
        }

        if (_cacheRoots is not null)
        {
            await Task.Run(() => _localLibrary.SaveCache(AppPaths.LocalLibraryCachePath, _cacheRoots));
        }

        // 重新绑定卡片，展示新翻译的中文名
        RenderPage();
        }
        catch { }
    }

    /// <summary>渲染当前页卡片（替换 ItemsSource 触发一次刷新，避免逐条通知）。</summary>
    private void RenderPage()
    {
        var start = (_page - 1) * _pageSize;
        var count = Math.Min(_pageSize, Math.Max(0, _filtered.Count - start));
        var items = new List<LocalComicViewModel>(count);
        for (var i = 0; i < count; i++)
        {
            items.Add(ToCard(_filtered[start + i]));
        }
        LocalItems.ItemsSource = items;
        LocalItems.ScrollToTop();
    }

    /// <summary>更新分页状态（页码/总页数/按钮可用性）。</summary>
    private void UpdatePaging()
    {
        _pageCount = _filtered.Count == 0 ? 0 : (int)Math.Ceiling(_filtered.Count / (double)_pageSize);
        PageText.Text = _pageCount == 0 ? "" : $"第 {_page} / {_pageCount} 页";
        PrevPageButton.IsEnabled = _page > 1;
        NextPageButton.IsEnabled = _page > 0 && _page < _pageCount;
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e) => GoToPage(_page - 1);

    private void NextPage_Click(object sender, RoutedEventArgs e) => GoToPage(_page + 1);

    private void JumpPageBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
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
        GoToPage(Math.Clamp(page, 1, Math.Max(1, _pageCount)));
        JumpPageBox.Clear();
    }

    private void GoToPage(int page)
    {
        if (page < 1 || page > _pageCount || page == _page)
        {
            return;
        }
        _page = page;
        UpdatePaging();
        RenderPage();
    }

    private void PageSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageSizeBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var size) && size != _pageSize)
        {
            _pageSize = size;
            if (_hasLoaded)
            {
                _page = 1;
                UpdatePaging();
                RenderPage();
            }
        }
    }

    /// <summary>绑定右侧本地搜索工具；已加载时推送标签列表。</summary>
    public void SetSearchPanel(LocalSearchPanel panel)
    {
        if (_searchPanel != null) { _searchPanel.SearchChanged -= ApplySearch; _searchPanel.FilterChanged -= ApplyFilter; }
        _searchPanel = panel;
        _searchPanel.SearchChanged += ApplySearch;
        _searchPanel.FilterChanged += ApplyFilter;
        if (_hasLoaded)
        {
            _searchPanel.SetFilters(BuildTagCounts(), BuildAuthorCounts(), BuildRatingCounts());
        }
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            _sortTag = tag;
        }
        else if (SortBox.SelectedItem is ComboBoxItem c && c.Content is string s)
        {
            _sortTag = s;
        }
        if (_hasLoaded)
        {
            RebuildFilter();
        }
    }

    private void SourceFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceFilterBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            _filterSourceId = tag ?? "";
        }
        else
        {
            _filterSourceId = "";
        }
        if (_hasLoaded)
        {
            RebuildFilter();
        }
    }

    /// <summary>应用本地搜索条件（关键字 + 标签），实时过滤列表。</summary>
    public void ApplySearch(string keyword, IReadOnlyCollection<string> tags) => ApplyFilter(new LocalSearchPanel.LocalFilterState(keyword, tags, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<int>(), Array.Empty<int>()));
    public void ApplyFilter(LocalSearchPanel.LocalFilterState state)
    {
        _keyword = state.Keyword;
        _selectedTags = new HashSet<string>(state.IncludedTags, StringComparer.OrdinalIgnoreCase);
        _excludedTags = new HashSet<string>(state.ExcludedTags, StringComparer.OrdinalIgnoreCase);
        _includedAuthors = new HashSet<string>(state.IncludedAuthors, StringComparer.OrdinalIgnoreCase);
        _excludedAuthors = new HashSet<string>(state.ExcludedAuthors, StringComparer.OrdinalIgnoreCase);
        _includedRatings = new HashSet<int>(state.IncludedRatings);
        _excludedRatings = new HashSet<int>(state.ExcludedRatings);
        if (_hasLoaded)
        {
            RebuildFilter();
        }
    }

    /// <summary>按关键字（漫画名/中文名/作者/标签）与已选标签重建过滤结果。</summary>
    private void RebuildFilter()
    {
        var query = _allComics.Where(MatchesFilter);
        _filtered = _sortTag switch
        {
            "ModifiedAsc" => query.OrderBy(c => c.ModifiedAt).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "NameAsc" => query.OrderBy(c => c.NameCn, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "NameDesc" => query.OrderByDescending(c => c.NameCn, StringComparer.OrdinalIgnoreCase).ThenByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "ChapterDesc" => query.OrderByDescending(c => c.ChapterCount).ThenByDescending(c => c.ModifiedAt).ToList(),
            "ImageDesc" => query.OrderByDescending(c => c.ImageCount).ThenByDescending(c => c.ModifiedAt).ToList(),
            "ViewsDesc" => query.OrderByDescending(c => ParseCount(c.TotalViews)).ThenByDescending(c => c.ModifiedAt).ToList(),
            "PopularityDesc" => query.OrderByDescending(c => ParseCount(c.Likes)).ThenByDescending(c => ParseCount(c.TotalViews)).ThenByDescending(c => c.ModifiedAt).ToList(),
            "CommentsDesc" => query.OrderByDescending(c => ParseCount(c.CommentTotal)).ThenByDescending(c => c.ModifiedAt).ToList(),
            "RatingDesc" => query.OrderByDescending(c => c.Rating).ThenByDescending(c => c.ModifiedAt).ToList(),
            "LastReadDesc" => query.OrderByDescending(c => c.LastReadAt).ThenByDescending(c => c.ModifiedAt).ToList(),
            "ReadCountDesc" => query.OrderByDescending(c => c.ReadCount).ThenByDescending(c => c.ModifiedAt).ToList(),
            _ => query.OrderByDescending(c => c.ModifiedAt).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
        _page = 1;

        if (_filtered.Count == _allComics.Count)
        {
            LocalCount.Text = _allComics.Count == 0 ? "" : $"共 {_allComics.Count} 部";
        }
        else
        {
            LocalCount.Text = $"匹配 {_filtered.Count} / {_allComics.Count} 部";
        }

        var filtering = _selectedTags.Count > 0 || _excludedTags.Count > 0 || _includedAuthors.Count > 0 || _excludedAuthors.Count > 0 || _includedRatings.Count > 0 || _excludedRatings.Count > 0 || !string.IsNullOrWhiteSpace(_keyword);
        EmptyTitleText.Text = filtering ? "没有匹配的漫画" : "所选目录下还没有已下载的漫画";
        EmptySubtitleText.Text = filtering ? "换个关键字或标签试试" : "去搜索页下载漫画后即可在这里看到";

        ShowState(_filtered.Count == 0 ? State.Empty : State.Result);
        UpdatePaging();
        RenderPage();
    }

    private bool MatchesFilter(LocalComic comic)
    {
        if (!string.IsNullOrEmpty(_filterSourceId))
        {
            var effectiveId = string.IsNullOrEmpty(comic.SourceId) ? "jm" : comic.SourceId;
            if (!string.Equals(effectiveId, _filterSourceId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        if (_selectedTags.Count > 0 && !comic.Tags.Any(t => _selectedTags.Contains(t)))
        {
            return false;
        }
        if (_excludedTags.Count > 0 && comic.Tags.Any(t => _excludedTags.Contains(t)))
        {
            return false;
        }
        if (_includedAuthors.Count > 0 && !comic.Author.Any(a => _includedAuthors.Contains(a)))
        {
            return false;
        }
        if (_excludedAuthors.Count > 0 && comic.Author.Any(a => _excludedAuthors.Contains(a)))
        {
            return false;
        }
        if (_includedRatings.Count > 0 && !_includedRatings.Contains(comic.Rating)) return false;
        if (_excludedRatings.Count > 0 && _excludedRatings.Contains(comic.Rating)) return false;
        if (string.IsNullOrWhiteSpace(_keyword))
        {
            return true;
        }
        return comic.NameCn.Contains(_keyword, StringComparison.OrdinalIgnoreCase)
            || comic.Name.Contains(_keyword, StringComparison.OrdinalIgnoreCase)
            || comic.Author.Any(a => a.Contains(_keyword, StringComparison.OrdinalIgnoreCase))
            || comic.Tags.Any(t => t.Contains(_keyword, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>统计本地漫画标签计数（仿 Green FilterSidebar items.count）。</summary>
    private Dictionary<string,int> BuildTagCounts()
    {
        return _allComics.SelectMany(c => c.Tags).Where(t => !string.IsNullOrWhiteSpace(t)).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
    }
    private Dictionary<int,int> BuildRatingCounts()
    {
        return _allComics.Where(c => c.Rating > 0).GroupBy(c => c.Rating).ToDictionary(g => g.Key, g => g.Count());
    }
    private Dictionary<string,int> BuildAuthorCounts()
    {
        return _allComics.SelectMany(c => c.Author).Where(a => !string.IsNullOrWhiteSpace(a)).GroupBy(a => a, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
    }
    [Obsolete("Use BuildTagCounts")] private List<string> BuildTagList()
    {
        return _allComics
            .SelectMany(c => c.Tags)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .Take(60)
            .ToList();
    }

    private LocalComicViewModel ToCard(LocalComic comic)
    {
        var tags = comic.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        var displayTags = tags.Take(3).ToList();

        var stats = $"{comic.ChapterCount} 章";
        if (comic.ImageCount > 0)
        {
            stats += $" · {comic.ImageCount} 页";
        }

        return new LocalComicViewModel
        {
            Name = comic.Name,
            NameCn = comic.NameCn,
            CoverPath = comic.CoverPath,
            FolderPath = comic.Path,
            Tags = tags,
            DisplayTags = displayTags,
            ChapterCount = comic.ChapterCount,
            ImageCount = comic.ImageCount,
            HasMetadata = comic.HasMetadata,
            StatsText = stats,
            Source = comic,
            OpenFolderCommand = new Common.RelayCommand(_ => OpenFolder(comic.Path)),
            OpenReaderCommand = new Common.RelayCommand(_ => Common.Navigation.OpenReader(comic)),
        };
    }

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            ToastService.Show("目录不存在或已被移动", ToastKind.Error);
            return;
        }
        try
        {
            Process.Start("explorer.exe", path);
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex, "打开目录失败：");
        }
    }

    private static long ParseCount(string raw){ if(string.IsNullOrWhiteSpace(raw)) return 0; var s=raw.Trim().Replace(",", ""); if(long.TryParse(s,out var n)) return n; return 0; }

    private bool PatchMissingViews(List<LocalComic> comics)
    {
        var any=false;
        for(var i=0;i<comics.Count;i++)
        {
            var c=comics[i];
            if(!c.HasMetadata || !string.IsNullOrWhiteSpace(c.TotalViews)) continue;
            var md=_localLibrary.ReadMetadata(c.Path);
            if(md is null || string.IsNullOrWhiteSpace(md.TotalViews)) continue;
            comics[i]=new LocalComic{SourceId=c.SourceId,AlbumId=c.AlbumId,Name=c.Name,NameCn=c.NameCn,Path=c.Path,CoverPath=c.CoverPath,Tags=c.Tags,Author=c.Author,ChapterCount=c.ChapterCount,ImageCount=c.ImageCount,ModifiedAt=c.ModifiedAt,MetadataStamp=c.MetadataStamp,HasMetadata=c.HasMetadata,TotalViews=md.TotalViews,Likes=md.Likes,CommentTotal=md.CommentTotal,ReadImageCount=c.ReadImageCount,TotalImageCount=c.TotalImageCount,FirstReadAt=c.FirstReadAt,LastReadAt=c.LastReadAt,ReadCount=c.ReadCount,Rating=c.Rating,Notes=c.Notes};
            any=true;
        }
        return any;
    }

    private enum State
    {
        NoDirs,
        Loading,
        Empty,
        Result,
    }

    private void ShowState(State state)
    {
        NoDirsPanel.Visibility = state == State.NoDirs ? Visibility.Visible : Visibility.Collapsed;
        LoadingPanel.Visibility = state == State.Loading ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = state == State.Empty ? Visibility.Visible : Visibility.Collapsed;
        LocalItems.Visibility = state == State.Result ? Visibility.Visible : Visibility.Collapsed;
    }
}

