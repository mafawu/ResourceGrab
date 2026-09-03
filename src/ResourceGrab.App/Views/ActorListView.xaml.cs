using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Services;
using ResourceGrab.Core;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;

namespace ResourceGrab.App.Views;

/// <summary>演员卡片数据：名字 + 作品数 + 代表作海报（无海报时 Poster 为空，卡片显示首字回退）。</summary>
public sealed record ActorCardItem(string Name, int Count, string? Poster)
{
    public string Fallback => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
    public string CountText => $"{Count} 部";
}

/// <summary>
/// 演员列表页：海报卡片形式浏览本地库全部演员（海报取该演员最新一部有封面的作品），
/// 支持搜索过滤、按作品数/名称排序、分页浏览。
/// 全量统计与海报映射在 Refresh() 时缓存一次，翻页/过滤不重算；
/// 外部（VideoView 切导航）调用 Refresh() 使缓存失效。
/// </summary>
public partial class ActorListView : UserControl
{
    private readonly VideoLibraryService _library;

    /// <summary>全量有序缓存：Refresh() 重建；翻页/过滤只在此切片。</summary>
    private List<KeyValuePair<string, int>> _ordered = [];
    private List<KeyValuePair<string, int>> _filtered = [];
    private Dictionary<string, string?> _posters = new(StringComparer.OrdinalIgnoreCase);
    private string _searchText = "";
    private bool _sortByName;
    private int _page = 1;
    private int _pageSize = 40;
    private int _pageCount = 1;
    private bool _fetchingAvatars;

    public Action<string>? ActorSelected;

    public ActorListView()
    {
        InitializeComponent();
        _library = App.Services.GetRequiredService<VideoLibraryService>();
        Loaded += (_, _) => Refresh();
    }

    /// <summary>重建统计/海报缓存并回到第一页。翻页/过滤请走内部方法，不要调这个。</summary>
    public void Refresh()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ordered = _library.GetActorCounts()
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        BuildPosterMap();
        _page = 1;
        ApplyFilterAndRender();
        App.Services.GetRequiredService<ResourceGrab.Core.Logging.ILogger>().Info($"[ActorListView] Refresh 耗时 {sw.ElapsedMilliseconds} ms ({_ordered.Count} 位演员)");
    }

    /// <summary>演员 → 卡片图：优先 GFriends 本地头像，其次该演员最新一部有封面的作品海报（零联网，纯本地映射）。</summary>
    private void BuildPosterMap()
    {
        _posters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var avatarDir = System.IO.Path.Combine(AppPaths.AppDataDir, "avatars");

        foreach (var (actor, _) in _ordered)
        {
            var avatar = System.IO.Path.Combine(avatarDir, GFriendsSource.AvatarFileName(actor) + ".jpg");
            if (File.Exists(avatar)) _posters[actor] = avatar;
        }

        foreach (var item in _library.Items.OrderByDescending(i => i.AddedDate))
        {
            if (_posters.Count >= _ordered.Count) break;
            var poster = !string.IsNullOrEmpty(item.PosterPath) && File.Exists(item.PosterPath) ? item.PosterPath
                : !string.IsNullOrEmpty(item.CoverPath) && File.Exists(item.CoverPath) ? item.CoverPath
                : null;
            if (poster is null) continue;
            foreach (var actor in item.Actors)
            {
                if (string.IsNullOrWhiteSpace(actor)) continue;
                if (!_posters.ContainsKey(actor))
                    _posters[actor] = poster;
            }
        }
    }

    private void ApplyFilterAndRender()
    {
        // XAML 解析期间 IsSelected="True" 会触发 SelectionChanged，此时子控件尚未创建，跳过
        if (ActorCards is null) return;

        var query = _ordered.AsEnumerable();
        var keyword = _searchText.Trim();
        if (keyword.Length > 0)
            query = query.Where(kv => kv.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        if (_sortByName)
            query = query.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase);

        _filtered = query.ToList();
        _pageCount = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)_pageSize));
        _page = Math.Clamp(_page, 1, _pageCount);

        var pageItems = _filtered.Skip((_page - 1) * _pageSize).Take(_pageSize)
            .Select(kv => new ActorCardItem(kv.Key, kv.Value, _posters.GetValueOrDefault(kv.Key)))
            .ToList();
        ActorCards.ItemsSource = pageItems;
        ActorCards.ScrollToTop();

        EmptyState.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text = _filtered.Count == 0 ? "" : $"第 {_page} / {_pageCount} 页 · 共 {_filtered.Count} 位";
        PrevPageButton.IsEnabled = _page > 1;
        NextPageButton.IsEnabled = _page < _pageCount;
    }

    private void ActorCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActorCardItem item)
            ActorSelected?.Invoke(item.Name);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text;
        _page = 1;
        ApplyFilterAndRender();
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _sortByName = SortBox.SelectedItem is ComboBoxItem { Tag: "name" };
        _page = 1;
        ApplyFilterAndRender();
    }

    private void PageSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageSizeBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var size) && size != _pageSize)
        {
            _pageSize = size;
            _page = 1;
            ApplyFilterAndRender();
        }
    }

    private void ChangePage_Click(object sender, RoutedEventArgs e)
    {
        if (sender == PrevPageButton && _page > 1) _page--;
        else if (sender == NextPageButton && _page < _pageCount) _page++;
        else return;
        ApplyFilterAndRender();
    }

    /// <summary>批量拉取全部演员的 GFriends 本人头像：并发 3、已落盘跳过、按钮显示进度，完成后刷新卡片图。</summary>
    private async void FetchAvatars_Click(object sender, RoutedEventArgs e)
    {
        if (_fetchingAvatars) return;
        var gfriends = App.Services.GetServices<IVideoActorSource>()
            .OfType<GFriendsSource>().FirstOrDefault();
        if (gfriends is null)
        {
            ToastService.Show("GFriends 源未注册", ToastKind.Error);
            return;
        }

        var actors = _ordered.Select(kv => kv.Key).ToList();
        if (actors.Count == 0)
        {
            ToastService.Show("还没有演员数据", ToastKind.Info);
            return;
        }

        _fetchingAvatars = true;
        FetchAvatarsButton.IsEnabled = false;
        RestoreCacheButton.IsEnabled = false;
        var done = 0;
        var hit = 0;
        var originalContent = FetchAvatarsButton.Content;
        var progress = new Progress<(int Done, int Total, int Hit)>(t =>
            FetchAvatarsButton.Content = $"拉取头像 {t.Done}/{t.Total}（命中 {t.Hit}）");

        try
        {
            await Task.Run(async () =>
            {
                await Parallel.ForEachAsync(actors,
                    new ParallelOptions { MaxDegreeOfParallelism = 3 },
                    async (name, ct) =>
                    {
                        var result = await gfriends.FetchAsync(name, ct);
                        Interlocked.Increment(ref done);
                        if (result.Outcome == ActorSourceOutcome.Success)
                            Interlocked.Increment(ref hit);
                        ((IProgress<(int, int, int)>)progress).Report((done, actors.Count, hit));
                    });
            });
        }
        catch (Exception ex)
        {
            ToastService.Show($"拉取中断: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            _fetchingAvatars = false;
            FetchAvatarsButton.IsEnabled = true;
            FetchAvatarsButton.Content = originalContent;
            RestoreCacheButton.IsEnabled = true;
        }

        Refresh();
        ToastService.Show(hit > 0
            ? $"头像拉取完成：命中 {hit} / {actors.Count} 位演员"
            : "没有拉到任何头像——请检查网络/代理设置（gfriends 在 GitHub 上，需配置 VideoScraping.Proxy）",
            hit > 0 ? ToastKind.Success : ToastKind.Info);
    }

    private async void RestoreCache_Click(object sender, RoutedEventArgs e)
    {
        RestoreCacheButton.IsEnabled = false;
        var restored = 0;
        try
        {
            restored = await Task.Run(() => _library.RestoreMetadataFromCacheForAll());
        }
        catch (Exception ex)
        {
            ToastService.Show($"恢复失败: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            RestoreCacheButton.IsEnabled = true;
        }

        Refresh();
        ToastService.Show(restored > 0
            ? $"已从刮削缓存恢复 {restored} 个视频的元数据"
            : "缓存中没有可恢复的数据", restored > 0 ? ToastKind.Success : ToastKind.Info);
    }
}
