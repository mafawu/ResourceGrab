using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Common;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Services;
using ResourceGrab.App.Shell.Providers;
using ResourceGrab.App.ViewModels;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Services.ShortDrama;
using ResourceGrab.Core.Sources.ShortDrama;

namespace ResourceGrab.App.Views;

/// <summary>短剧列表页的六种内容来源（推荐 / 上新 / 频道 / 榜单 / 搜索）。</summary>
public enum DramaBrowseMode
{
    Recommend,
    Newest,
    Channel,
    Rank,
    Search,
}

/// <summary>
/// 短剧列表页：推荐、最近上新、四个频道、榜单、搜索共用同一个视图。
/// 与写真页的"六维度共用一个浏览视图"同构——差异只在数据来源，不在版式。
/// </summary>
public partial class ShortDramaBrowseView : CardGridViewBase
{
    private readonly IShortDramaSource? _source;
    private readonly ILogger? _logger;
    private readonly ShortDramaDownloadService? _download;
    private readonly HashSet<string> _downloading = new(StringComparer.Ordinal);

    private DramaBrowseMode _mode = DramaBrowseMode.Recommend;
    private DramaRankKind _rankKind = DramaRankKind.Hot;
    private string? _scope;
    private string _keyword = "";
    private int _page = 1;
    private int? _totalPages;
    private int _loadVersion;

    private readonly List<ShortDramaCardAdapter> _items = [];

    /// <summary>请求打开某部剧（MainWindow 负责跳详情页）。</summary>
    public event EventHandler<ShortDramaSummary>? OpenRequested;

    /// <summary>请求直接播放某部剧（卡片悬停播放键）。</summary>
    public event EventHandler<ShortDramaSummary>? PlayRequested;

    public ICommand RetryCommand { get; }

    public ShortDramaBrowseView()
    {
        InitializeComponent();
        _source = App.Services?.GetService<IShortDramaSource>();
        _logger = App.Services?.GetService<ILogger>();
        _download = App.Services?.GetService<ShortDramaDownloadService>();

        RetryCommand = new RelayCommand(async _ =>
        {
            // 「换线路重试」：先刷新发布页，再重新加载当前来源
            await RefreshLineAsync(silent: true);
            await LoadAsync(1);
        });
        StateHost.RetryCommand = RetryCommand;

        // SearchWatermarkTextStyle 的触发器绑的是「祖先 TextBox」，而占位块是 TextBox 的兄弟节点，
        // 触发器永远不会命中（全站那几处也是手写显隐的），所以这里显式接管。
        SearchBox.TextChanged += (_, _) => UpdateSearchPlaceholder();
        UpdateSearchPlaceholder();

        Loaded += (_, _) => UpdateLineCard();
    }

    // ====================== 对外入口 ======================

    /// <summary>切换内容来源并重新加载。</summary>
    public void ShowMode(DramaBrowseMode mode, string? scope = null, string? title = null)
    {
        _mode = mode;
        _scope = scope;
        _keyword = "";
        _page = 1;
        _totalPages = null;
        SearchBox.Text = "";

        switch (mode)
        {
            case DramaBrowseMode.Channel:
                TitleText.Text = title ?? ChannelTitle(scope);
                SubtitleText.Text = "频道";
                break;
            case DramaBrowseMode.Newest:
                TitleText.Text = "最近上新";
                SubtitleText.Text = "按更新顺序";
                break;
            case DramaBrowseMode.Rank:
                TitleText.Text = "榜单";
                SubtitleText.Text = "热播 · TOP 20";
                break;
            case DramaBrowseMode.Search:
                TitleText.Text = "搜索";
                break;
            default:
                TitleText.Text = "推荐";
                SubtitleText.Text = "编辑精选";
                break;
        }

        _ = LoadAsync(1);
        UpdateLineCard();
    }

    /// <summary>带关键词进入搜索态（由主窗口的全局搜索调用）。</summary>
    public void ShowSearch(string keyword)
    {
        ShowMode(DramaBrowseMode.Search);
        _keyword = keyword ?? "";
        SearchBox.Text = _keyword;
        _ = LoadAsync(1);
    }

    public DramaRankKind RankKind
    {
        get => _rankKind;
        set => _rankKind = value;
    }

    // ====================== 加载 ======================

    private async Task LoadAsync(int page)
    {
        if (_source is null)
        {
            StateHost.State = StateKind.Error;
            StateHost.ErrorDescription = "短剧源未注册";
            return;
        }

        var version = ++_loadVersion;
        _page = Math.Max(1, page);
        StateHost.State = StateKind.Loading;
        StateHost.LoadingText = "正在加载短剧…";

        try
        {
            IReadOnlyList<ShortDramaSummary> items;
            int? totalPages;

            if (_mode == DramaBrowseMode.Rank)
            {
                items = await _source.GetRankAsync(_rankKind);
                totalPages = 1;
            }
            else if (_mode == DramaBrowseMode.Search && !string.IsNullOrWhiteSpace(_keyword))
            {
                var listing = await _source.SearchAsync(_keyword, _page);
                items = listing.Items;
                totalPages = listing.TotalPages;
                if (listing.TotalCount > 0) SubtitleText.Text = $"「{_keyword}」共 {listing.TotalCount} 个结果";
            }
            else
            {
                var kind = _mode switch
                {
                    DramaBrowseMode.Newest => DramaFeedKind.Newest,
                    DramaBrowseMode.Channel => DramaFeedKind.Channel,
                    _ => DramaFeedKind.Recommend,
                };
                var listing = await _source.GetFeedAsync(kind, _page, _scope);
                items = listing.Items;
                totalPages = listing.TotalPages;
            }

            if (version != _loadVersion) return; // 已切换到别的来源，丢弃这次结果

            _totalPages = totalPages;
            _items.Clear();
            foreach (var item in items)
                _items.Add(new ShortDramaCardAdapter(item, OpenSummary, PlaySummary, DownloadSummary));

            StateHost.Result = _items;
            StateHost.State = _items.Count > 0 ? StateKind.Result : StateKind.Empty;

            // 封面：先同步吃本地缓存（命中就立刻显示），剩下的后台解密，回来一张填一张
            _ = FillCoversAsync(_items, version);

            if (_mode == DramaBrowseMode.Rank)
                SubtitleText.Text = $"{RankKindName(_rankKind)} · 共 {_items.Count} 部";
            else if (_mode is DramaBrowseMode.Recommend or DramaBrowseMode.Newest or DramaBrowseMode.Channel)
                SubtitleText.Text = _totalPages is > 0 ? $"{ModeSubtitle()} · 第 {_page} / {_totalPages} 页" : ModeSubtitle();

            UpdatePager();
            UpdateLineCard();
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            _logger?.Warn($"[ShortDrama] 列表加载失败（{_mode} p{_page}）：{ex.Message}");
            _items.Clear();
            StateHost.Result = _items;
            StateHost.State = StateKind.Error;
            StateHost.ErrorDescription = ex is ShortDramaUnavailableException unavailable
                ? unavailable.Message
                : $"加载失败：{ex.Message}";
            UpdatePager();
            UpdateLineCard();
        }
    }

    private string ModeSubtitle() => _mode switch
    {
        DramaBrowseMode.Newest => "按更新顺序",
        DramaBrowseMode.Channel => "频道",
        _ => "编辑精选",
    };

    private static string RankKindName(DramaRankKind kind) => kind switch
    {
        DramaRankKind.Recommend => "推荐榜",
        DramaRankKind.Potential => "潜力榜",
        _ => "热播榜",
    };

    private static string ChannelTitle(string? scope)
    {
        foreach (var (slug, name) in ShortDramaNavSectionProvider.Channels)
            if (string.Equals(slug, scope, StringComparison.OrdinalIgnoreCase)) return name;
        return "频道";
    }

    // ====================== 交互 ======================

    private void OpenSummary(ShortDramaSummary summary) => OpenRequested?.Invoke(this, summary);

    private void PlaySummary(ShortDramaSummary summary) => PlayRequested?.Invoke(this, summary);

    /// <summary>卡片悬停「下载全部」：按剧集详情一次性入队所有集，已下载的自动跳过。</summary>
    private void DownloadSummary(ShortDramaSummary summary) => _ = DownloadAllAsync(summary);

    private async Task DownloadAllAsync(ShortDramaSummary summary)
    {
        var source = _source;
        var download = _download;
        if (source is null || download is null)
        {
            ToastService.Show("短剧下载服务未就绪", ToastKind.Error);
            return;
        }
        if (!_downloading.Add(summary.Id)) return;

        try
        {
            ToastService.Show($"正在获取《{summary.Title}》的剧集信息…");
            var detail = await source.GetDetailAsync(summary.Id);
            if (detail is null || detail.Episodes.Count == 0)
            {
                ToastService.Show("没有可下载的剧集", ToastKind.Error);
                return;
            }

            // 默认下载所有集；EnqueueDramaAsync 内部会跳过已存在文件。
            var result = await download.EnqueueDramaAsync(detail);
            var message = $"《{summary.Title}》已加入下载：{result.Queued} 集";
            if (result.Skipped > 0) message += $"，跳过 {result.Skipped} 集";
            if (result.Failed > 0) message += $"，失败 {result.Failed} 集";
            ToastService.Show(message, result.Failed > 0 ? ToastKind.Error : ToastKind.Success);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDrama] 下载入队失败 {summary.Id}: {ex.Message}");
            ToastService.ShowError(ex, "加入下载失败：");
        }
        finally
        {
            _downloading.Remove(summary.Id);
        }
    }

    /// <summary>点输入框的图标/留白区域也能聚焦（不然只有正中间那条 17px 高的文字区能点），
    /// 不吞事件，避免影响点击定位光标。</summary>
    private void SearchField_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!SearchBox.IsKeyboardFocusWithin) SearchBox.Focus();
    }

    private void UpdateSearchPlaceholder()
        => SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _mode = DramaBrowseMode.Search;
        _keyword = SearchBox.Text.Trim();
        _page = 1;
        TitleText.Text = "搜索";
        SubtitleText.Text = _keyword.Length == 0 ? "" : $"「{_keyword}」";
        _ = LoadAsync(1);
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_page <= 1) return;
        _ = LoadAsync(_page - 1);
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_totalPages is int total && _page >= total) return;
        _ = LoadAsync(_page + 1);
    }

    private async void RefreshLine_Click(object sender, RoutedEventArgs e)
    {
        await RefreshLineAsync(silent: false);
        await LoadAsync(1);
    }

    private async Task RefreshLineAsync(bool silent)
    {
        if (_source is null) return;
        try
        {
            RefreshLineButton.IsEnabled = false;
            var hosts = await _source.RefreshLinesAsync();
            if (!silent)
            {
                ToastService.Show(hosts.Count > 0
                    ? $"已发现 {hosts.Count} 条线路"
                    : "未发现新线路，沿用当前线路");
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDrama] 换线失败：{ex.Message}");
            if (!silent) ToastService.ShowError(ex, "换线失败：");
        }
        finally
        {
            RefreshLineButton.IsEnabled = true;
            UpdateLineCard();
        }
    }

    private void UpdateLineCard()
    {
        var host = _source?.LineHost;
        LineHostText.Text = string.IsNullOrWhiteSpace(host) ? "线路未知" : host;
    }

    /// <summary>
    /// 给当前页的卡片补齐封面。
    /// 站点的封面地址拿到的是密文（AES-128-CBC，密钥在页面的 crypto-worker.js 里），
    /// 所以这里走「下载 → 本地解密 → 落盘缓存」，卡片只绑本地文件路径。
    /// </summary>
    private async Task FillCoversAsync(List<ShortDramaCardAdapter> cards, int version)
    {
        var service = App.Services?.GetService<DramaCoverService>();
        if (service is null) return;

        foreach (var card in cards)
        {
            if (version != _loadVersion) return;                 // 页面已切走
            if (card.Summary is not { } summary) continue;

            // 命中缓存就不发请求
            var cached = service.TryGetCached(summary.Id);
            if (cached is not null)
            {
                card.SetCoverUrl(cached);
                continue;
            }

            try
            {
                var path = await service.EnsureAsync(summary.Id, summary.CoverUrl);
                if (version != _loadVersion) return;
                if (path is not null) card.SetCoverUrl(path);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"[ShortDrama] 封面获取失败 {summary.Id}：{ex.Message}");
            }
        }
    }

    private void UpdatePager()
    {
        PagingPanel.Visibility = _mode == DramaBrowseMode.Rank ? Visibility.Collapsed : Visibility.Visible;
        PageText.Text = _totalPages is int total ? $"{_page} / {total}" : $"第 {_page} 页";
        PrevButton.IsEnabled = _page > 1;
        NextButton.IsEnabled = _totalPages is not int max || _page < max;
    }
}
