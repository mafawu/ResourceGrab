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

/// <summary>写真图集列表页的两种模式。</summary>
public enum PhotobookGalleryMode
{
    /// <summary>
    /// 「写真」：全站图集分页列表 + 关键词搜索。
    /// 站点把列表与搜索放在同一个端点（关键词为空即全站列表），故两者共用一个方法。
    /// </summary>
    Home,

    /// <summary>「随机」：随机抽取一批图集，无分页。仅实现随机能力的源可选。</summary>
    Random,
}

/// <summary>
/// 写真图集列表页。两种模式共用这一视图，靠各自独立的路由实例区分
/// —— ShellNavigator 没有参数化路由，<c>CreateView()</c> 每次返回缓存实例。
/// 每次进入由 <see cref="OnShown"/> 重新解析当前源，因为切换源不会重建页面。
/// <para>
/// 与 <see cref="PhotobookBrowseView"/> 的分工：后者是「按维度条目浏览」（左栏条目 + 右栏结果），
/// 本页是「不带筛选的扁平列表 / 随机」，即站点主导航里的「写真」与「随机」两个入口。
/// 卡片与分页的呈现方式两者保持一致。
/// </para>
/// </summary>
public partial class PhotobookGalleryView : UserControl
{
    /// <summary>卡片宽度随容器宽度自适应（写真卡片比漫画缩略图大，见 PhotobookCardLayout）。</summary>
    private double _cardWidth = PhotobookCardLayout.DefaultCardWidth;

    /// <summary>封面高度，随卡片宽度按竖版比例缩放。</summary>
    private double _coverHeight = PhotobookCardLayout.CoverHeight(PhotobookCardLayout.DefaultCardWidth);

    /// <summary>「随机」一次抽取的图集数。</summary>
    private const int RandomPick = 24;

    private readonly PhotobookGalleryMode _mode;

    private ConfigService? _config;
    private DownloadManager? _downloadManager;

    private IComicSource? _source;

    /// <summary>封面请求附加头（防盗链等），随当前源变化。</summary>
    private IReadOnlyDictionary<string, string>? _coverHeaders;

    private string _keyword = "";
    private string _loadedSourceId = "";
    private long _page = 1;
    private long _totalPages = 1;
    private bool _loaded;
    private bool _loading;

    public PhotobookGalleryView() : this(PhotobookGalleryMode.Home) { }

    public PhotobookGalleryView(PhotobookGalleryMode mode)
    {
        _mode = mode;
        InitializeComponent();

        // 卡片容器宽度变化时重排（窗口拉伸、滚动条出现/消失都会改变这层 WrapPanel 的宽度）
        AlbumList.SizeChanged += (_, _) => ApplyCardLayout();

        var isRandom = mode == PhotobookGalleryMode.Random;
        HeaderText.Text = isRandom ? "随机图集" : "写真";
        HeaderIcon.Data = isRandom ? Icons.Shuffle : Icons.Photobook;
        SearchPanel.Visibility = isRandom ? Visibility.Collapsed : Visibility.Visible;
        RerollButton.Visibility = isRandom ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>当前页面的模式（供调试与路由区分）。</summary>
    public PhotobookGalleryMode Mode => _mode;

    /// <summary>导航到本页时调用：按当前源加载图集。</summary>
    public void OnShown() => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        var current = Resolve<SourceManager>()?.Current;
        if (current is null)
        {
            ShowHint("未找到内容源。");
            return;
        }

        _source = current;
        _coverHeaders = current.Info.CoverHeaders;

        if (current.Info.Kind != ResourceKind.Photobook)
        {
            ShowHint("尚未接入写真源。\n\n在顶栏的源下拉里选定一个写真源后，这里会显示图集列表。");
            return;
        }

        // 同一源重复进入「写真」页时保留上次的结果与页码；「随机」页每次重进都重抽
        var sourceId = current.Info.Id;
        if (_mode == PhotobookGalleryMode.Home && _loaded && _loadedSourceId == sourceId)
        {
            return;
        }

        _loadedSourceId = sourceId;
        _loaded = true;

        if (_mode == PhotobookGalleryMode.Random)
        {
            await LoadRandomAsync();
        }
        else
        {
            await LoadPageAsync(1, _keyword);
        }
    }

    // ====================== 加载 ======================

    private async Task LoadPageAsync(long page, string keyword)
    {
        if (_source is not IComicSource source || _loading)
        {
            return;
        }

        _loading = true;
        _page = Math.Max(1, page);
        _keyword = keyword;
        AlbumList.Children.Clear();
        Pager.Visibility = Visibility.Collapsed;
        HeaderText.Text = keyword.Length > 0 ? $"写真 · {keyword}" : "写真";
        ShowHint("正在加载图集…");

        try
        {
            var result = await source.SearchAsync(keyword, (int)_page);
            Render(result);
        }
        catch (Exception ex)
        {
            ShowHint("加载图集失败：" + ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadRandomAsync()
    {
        if (_source is not IRandomPhotobookSource random || _loading)
        {
            ShowHint("当前写真源不支持随机浏览。");
            return;
        }

        _loading = true;
        AlbumList.Children.Clear();
        Pager.Visibility = Visibility.Collapsed;
        ShowHint("正在随机抽取图集…");

        try
        {
            var result = await random.GetRandomGalleriesAsync(RandomPick);
            Render(result);
        }
        catch (Exception ex)
        {
            ShowHint("随机加载失败：" + ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Render(SearchResult result)
    {
        AlbumList.Children.Clear();

        if (result.Items.Count == 0)
        {
            ShowHint(_keyword.Length > 0 ? $"没有匹配「{_keyword}」的图集。" : "没有图集。");
            return;
        }

        foreach (var album in result.Items)
        {
            AlbumList.Children.Add(BuildAlbumCard(album));
        }
        HintText.Visibility = Visibility.Collapsed;
        ApplyCardLayout();

        if (_mode == PhotobookGalleryMode.Random)
        {
            ResultCountText.Text = $"随机 {result.Items.Count} 部";
            PageText.Visibility = Visibility.Collapsed;
            PrevButton.Visibility = Visibility.Collapsed;
            NextButton.Visibility = Visibility.Collapsed;
            Pager.Visibility = Visibility.Visible;
            return;
        }

        _totalPages = Math.Max(1, result.TotalPages);
        ResultCountText.Text = "";
        PageText.Visibility = Visibility.Visible;
        PrevButton.Visibility = Visibility.Visible;
        NextButton.Visibility = Visibility.Visible;
        PageText.Text = result.Total > 0
            ? $"第 {_page} / {_totalPages} 页 · 共 {result.Total} 部"
            : $"第 {_page} / {_totalPages} 页";
        PrevButton.IsEnabled = _page > 1;
        NextButton.IsEnabled = _page < _totalPages;
        Pager.Visibility = Visibility.Visible;
    }

    // ====================== 交互 ======================

    private void SearchButton_Click(object sender, RoutedEventArgs e)
        => _ = LoadPageAsync(1, SearchBox.Text?.Trim() ?? "");

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = LoadPageAsync(1, SearchBox.Text?.Trim() ?? "");
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        _ = LoadPageAsync(1, "");
    }

    private void RerollButton_Click(object sender, RoutedEventArgs e) => _ = LoadRandomAsync();

    private void PrevButton_Click(object sender, RoutedEventArgs e) => _ = LoadPageAsync(_page - 1, _keyword);

    private void NextButton_Click(object sender, RoutedEventArgs e) => _ = LoadPageAsync(_page + 1, _keyword);

    // ====================== 卡片 ======================

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
        // 部分站点 CDN 会直接拒掉 → 封面整片空白（与漫画源保持一致都走 ImageLoader）。
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
        if (sender is not Border { Tag: ComicSummary album } || _source is not { } comicSource)
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
            try { Resolve<ResourceGrab.Core.Logging.ILogger>()?.Error("[PhotobookGallery] 打开图集失败", ex); }
            catch { }
            ToastService.ShowError(ex);
        }
    }

    private async void DownloadAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ComicSummary album } || _source is not { } comicSource)
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
            try { Resolve<ResourceGrab.Core.Logging.ILogger>()?.Error("[PhotobookGallery] 加入下载失败", ex); }
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
}
