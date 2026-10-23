using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Common;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Services;
using ResourceGrab.App.Themes;
using ResourceGrab.Core.Downloading;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources;

namespace ResourceGrab.App.Views;

/// <summary>
/// 写真维度浏览页。六个维度共用这一视图，靠各自的独立路由实例区分
/// —— ShellNavigator 没有参数化路由，<c>CreateView()</c> 每次返回缓存实例。
/// 每次进入由 <see cref="OnShown"/> 重新解析当前源，因为切换源不会重建页面。
/// <para>
/// 左栏是该维度的条目（分类 / 标签 / Coser / …），右栏是所选条目下的图集卡片 + 分页。
/// 条目数差异极大（分类 10 项、标签 507 项、出演 2166 项），故超过阈值时提供关键词过滤，
/// 且渲染条数设上限，避免一次性生成上千个按钮。
/// </para>
/// </summary>
public partial class PhotobookBrowseView : UserControl
{
    /// <summary>超过此条目数才显示过滤框。</summary>
    private const int FilterThreshold = 60;

    /// <summary>左栏最多渲染的条目数（超出部分靠过滤缩小范围）。</summary>
    private const int RenderCap = 400;

    /// <summary>内存/磁盘里的条目在这个时长内直接复用；超过则静默后台刷新一次。</summary>
    private static readonly TimeSpan DimensionRefreshInterval = TimeSpan.FromMinutes(10);

    /// <summary>卡片宽度随容器宽度自适应（写真卡片比漫画缩略图大，见 PhotobookCardLayout）。</summary>
    private double _cardWidth = PhotobookCardLayout.DefaultCardWidth;

    /// <summary>封面高度，随卡片宽度按竖版比例缩放。</summary>
    private double _coverHeight = PhotobookCardLayout.CoverHeight(PhotobookCardLayout.DefaultCardWidth);

    private readonly TaxonomyKind _kind;

    private ConfigService? _config;
    private DownloadManager? _downloadManager;

    private ITaxonomySource? _source;

    /// <summary>封面请求附加头（防盗链等），随当前源变化。</summary>
    private IReadOnlyDictionary<string, string>? _coverHeaders;

    private IReadOnlyList<TaxonomyItem> _allItems = [];
    private string _loadedSourceId = "";
    private bool _loaded;

    /// <summary>条目列表缓存（先用上次结果出内容，再后台刷新覆盖）。</summary>
    private PhotobookTaxonomyCache? _taxonomyCache;

    /// <summary>是否正在拉取条目（用于去重与状态文案）。</summary>
    private bool _refreshingItems;

    /// <summary>当前条目数据的时间（内存新鲜度判定）/ 来自缓存的时间（用于标注）。</summary>
    private DateTimeOffset? _lastLoadedAt;
    private DateTimeOffset? _cacheSavedAt;

    /// <summary>条目计数旁的状态后缀（正在刷新 / 已是最新 / 刷新失败…）。</summary>
    private string _itemStatus = "";
    private int _lastMatched;
    private int _lastRendered;

    private string _selectedItemId = "";
    private long _page = 1;
    private long _totalPages = 1;
    private bool _loadingAlbums;

    public PhotobookBrowseView() : this(TaxonomyKind.Category) { }

    public PhotobookBrowseView(TaxonomyKind kind)
    {
        _kind = kind;
        InitializeComponent();

        // 卡片容器宽度变化时重排（窗口拉伸、左栏条目名换行、滚动条出现/消失都会改变这层 WrapPanel 的宽度）
        AlbumList.SizeChanged += (_, _) => ApplyCardLayout();

        DimensionTitle.Text = TitleFor(kind);
        DimensionIcon.Data = IconFor(kind);
    }

    /// <summary>当前页面对应的维度。</summary>
    public TaxonomyKind Kind => _kind;

    /// <summary>导航到本页时调用：按当前源加载维度条目。</summary>
    public async void OnShown() => await LoadDimensionsAsync();

    // ====================== 左栏：维度条目 ======================

    private async Task LoadDimensionsAsync()
    {
        var sources = Resolve<SourceManager>();
        var current = sources?.Current;
        var sourceId = current?.Info.Id ?? "";

        // 同一源已经加载过：保留上次的选择与结果，不重复拉取（Coser 维度可达数十次请求）。
        // 仅当手里这份数据放置超过新鲜期时，才静默后台刷新一次（不清空现有内容）。
        if (_loaded && _loadedSourceId == sourceId)
        {
            if (_source is { } loaded
                && _lastLoadedAt is { } at
                && DateTimeOffset.Now - at > DimensionRefreshInterval)
            {
                await RefreshItemsAsync(loaded, sourceId, fromCache: true);
            }
            return;
        }

        _loaded = false;
        _loadedSourceId = sourceId;
        _source = null;
        _allItems = [];
        _lastLoadedAt = null;
        _cacheSavedAt = null;
        _itemStatus = "";
        _lastMatched = 0;
        _lastRendered = 0;
        _selectedItemId = "";
        ItemList.Children.Clear();
        AlbumList.Children.Clear();
        FilterBox.Visibility = Visibility.Collapsed;
        Pager.Visibility = Visibility.Collapsed;
        ResultHeader.Text = "请选择左侧条目";
        ItemCountText.Text = "";

        if (current is not ITaxonomySource source || !current.Info.Taxonomies.Contains(_kind))
        {
            ShowHint($"「{TitleFor(_kind)}」暂不可用：当前写真源尚未接入，或未声明该维度。\n\n" +
                     "接入写真源后，这里会显示该维度下的条目，以及所选条目的图集列表。");
            return;
        }

        _source = source;
        _coverHeaders = current.Info.CoverHeaders;

        // ① 先用上次缓存的条目立即出内容：有缓存就不必干等网络（Coser 维度要拉上百个请求）
        var cached = await TryLoadCachedItemsAsync(sourceId);
        if (cached is { Items.Count: > 0 })
        {
            _allItems = cached.Items;
            _cacheSavedAt = cached.SavedAt;
            _loaded = true;
            FilterBox.Visibility = _allItems.Count > FilterThreshold ? Visibility.Visible : Visibility.Collapsed;
            FilterBox.Text = "";
            RenderItems();
            ShowHint("已加载条目，请从左侧选择一项以浏览图集。");
        }
        else
        {
            ShowHint("正在加载条目…");
        }

        // ② 后台刷新：成功且内容有变化才覆盖界面与缓存（stale-while-revalidate）
        await RefreshItemsAsync(source, sourceId, fromCache: cached is not null);
    }

    /// <summary>读维度条目缓存；未接缓存服务、没有缓存或缓存不可用时返回 null。</summary>
    private async Task<PhotobookTaxonomyCache.Snapshot?> TryLoadCachedItemsAsync(string sourceId)
    {
        _taxonomyCache ??= Resolve<PhotobookTaxonomyCache>();
        if (_taxonomyCache is null || sourceId.Length == 0)
        {
            return null;
        }

        try
        {
            return await _taxonomyCache.LoadAsync(sourceId, _kind);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 拉取维度条目并覆盖当前内容（同时写回缓存）。
    /// <para>
    /// <paramref name="fromCache"/> 为 true 表示界面上已经在展示缓存数据：此时不显示「正在加载」，
    /// 刷新失败也保留缓存内容，只在条目计数旁标注状态，避免「刷新失败反而什么都看不见」。
    /// </para>
    /// </summary>
    private async Task RefreshItemsAsync(ITaxonomySource source, string sourceId, bool fromCache)
    {
        if (_refreshingItems)
        {
            return;
        }

        _refreshingItems = true;
        SetItemStatus(fromCache ? " · 正在刷新…" : "");

        // 先把网络结果拿到手、并落下「正在刷新」标志，再更新界面文案，
        // 否则文案里会残留「正在刷新…」
        IReadOnlyList<TaxonomyItem> items = [];
        var failed = "";
        try
        {
            items = await source.GetTaxonomyItemsAsync(_kind);
        }
        catch (Exception ex)
        {
            failed = ex.Message;
        }

        _refreshingItems = false;

        if (failed.Length > 0)
        {
            if (_allItems.Count == 0)
            {
                SetItemStatus("");
                ShowHint("加载条目失败：" + failed);
            }
            else
            {
                // 刷新失败但手里有（缓存的）数据：保留展示，只标注状态
                SetItemStatus(_cacheSavedAt is { } at
                    ? $" · 刷新失败，显示缓存（{at:MM-dd HH:mm}）"
                    : " · 刷新失败");
            }
            return;
        }

        var changed = !SameItems(_allItems, items);
        _allItems = items;
        _loaded = true;
        _lastLoadedAt = DateTimeOffset.Now;
        _cacheSavedAt = _lastLoadedAt;
        FilterBox.Visibility = items.Count > FilterThreshold ? Visibility.Visible : Visibility.Collapsed;

        _itemStatus = "";
        if (changed)
        {
            RenderItems();
        }
        else
        {
            UpdateItemCountText();
        }
        SetItemStatus(changed ? " · 已更新" : " · 已是最新");

        if (_taxonomyCache is not null && sourceId.Length > 0)
        {
            await _taxonomyCache.SaveAsync(sourceId, _kind, items);
        }

        // 首次（无缓存）加载时才需要补右栏提示；已经在看某个条目的图集就不要盖掉
        if (!fromCache && AlbumList.Children.Count == 0)
        {
            ShowHint(items.Count > 0
                ? "已加载条目，请从左侧选择一项以浏览图集。"
                : "该维度下暂无条目。");
        }
    }

    /// <summary>条目列表是否等值（顺序、Id、计数都比），用于决定要不要重建左栏。</summary>
    private static bool SameItems(IReadOnlyList<TaxonomyItem> a, IReadOnlyList<TaxonomyItem> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal) || a[i].Count != b[i].Count)
            {
                return false;
            }
        }

        return true;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => RenderItems();

    private void RenderItems()
    {
        ItemList.Children.Clear();

        var keyword = FilterBox.Text?.Trim() ?? "";
        var matched = keyword.Length > 0
            ? _allItems.Where(i => i.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList()
            : _allItems.ToList();

        var rendered = matched.Take(RenderCap).ToList();
        foreach (var item in rendered)
        {
            ItemList.Children.Add(BuildItemButton(item));
        }

        _lastMatched = matched.Count;
        _lastRendered = rendered.Count;
        UpdateItemCountText();
    }

    /// <summary>更新条目计数旁的状态（正在刷新 / 已是最新 / 刷新失败…）。</summary>
    private void SetItemStatus(string status)
    {
        _itemStatus = status;
        UpdateItemCountText();
    }

    private void UpdateItemCountText()
    {
        string text;
        if (_allItems.Count == 0)
        {
            text = _refreshingItems ? "正在加载条目…" : "该维度下暂无条目。";
        }
        else if (_lastMatched == 0)
        {
            text = "没有匹配的条目。";
        }
        else if (_lastRendered < _lastMatched)
        {
            text = $"显示前 {_lastRendered} 项（共 {_lastMatched} 项），输入关键词缩小范围。";
        }
        else
        {
            text = $"共 {_allItems.Count} 个条目。";
        }

        ItemCountText.Text = _itemStatus.Length == 0 ? text : text + _itemStatus;
    }

    private Button BuildItemButton(TaxonomyItem item)
    {
        var button = new Button
        {
            Content = item.Count is { } count ? $"{item.Name} ({count})" : item.Name,
            Tag = item,
            Style = TryFindResource("GhostButtonStyle") as Style,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(10, 6, 10, 6),
            ToolTip = item.Name,
        };
        button.Click += Item_Click;
        return button;
    }

    private async void Item_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TaxonomyItem item })
        {
            await LoadAlbumsAsync(item.Id, item.Name, 1);
        }
    }

    // ====================== 右栏：图集 ======================

    private async Task LoadAlbumsAsync(string itemId, string itemName, long page)
    {
        if (_source is null || _loadingAlbums)
        {
            return;
        }

        _loadingAlbums = true;
        _selectedItemId = itemId;
        _page = page;
        AlbumList.Children.Clear();
        Pager.Visibility = Visibility.Collapsed;
        ResultHeader.Text = itemName;
        ShowHint("正在加载图集…");

        try
        {
            var result = await _source.BrowseByTaxonomyAsync(_kind, itemId, (int)page);
            if (result.Items.Count == 0)
            {
                ShowHint("该条目下暂无图集。");
                return;
            }

            foreach (var album in result.Items)
            {
                AlbumList.Children.Add(BuildAlbumCard(album));
            }
            HintText.Visibility = Visibility.Collapsed;
            ApplyCardLayout();

            _totalPages = Math.Max(1, result.TotalPages);
            PageText.Text = result.Total > 0
                ? $"第 {Math.Max(1, _page)} / {_totalPages} 页 · 共 {result.Total} 部"
                : $"第 {Math.Max(1, _page)} / {_totalPages} 页";
            PrevButton.IsEnabled = _page > 1;
            NextButton.IsEnabled = _page < _totalPages;
            Pager.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ShowHint("加载图集失败：" + ex.Message);
        }
        finally
        {
            _loadingAlbums = false;
        }
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => _ = ReloadPageAsync(_page - 1);

    private void NextButton_Click(object sender, RoutedEventArgs e) => _ = ReloadPageAsync(_page + 1);

    private Task ReloadPageAsync(long page)
    {
        var name = ResultHeader.Text;
        return _selectedItemId.Length == 0 ? Task.CompletedTask : LoadAlbumsAsync(_selectedItemId, name, page);
    }

    private UIElement BuildAlbumCard(ComicSummary album)
    {
        var image = new Image
        {
            // UniformToFill：封面**永远铺满**卡片宽度（超出部分裁切），与漫画/视频侧的
            // AlbumCard / PosterCard / LocalComicCard 做法一致。
            // 不能用 Uniform —— 写真封面宽高比并不统一（实测 1.33~1.6，还有竖向拼图这类极端比例），
            // 用 Uniform 时偏高的封面会缩成窄条居中，卡片两侧留下大片空白（10-07 实际踩到）。
            Stretch = Stretch.UniformToFill,
        };
        // 复用统一加载器：它用自带 UA 的 HttpClient 抓字节后再解码。
        // 若直接用 BitmapImage(UriSource)，会走 WPF 内置网络栈（UA 为空），
        // 站点 CDN 会拒掉 → 封面整片空白（与漫画源一致都走 ImageLoader）。
        ImageLoader.SetHeaders(image, _coverHeaders);
        ImageLoader.SetSource(image, album.CoverUrl);

        var coverHost = new Grid
        {
            Width = _cardWidth,
            Height = _coverHeight,
            ClipToBounds = true,
            Tag = "PhotobookCoverHost",
        };
        coverHost.Children.Add(image);

        var downloadButton = new Button
        {
            Content = BuildDownloadButtonContent(),
            Tag = album,
            Padding = new Thickness(14, 5, 14, 5),
            Style = TryFindResource("PrimaryButtonStyle") as Style,
            ToolTip = "下载整册",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        downloadButton.Click += DownloadAlbum_Click;

        var hoverOverlay = new Border
        {
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        hoverOverlay.SetResourceReference(Border.BackgroundProperty, "CardScrimBrush");
        coverHost.Children.Add(hoverOverlay);
        coverHost.Children.Add(downloadButton);

        var title = new TextBlock
        {
            Text = album.Title,
            FontSize = 12,
            Margin = new Thickness(8, 6, 8, 0),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var stack = new StackPanel();
        stack.Children.Add(coverHost);
        stack.Children.Add(title);

        // 分类名（来自卡片内的 /type/ 链接）。作者已含在标题前缀里，故不重复显示。
        if (album.Category.Length > 0)
        {
            var category = new TextBlock
            {
                Text = album.Category,
                FontSize = 10.5,
                Margin = new Thickness(8, 2, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            category.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            stack.Children.Add(category);
        }

        var card = new Border
        {
            Width = _cardWidth,
            Margin = new Thickness(0, 0, GridCellSizer.Spacing, GridCellSizer.Spacing),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            // UniformToFill 会让封面在某一方向溢出，必须裁掉，否则会盖住相邻卡片
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            Tag = album,
            ToolTip = album.Title + "（点击浏览）",
            Child = stack,
        };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        card.MouseEnter += (_, _) =>
        {
            hoverOverlay.Visibility = Visibility.Visible;
            downloadButton.Visibility = Visibility.Visible;
        };
        card.MouseLeave += (_, _) =>
        {
            hoverOverlay.Visibility = Visibility.Collapsed;
            downloadButton.Visibility = Visibility.Collapsed;
        };
        card.MouseLeftButtonUp += AlbumCard_Click;
        return card;
    }

    private static StackPanel BuildDownloadButtonContent()
    {
        var icon = new Path
        {
            Data = Icons.Download,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(Shape.StrokeProperty, "PrimaryFgBrush");

        var text = new TextBlock
        {
            Text = "下载",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryFgBrush");

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(text);
        return content;
    }

    private async void AlbumCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ComicSummary album } || _source is not IComicSource comicSource)
        {
            return;
        }

        try
        {
            var detail = await comicSource.GetComicAsync(album.Id);
            // 图集无章节层级，直接进在线阅读器浏览整册图片
            Navigation.OpenOnlineReader(comicSource, detail.Chapters, 0);
        }
        catch (Exception ex)
        {
            try { Resolve<ResourceGrab.Core.Logging.ILogger>()?.Error("[PhotobookBrowse] 打开图集失败", ex); }
            catch { }
            ToastService.ShowError(ex);
        }
    }

    private async void DownloadAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ComicSummary album } || _source is not IComicSource comicSource)
        {
            return;
        }

        _config ??= Resolve<ConfigService>();
        _downloadManager ??= Resolve<DownloadManager>();
        if (_config is null || _downloadManager is null)
        {
            return;
        }

        try
        {
            var (count, title) = await DownloadHelper.EnqueueAllAsync(
                comicSource, _config, _downloadManager, album.Id);
            ToastService.Show($"已将「{title}」的 {count} 个章节加入下载队列", ToastKind.Success);
        }
        catch (Exception ex)
        {
            try { Resolve<ResourceGrab.Core.Logging.ILogger>()?.Error("[PhotobookBrowse] 加入下载失败", ex); }
            catch { }
            ToastService.ShowError(ex);
        }
    }

    // ====================== 工具 ======================

    /// <summary>
    /// 按卡片容器当前宽度重排：列数自适应，每行铺满可用宽度。
    /// <para>
    /// 宽度取自承载卡片的 <c>AlbumList</c>（WrapPanel）自身的 ActualWidth —— 它就是
    /// ScrollViewer 视口的真实宽度，而不是拿外层 ScrollViewer 的宽度去减一个估算的滚动条宽度。
    /// 后者会略大于真实宽度，导致每行差一点点放不下第二张卡、整片网格退化成单列（见
    /// <see cref="PhotobookCardLayout.AvailableWidth"/>）。
    /// </para>
    /// <para>
    /// 只改尺寸、不重新请求封面，所以拖动窗口缩放时不会有加载闪烁。
    /// </para>
    /// </summary>
    private void ApplyCardLayout()
    {
        var available = PhotobookCardLayout.AvailableWidth(AlbumList);
        if (available <= 0)
        {
            return;
        }

        if (AlbumList.Children.Count == 0)
        {
            // 还没有卡片：先记下待用尺寸，让首次渲染直接用对的大小，避免建完再跳变
            var (_, width) = PhotobookCardLayout.Compute(available);
            _cardWidth = width;
            _coverHeight = PhotobookCardLayout.CoverHeight(width);
            return;
        }

        _cardWidth = PhotobookCardLayout.Apply(AlbumList, available);
        _coverHeight = PhotobookCardLayout.CoverHeight(_cardWidth);
    }

    private void ShowHint(string text)
    {
        HintText.Text = text;
        HintText.Visibility = Visibility.Visible;
    }

    private static T? Resolve<T>() where T : class
    {
        try { return App.Services.GetService<T>(); }
        catch { return null; }
    }

    private static string TitleFor(TaxonomyKind kind) => kind switch
    {
        TaxonomyKind.Category => "分类",
        TaxonomyKind.Tag => "标签",
        TaxonomyKind.Coser => "Coser",
        TaxonomyKind.Franchise => "系列",
        TaxonomyKind.Character => "角色",
        TaxonomyKind.Topic => "主题",
        _ => "写真",
    };

    private static Geometry IconFor(TaxonomyKind kind) => kind switch
    {
        TaxonomyKind.Category => Icons.Category,
        TaxonomyKind.Tag => Icons.Tag,
        TaxonomyKind.Coser => Icons.User,
        TaxonomyKind.Franchise => Icons.Library,
        TaxonomyKind.Character => Icons.Character,
        TaxonomyKind.Topic => Icons.Document,
        _ => Icons.Photobook,
    };
}
