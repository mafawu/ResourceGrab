using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ResourceGrab.App.Common;
using ResourceGrab.App.Services;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Errors;
using ResourceGrab.Core.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Views;

/// <summary>
/// 在线阅读器：按章节在线浏览漫画（数据走 IComicSource 图片链路，不落盘）。
/// 交互与本地阅读器一致：滚动/翻页两种模式、适应宽度/高度/页面/实际大小、Ctrl+滚轮缩放、
/// ←/→ 或滚轮翻页、章节切换、阅读进度记忆。
/// 性能设计：仅加载可视区附近图片，离开即释放 BitmapSource；字节缓存由 OnlineReaderService 管理，
/// 回看已读页面直接命中缓存不再请求网络。
/// </summary>
public partial class OnlineReaderView : UserControl
{
    private enum FitMode { FitWidth, FitHeight, FitPage, Actual }

    /// <summary>查看方式。翻页/滚动既有；画墙是写真（图集）专用的第三种。</summary>
    private enum ReaderMode
    {
        /// <summary>单列纵向连续滚动。</summary>
        Scroll,

        /// <summary>一次一页。</summary>
        Page,

        /// <summary>整册图片平铺成缩略图墙，点击任意一张进入单图查看。仅写真源开放。</summary>
        Wall,
    }

    private sealed class PageHost
    {
        public required Grid Root { get; init; }
        public required Image Image { get; init; }
        public required Border StateLayer { get; init; }
        public required TextBlock StateText { get; init; }
        public required Controls.LoadingIndicator StateIndicator { get; init; }
        /// <summary>在 <see cref="_hosts"/> 里的下标。双页模式要靠它判断本页在行内是左还是右。</summary>
        public int Index;
        public int PixelWidth;
        public int PixelHeight;
        /// <summary>当前已解码位图的解码宽度（0 = 未加载）。用于画墙低清 → 单图高清的按需升级。</summary>
        public int DecodeWidth;
        public double HeightEstimate = 800;
        public bool IsLoaded;
        public bool IsLoading;
    }

    /// <summary>
    /// 画墙里的一张缩略贴片。<b>只持视觉元素，不持有加载状态</b> —— 加载状态统一由
    /// <see cref="_hosts"/> 按页索引管理，贴片仅通过 <see cref="SyncWallTile"/> 映射过去。
    /// 这样两种呈现共用同一份 <see cref="_loaded"/> 位图缓存，切换模式不会重新下载。
    /// </summary>
    private sealed class WallTile
    {
        public required Border Root { get; init; }
        public required Image Image { get; init; }
        public required Border StateLayer { get; init; }
        public required TextBlock StateText { get; init; }
        public required Controls.LoadingIndicator StateIndicator { get; init; }
    }

    private readonly OnlineReaderService _service;
    private readonly IComicSource _source;
    private readonly IReadOnlyList<Chapter> _chapters;

    private int _chapterIndex;
    private int _chapterVersion;
    private bool _switchingChapter;
    private IReadOnlyList<ImagePage> _pages = Array.Empty<ImagePage>();
    private readonly List<PageHost> _hosts = new();
    private readonly Dictionary<int, BitmapSource> _loaded = new();
    private readonly HashSet<int> _loading = new();
    private double[] _tops = Array.Empty<double>();

    private FitMode _fitMode = FitMode.FitPage;
    /// <summary>最近加载图片的宽高比（宽/高），未加载页占位按此估算，保证适应模式下布局与单张图一致。</summary>
    private double _aspectRatio = 0.75;
    private ReaderMode _mode = ReaderMode.Page;

    // ---- 画墙（仅写真源可用）----
    private readonly List<WallTile> _wallTiles = new();
    private int _wallColumns = 4;
    private double _wallTileWidth = WallDefaultTileWidth;
    private double _wallTileHeight;
    private bool _wallLaidOut;
    /// <summary>画墙图片宽度缩放，与漫画阅读器的 <see cref="_zoom"/> 分离，避免污染翻页/滚动。</summary>
    private double _wallZoom = 1.0;

    // ---- 双页并排（滚动 / 翻页模式可用）----
    private bool _spread;
    private bool _allowSpread;

    /// <summary>
    /// 每行放几页。双页并排就是 2；画墙是另一种容器（WrapPanel + 等宽贴片），不走这里，
    /// 所以在画墙下强制按单页计算，避免「先开双页再切画墙」时 _tops 按双页行分组、
    /// 切回来又对不上。
    /// </summary>
    private int PagesPerRow => _allowSpread && _spread && _mode != ReaderMode.Wall ? 2 : 1;

    /// <summary>同一行相邻两页之间的横向间距。</summary>
    private const double PageColumnGap = 4;

    /// <summary>相邻两行之间的纵向间距（与页面根元素 Margin 的 2+2 对应）。</summary>
    private const double PageRowGap = 4;

    /// <summary>
    /// 单页可用的横向宽度：双页时把可用宽平分（先扣掉行内间距）。
    /// <b>向下取整</b>且留出 2px 余量，理由和画墙一样 —— WrapPanel 是「超出即换行」的严格布局，
    /// 估算比真实值大哪怕 1px，每行就会少排一张，整片网格退化成单列。
    /// </summary>
    private double PageSlotWidth
    {
        get
        {
            var perRow = PagesPerRow;
            var usable = _viewWidth - PageColumnGap * (perRow - 1) - 2;
            return Math.Max(80, Math.Floor(usable / perRow));
        }
    }

    /// <summary>画墙贴片宽度区间。比图集卡片小：一屏要铺下更多张才叫「墙」。</summary>
    private const double WallMinTileWidth = 120;
    private const double WallMaxTileWidth = 190;
    private const double WallDefaultTileWidth = 150;

    /// <summary>画墙贴片高宽比 = 高 / 宽。写真图片横竖混杂，统一按略竖的框裁切（UniformToFill）。</summary>
    private const double WallTileRatio = 1.35;

    /// <summary>画墙可视区上下各多加载的行高像素，避免快速滚动时一片白。</summary>
    private const double WallOverscan = 260;

    private int _currentPage;
    private int _pendingScrollTo = -1;
    private double _viewWidth = 1200;
    private double _viewHeight = 800;
    private double _zoom = 1.0;
    private const double ZoomMin = 0.5;
    private const double ZoomMax = 3.0;

    /// <summary>翻页模式下左右各这一段宽度算「翻页热区」（点左翻上一页、点右翻下一页）。</summary>
    private const double EdgeZoneRatio = 0.18;

    /// <summary>单列流（滚动/翻页）的解码宽度：图片要铺满视口，得够清晰。</summary>
    private const int FullDecodeWidth = 1600;

    /// <summary>
    /// 画墙贴片的解码宽度。一屏要铺几十张，按 1600 解会把内存顶到几百 MB；
    /// 贴片最大也就 190px 宽，720 已经远超所需。
    /// <para>
    /// 从画墙点进单图时不需要重下字节（<see cref="OnlineReaderService"/> 有字节缓存），
    /// 只是按 <see cref="FullDecodeWidth"/> 重新解码一次 —— 由 <see cref="EnsureImage"/> 里
    /// 「已加载但清晰度不够」的分支自动补上。
    /// </para>
    /// </summary>
    private const int WallDecodeWidth = 720;
    private bool _suppressZoomSlider;
    private bool _suppressFitCombo;
    private bool _suppressChapterCombo;
    private bool _suppressScrollSpeedSave;

    private readonly DispatcherTimer _progressTimer;
    /// <summary>滚动模式防抖：停止滚动 120ms 后才加载可视区，避免惯性滚动触发大量图片请求。</summary>
    private readonly DispatcherTimer _scrollDebounce;
    private Dictionary<string, ProgressEntry> _progress = new();

    public OnlineReaderView(IComicSource source, IReadOnlyList<Chapter> chapters, int startIndex)
    {
        // XAML 初始化滑杆的 Minimum/Value 时会触发 ValueChanged，此时 ImageStack 等命名元素尚未创建。
        _suppressZoomSlider = true;
        InitializeComponent();
        _suppressZoomSlider = false;
        _suppressScrollSpeedSave = true;
        try
        {
            if (App.Services.GetService(typeof(ResourceGrab.Core.Services.ConfigService)) is ResourceGrab.Core.Services.ConfigService ocfg)
            {
                ScrollSpeedSlider.Value = ResourceGrab.Core.Services.ConfigService.NormalizeScrollSpeed(ocfg.Current.ReaderScrollSpeed);
                UpdateScrollSpeedText(ScrollSpeedSlider.Value);
            }
            else
            {
                ScrollSpeedSlider.Value = 1.0;
                UpdateScrollSpeedText(1.0);
            }
        }
        catch { }
        _suppressScrollSpeedSave = false;
        UpdateZoomUI();

        _service = App.Services.GetRequiredService<OnlineReaderService>();
        _source = source;
        _chapters = chapters;
        _chapterIndex = chapters.Count == 0 ? 0 : Math.Clamp(startIndex, 0, chapters.Count - 1);
        _allowSpread = source.Info.Kind != ResourceKind.Photobook;
        if (!_allowSpread)
        {
            _spread = false;
            SpreadToggleButton.Visibility = Visibility.Collapsed;
        }

        // 「画墙」只对写真开放：图集天然是「一册 N 张平铺浏览」，而漫画章节动辄上百页，
        // 铺成墙既无意义也重（每张都要真实下载解码）。按钮藏起来而不是置灰，避免误导。
        if (source.Info.Kind == ResourceKind.Photobook)
        {
            WallModeButton.Visibility = Visibility.Visible;
            // 写真图集没有章节层级，默认整册铺墙比逐页翻看更符合浏览方式。
            _mode = ReaderMode.Wall;
            Scroller.Visibility = Visibility.Collapsed;
            WallScroller.Visibility = Visibility.Visible;
        }

        _suppressChapterCombo = true;
        ChapterCombo.ItemsSource = chapters.Select(c => c.Title).ToList();

        // 适应方式下拉与「滚动速度」可见性都要跟初始状态对齐（默认翻页 → 滚动速度整组隐藏）
        UpdateModeUI();

        // 弹层刚打开时直接 Focus 不可靠（要在 Popup 完成自己那轮布局之后才生效），挂 Opened 才稳
        PageJumpPopup.Opened += (_, _) =>
        {
            PageJumpBox.SelectAll();
            PageJumpBox.Focus();
        };

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _scrollDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _scrollDebounce.Tick += (_, _) =>
        {
            _scrollDebounce.Stop();
            // 滚动停稳：先取消可视区之外的在途加载，把 2 并发Slot让给当前页
            CancelOutOfWindowLoads();
            UpdateVisible();
        };
        _progressTimer.Tick += (_, _) => SaveProgress();
        Unloaded += (_, _) =>
        {
            // 停止定时器并保存进度，避免阅读器实例被 DispatcherTimer 持有而无法回收
            _progressTimer.Stop();
            _scrollDebounce.Stop();
            CancelAllLoads();
            SaveProgress();
        };

        if (chapters.Count == 0)
        {
            TitleText.Text = source.Info.DisplayName;
            ChapterStatePanel.Visibility = Visibility.Visible;
            ChapterLoadingIndicator.Visibility = Visibility.Collapsed;
            ChapterStateText.Text = "该漫画没有可在线阅读的章节";
            return;
        }
        _ = LoadChapterAsync(_chapterIndex);
    }

    // ====================== 章节 ======================

    private string ProgressKey
        => $"online:{_source.Info.Id}:{_chapters[_chapterIndex].ComicId}:{_chapters[_chapterIndex].Id}";

    private async Task LoadChapterAsync(int index)
    {
        if (_switchingChapter)
        {
            return;
        }
        _switchingChapter = true;
        var version = ++_chapterVersion;
        try
        {
            _chapterIndex = Math.Clamp(index, 0, Math.Max(0, _chapters.Count - 1));
            var chapter = _chapters[_chapterIndex];
            TitleText.Text = string.IsNullOrEmpty(chapter.ComicTitle)
                ? chapter.Title
                : $"{chapter.ComicTitle} · {chapter.Title}";
            _suppressChapterCombo = true;
            ChapterCombo.SelectedIndex = _chapterIndex;
            _suppressChapterCombo = false;
            UpdateChapterButtons();

            CancelAllLoads();
            ReleaseAll();
            ChapterStatePanel.Visibility = Visibility.Visible;
            ChapterLoadingIndicator.Visibility = Visibility.Visible;
            ChapterStateText.Visibility = Visibility.Collapsed;
            ChapterRetryButton.Visibility = Visibility.Collapsed;

            try
            {
                _pages = await _service.GetChapterPagesAsync(_source, chapter);
            }
            catch (Exception ex)
            {
                if (version != _chapterVersion)
                {
                    return;
                }
                ChapterLoadingIndicator.Visibility = Visibility.Collapsed;
                ChapterStateText.Visibility = Visibility.Visible;
                ChapterStateText.Text = JmErrorClassifier.Message(ex);
                ChapterRetryButton.Visibility = Visibility.Visible;
                UpdatePagingButtons();
                return;
            }
            if (version != _chapterVersion)
            {
                return;
            }
            ChapterStatePanel.Visibility = Visibility.Collapsed;

            if (_pages.Count == 0)
            {
                ChapterLoadingIndicator.Visibility = Visibility.Collapsed;
                ChapterStateText.Visibility = Visibility.Visible;
                ChapterStateText.Text = "本章暂无可用图片";
                ChapterRetryButton.Visibility = Visibility.Collapsed;
                ChapterStatePanel.Visibility = Visibility.Visible;
                UpdatePagingButtons();
                return;
            }

            BuildPlaceholders();

            _progress = ReadingProgress.Load();
            var startPage = 0;
            if (_progress.TryGetValue(ProgressKey, out var entry) && entry.Image > 0 && entry.Image < _hosts.Count)
            {
                startPage = entry.Image;
            }
            if (_mode == ReaderMode.Page)
            {
                _currentPage = startPage;
                Scroller.ScrollToTop();
                ScrollToPage(startPage);
            }
            else if (_mode == ReaderMode.Wall)
            {
                _currentPage = startPage;
                EnsureWallBuilt();
                ApplyWallLayout();
                WallScroller.ScrollToTop();
                WallScroller.ScrollToVerticalOffset(WallTileTop(startPage));
            }
            else
            {
                _pendingScrollTo = startPage;
                Scroller.ScrollToTop();
            }
            UpdateVisible();
        }
        finally
        {
            _switchingChapter = false;
        }
    }

    private void UpdateChapterButtons()
    {
        PrevChapterButton.IsEnabled = _chapterIndex > 0;
        NextChapterButton.IsEnabled = _chapterIndex < _chapters.Count - 1;
        // 单章不看位置（写真就是「全一册」），多章才需要「第 n / N 章」的方位感
        ChapterIndexText.Text = _chapters.Count > 1
            ? $"第 {_chapterIndex + 1} / {_chapters.Count} 章"
            : "";
    }

    private void PrevChapter_Click(object sender, RoutedEventArgs e) => _ = LoadChapterAsync(_chapterIndex - 1);

    private void NextChapter_Click(object sender, RoutedEventArgs e) => _ = LoadChapterAsync(_chapterIndex + 1);

    private void ChapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChapterCombo || ChapterCombo.SelectedIndex < 0)
        {
            return;
        }
        _ = LoadChapterAsync(ChapterCombo.SelectedIndex);
    }

    private void ChapterRetryButton_Click(object sender, RoutedEventArgs e) => _ = LoadChapterAsync(_chapterIndex);

    private void BackButton_Click(object sender, RoutedEventArgs e) => Navigation.Back();

    // ====================== 页面构建 ======================

    /// <summary>加载/失败遮罩的标记：点击翻页的热区判断靠它排除「点在重试遮罩上」的情况。</summary>
    private const string StateLayerTag = "reader-state-layer";

    /// <summary>
    /// 顶部 3px 横扫的「不确定进度」条。
    /// <para>
    /// 不能用内置 <c>ProgressBar IsIndeterminate</c>：主题里的 <c>ModernProgressBarStyle</c> 换掉了模板，
    /// 没有 <c>PART_GlowRect</c> 这个动画目标，<c>IsIndeterminate</c> 不会动（只会画一条静止的轨道）。
    /// </para>
    /// <para>
    /// 横扫的终点依赖轨道实际宽度，只能在 <c>SizeChanged</c> 里建动画；离屏时停掉，
    /// 否则滚动模式下几百个占位块会各留一个空转的时钟。
    /// </para>
    /// </summary>
    private FrameworkElement BuildLoadBar()
    {
        const double BarWidth = 96;
        var trackBrush = (Brush)FindResource("DividerBrush");
        var indicatorBrush = (Brush)FindResource("PrimaryBrush");

        var transform = new TranslateTransform(-BarWidth, 0);
        var indicator = new Border
        {
            Width = BarWidth,
            Background = indicatorBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = transform,
        };
        var track = new Grid
        {
            Height = 3,
            Background = trackBrush,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ClipToBounds = true,
        };
        track.Children.Add(indicator);

        void StartSweep()
        {
            var width = track.ActualWidth;
            if (width <= 0)
            {
                return;
            }
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
            {
                From = -BarWidth,
                To = width,
                Duration = TimeSpan.FromSeconds(1.1),
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }

        track.SizeChanged += (_, _) => StartSweep();
        track.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                transform.BeginAnimation(TranslateTransform.XProperty, null);
            }
            else
            {
                StartSweep();
            }
        };
        return track;
    }

    private void BuildPlaceholders()
    {
        var background = (Brush)FindResource("ReaderBackdropBrush");
        // 加载态底色不能用 SurfaceBrush（纯白）：它与真实白页一模一样，
        // 于是「正在加载」在屏幕上就是一张白纸加个红圈，看起来像页面渲染空了。
        var stateBackground = (Brush)FindResource("AppBackgroundBrush");
        var stateTextBrush = (Brush)FindResource("TextSecondaryBrush");

        for (var i = 0; i < _pages.Count; i++)
        {
            var image = new Image
            {
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var stateText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                FontSize = 12,
                Foreground = stateTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            var stateIndicator = new Controls.LoadingIndicator
            {
                RingSize = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            var stateStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { stateIndicator, stateText },
            };
            var stateContent = new Grid();
            stateContent.Children.Add(BuildLoadBar());
            stateContent.Children.Add(stateStack);
            var stateLayer = new Border
            {
                Background = stateBackground,
                Child = stateContent,
                Visibility = Visibility.Collapsed,
                CornerRadius = new CornerRadius(4),
                Tag = StateLayerTag,
            };
            var root = new Grid
            {
                Background = background,
                Margin = new Thickness(0, 2, 0, 2),
            };
            root.Children.Add(image);
            root.Children.Add(stateLayer);

            var host = new PageHost { Root = root, Image = image, StateLayer = stateLayer, StateText = stateText, StateIndicator = stateIndicator, Index = i };
            stateLayer.MouseLeftButtonDown += (_, _) => RetryPage(_hosts.IndexOf(host));
            ImageStack.Children.Add(root);
            _hosts.Add(host);
            UpdatePlaceholderSize(i);
        }
        SyncImageStackWidth();
        RebuildTops();
    }

    private void RetryPage(int index)
    {
        if (index < 0 || index >= _hosts.Count)
        {
            return;
        }
        var host = _hosts[index];
        if (host.IsLoading || host.IsLoaded)
        {
            return;
        }
        host.StateLayer.Visibility = Visibility.Collapsed;
        EnsureImage(index);
    }

    // ====================== 懒加载 ======================

    private void UpdateVisible()
    {
        if (_hosts.Count == 0)
        {
            return;
        }
        if (_mode == ReaderMode.Page)
        {
            // 双页模式下「当前页」是一整跨的起点，窗口按行取：本行 + 上下各一行
            var keep = Math.Max(1, PagesPerRow);
            var page = RowStart(Math.Clamp(_currentPage, 0, _hosts.Count - 1));
            var pageFirst = Math.Max(0, page - keep);
            var pageLast = Math.Min(_hosts.Count - 1, page + keep * 2 - 1);
            for (var i = pageFirst; i <= pageLast; i++)
            {
                EnsureImage(i);
            }
            for (var i = 0; i < _hosts.Count; i++)
            {
                if (i < pageFirst || i > pageLast)
                {
                    ReleaseImage(i);
                }
            }
            UpdatePageText();
            return;
        }

        if (_mode == ReaderMode.Wall)
        {
            // 画墙贴片等宽等高，行高固定，所以可视区可以按「索引 / 列数」直接算出来，
            // 不需要像单列流那样先量每张图的真实高度（_tops）再二分定位。
            var (wallFirst, wallLast) = VisibleWallRange();
            for (var i = wallFirst; i <= wallLast; i++)
            {
                EnsureImage(i);
            }
            var keep = Math.Max(1, _wallColumns);
            for (var i = 0; i < _hosts.Count; i++)
            {
                if (i < wallFirst - keep * 2 || i > wallLast + keep * 2)
                {
                    ReleaseImage(i);
                }
            }
            if (wallLast >= wallFirst)
            {
                // 让阅读进度落在画墙的视觉中心，切回翻页/滚动时不至于跳回开头
                _currentPage = Math.Clamp((wallFirst + wallLast) / 2, 0, _hosts.Count - 1);
            }
            UpdatePageText();
            return;
        }

        var first = FindIndexAt(Math.Max(0, Scroller.VerticalOffset - 400));
        var last = FindIndexAt(Math.Min(
            Scroller.VerticalOffset + Scroller.ViewportHeight + 400,
            _tops.Length > 0 ? _tops[^1] : 0));
        for (var i = first; i <= last; i++)
        {
            EnsureImage(i);
        }
        for (var i = 0; i < _hosts.Count; i++)
        {
            if (i < first - 4 || i > last + 4)
            {
                ReleaseImage(i);
            }
        }
        UpdatePageText();
    }

    /// <summary>每页独立取消源：滚出可视区（或切章节）时取消，在 2 并发Slot里给当前页让路。</summary>
    private readonly Dictionary<int, CancellationTokenSource> _loadCts = new();

    /// <summary>当前查看方式所需的解码宽度。</summary>
    private int CurrentDecodeWidth => _mode == ReaderMode.Wall ? WallDecodeWidth : FullDecodeWidth;

    private void EnsureImage(int index)
    {
        if (index < 0 || index >= _hosts.Count)
        {
            return;
        }
        var host = _hosts[index];
        var decodeWidth = CurrentDecodeWidth;
        if (host.IsLoaded && host.DecodeWidth >= decodeWidth)
        {
            return;
        }
        // 画墙已是低清图，切到单图时只在后台补解码。旧图继续显示，
        // 不再盖加载层，避免看起来像重新下载了一遍。
        var qualityUpgrade = host.IsLoaded && host.DecodeWidth > 0;
        var previousDecodeWidth = host.DecodeWidth;
        if (host.IsLoading)
        {
            // 已在按较低清晰度加载（画墙 720 → 单图 1600）：让它跑完，
            // 落图后 IsLoaded 置起，下一轮 UpdateVisible 会以更高清晰度重新解码。
            // 字节本身有缓存，补这一刀只花解码时间，不重新走网络。
            return;
        }
        host.IsLoading = true;
        host.DecodeWidth = decodeWidth;
        if (!qualityUpgrade)
        {
            host.StateLayer.Visibility = Visibility.Visible;
            host.StateIndicator.Visibility = Visibility.Visible;
            host.StateText.Visibility = Visibility.Collapsed;
        }
        SyncWallTile(index);
        _loading.Add(index);
        var page = _pages[index];
        var version = _chapterVersion;
        var cts = new CancellationTokenSource();
        if (_loadCts.TryGetValue(index, out var old))
        {
            try { old.Cancel(); old.Dispose(); } catch { }
        }
        _loadCts[index] = cts;
        var ct = cts.Token;

        _ = Task.Run(async () =>
        {
            var bytes = await _service.GetImageBytesAsync(_source, page, ct);
            ct.ThrowIfCancellationRequested();
            return DecodeBytes(bytes, decodeWidth);
        }, ct).ContinueWith(t =>
        {
            _loading.Remove(index);
            if (_loadCts.TryGetValue(index, out var cur) && ReferenceEquals(cur, cts))
            {
                _loadCts.Remove(index);
                try { cts.Dispose(); } catch { }
            }
            if (version != _chapterVersion)
            {
                return;
            }
            if (t.IsCompletedSuccessfully)
            {
                AttachImage(index, t.Result);
            }
            else if (IsCancellation(t.Exception))
            {
                // 被可视区调度取消：静默复位（不报红），滚回来/停稳后自动重下
                ResetLoadingHost(index);
            }
            else if (qualityUpgrade)
            {
                // 高清补解码失败时继续用画墙已有的低清图，不把已加载页面变成错误态。
                host.IsLoading = false;
                host.DecodeWidth = previousDecodeWidth;
                SyncWallTile(index);
            }
            else
            {
                MarkError(index, t.Exception?.GetBaseException().Message ?? "加载失败，点击重试");
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static bool IsCancellation(Exception? ex)
        => ex is OperationCanceledException
            || (ex as AggregateException)?.InnerExceptions.All(e => e is OperationCanceledException) == true;

    /// <summary>把一页恢复到"未开始"态（下一轮 UpdateVisible 会重新触发下载）。</summary>
    private void ResetLoadingHost(int index)
    {
        if (index < 0 || index >= _hosts.Count) return;
        var host = _hosts[index];
        host.IsLoading = false;
        if (host.IsLoaded)
        {
            SyncWallTile(index);
            return;
        }
        host.StateLayer.Visibility = Visibility.Collapsed;
        SyncWallTile(index);
    }

    /// <summary>取消可视窗口之外的在途加载（滚动停稳时调用）。翻页模式保留当前页±4，滚动模式保留可视区±8。</summary>
    private void CancelOutOfWindowLoads()
    {
        if (_loadCts.Count == 0) return;
        int keepFrom, keepTo;
        if (_mode == ReaderMode.Page)
        {
            var keep = Math.Max(1, PagesPerRow);
            var page = RowStart(Math.Clamp(_currentPage, 0, _hosts.Count - 1));
            keepFrom = page - keep * 2;
            keepTo = page + keep * 3;
        }
        else if (_mode == ReaderMode.Wall)
        {
            if (_hosts.Count == 0) return;
            var (first, last) = VisibleWallRange();
            var keep = Math.Max(1, _wallColumns);
            keepFrom = first - keep;
            keepTo = last + keep;
        }
        else
        {
            if (_hosts.Count == 0) return;
            keepFrom = FindIndexAt(Math.Max(0, Scroller.VerticalOffset - 400)) - 8;
            keepTo = FindIndexAt(Math.Min(
                Scroller.VerticalOffset + Scroller.ViewportHeight + 400,
                _tops.Length > 0 ? _tops[^1] : 0)) + 8;
        }
        foreach (var index in _loadCts.Keys.ToList())
        {
            if (index < keepFrom || index > keepTo) CancelLoad(index);
        }
    }

    private void CancelLoad(int index)
    {
        if (_loadCts.TryGetValue(index, out var cts))
        {
            _loadCts.Remove(index);
            try { cts.Cancel(); cts.Dispose(); } catch { }
        }
        _loading.Remove(index);
        ResetLoadingHost(index);
    }

    private void CancelAllLoads()
    {
        foreach (var index in _loadCts.Keys.ToList()) CancelLoad(index);
    }

    private void AttachImage(int index, BitmapSource bitmap)
    {
        if (index >= _hosts.Count)
        {
            return;
        }
        var host = _hosts[index];
        _loaded[index] = bitmap;
        host.IsLoaded = true;
        host.IsLoading = false;
        host.PixelWidth = bitmap.PixelWidth;
        host.PixelHeight = bitmap.PixelHeight;
        _aspectRatio = (double)bitmap.PixelWidth / Math.Max(1, bitmap.PixelHeight);
        ApplyFitSize(host);
        host.Image.Source = bitmap;
        host.StateLayer.Visibility = Visibility.Collapsed;
        SyncWallTile(index);
        RebuildTops();

        if (_mode == ReaderMode.Page && index == _currentPage)
        {
            ScrollToPage(_currentPage);
        }
        else if (_pendingScrollTo >= 0 && index >= _pendingScrollTo)
        {
            var target = _pendingScrollTo;
            _pendingScrollTo = -1;
            Scroller.ScrollToVerticalOffset(Math.Max(0, _tops[target] - 8));
        }
        UpdateVisible();
    }

    private void MarkError(int index, string message)
    {
        var host = _hosts[index];
        host.IsLoading = false;
        host.DecodeWidth = 0;
        host.StateLayer.Visibility = Visibility.Visible;
        host.StateIndicator.Visibility = Visibility.Collapsed;
        host.StateText.Visibility = Visibility.Visible;
        host.StateText.Text = message;
        SyncWallTile(index);
        RebuildTops();
        UpdateVisible();
    }

    private void ReleaseImage(int index)
    {
        var host = _hosts[index];
        if (host.IsLoading)
        {
            return;
        }
        if (_loaded.Remove(index, out var bitmap))
        {
            host.Image.Source = null;
            host.IsLoaded = false;
            host.DecodeWidth = 0;
            host.StateLayer.Visibility = Visibility.Collapsed;
            SyncWallTile(index);
        }
    }

    private void ReleaseAll()
    {
        ImageStack.Children.Clear();
        WallPanel.Children.Clear();
        _wallTiles.Clear();
        _hosts.Clear();
        _loaded.Clear();
        _loading.Clear();
        _tops = Array.Empty<double>();
        _pages = Array.Empty<ImagePage>();
    }

    // ====================== 布局与缩放 ======================

    /// <summary>
    /// 重建每页的纵向位置。
    /// <para>
    /// <b>双页模式下同一行的两页共用一个 top</b>，行高取行内最高的一页（两页通常等高，
    /// 但「适应高度」下宽高比不同就会不等，必须取 max 否则会重叠）。
    /// 这样 <see cref="_tops"/> 仍然单调不减，<see cref="FindIndexAt"/> 的二分查找不用改。
    /// </para>
    /// </summary>
    private void RebuildTops()
    {
        var perRow = PagesPerRow;
        _tops = new double[_hosts.Count + 1];
        var offset = 0.0;
        for (var i = 0; i < _hosts.Count; i += perRow)
        {
            var rowTop = offset;
            var rowHeight = 0.0;
            for (var k = i; k < Math.Min(i + perRow, _hosts.Count); k++)
            {
                _tops[k] = rowTop;
                rowHeight = Math.Max(rowHeight, _hosts[k].HeightEstimate);
            }
            offset = rowTop + rowHeight + PageRowGap;
        }
        _tops[_hosts.Count] = Math.Max(0, offset);
    }

    private int FindIndexAt(double offset)
    {
        if (_tops.Length == 0)
        {
            return 0;
        }
        var lo = 0;
        var hi = _tops.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_tops[mid] <= offset)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return lo;
    }

    // ====================== 画墙 ======================

    /// <summary>画墙贴片尚未构建（或已因换章节清空）时，按当前页数补齐。</summary>
    private void EnsureWallBuilt()
    {
        if (_pages.Count == 0 || _wallTiles.Count == _pages.Count)
        {
            return;
        }
        BuildWall();
    }

    /// <summary>
    /// 一次性建出全部贴片。<b>只建视觉元素，不触发任何下载</b> —— 该不该加载由
    /// <see cref="UpdateVisible"/> 按可视区决定，与单列流同一套调度。
    /// </summary>
    private void BuildWall()
    {
        WallPanel.Children.Clear();
        _wallTiles.Clear();

        var stateBackground = (Brush)FindResource("SurfaceBrush");
        var stateTextBrush = (Brush)FindResource("TextSecondaryBrush");

        for (var i = 0; i < _pages.Count; i++)
        {
            var image = new Image
            {
                // 与图集卡片一致：铺满贴片、超出裁切。
                // 写真的图片横竖混杂，用 Uniform 会在贴片两侧/上下留出大片空白，整面墙看下来很碎。
                Stretch = Stretch.UniformToFill,
            };

            var stateText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 120,
                FontSize = 11,
                Foreground = stateTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            var stateIndicator = new Controls.LoadingIndicator
            {
                RingSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            var stateContent = new Grid();
            stateContent.Children.Add(BuildLoadBar());
            stateContent.Children.Add(new StackPanel { Children = { stateIndicator, stateText } });
            var stateLayer = new Border
            {
                Background = stateBackground,
                Visibility = Visibility.Collapsed,
                Child = stateContent,
                Tag = StateLayerTag,
            };

            // 页码角标：画墙靠位置认图容易迷路，切回单图时也要知道这是第几张。
            // 压在图片上，所以走项目里既有的「媒体上叠加层」配色（半透明黑底 + 白字）。
            var badgeHost = new Border
            {
                Background = (Brush)FindResource("CardBadgeScrimBrush"),
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4),
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("OnMediaBrush"),
                    Margin = new Thickness(5, 1, 5, 1),
                },
            };

            var root = new Grid { ClipToBounds = true };
            root.Children.Add(image);
            root.Children.Add(stateLayer);
            root.Children.Add(badgeHost);

            var tile = new Border
            {
                Child = root,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Cursor = Cursors.Hand,
                Tag = i,
                ToolTip = $"第 {i + 1} 张（点击查看大图）",
            };
            tile.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            tile.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            tile.MouseLeftButtonUp += WallTile_Click;

            _wallTiles.Add(new WallTile
            {
                Root = tile,
                Image = image,
                StateLayer = stateLayer,
                StateText = stateText,
                StateIndicator = stateIndicator,
            });
            WallPanel.Children.Add(tile);
        }

        _wallLaidOut = false;
        // 位图缓存与单列流共用：已经加载过的图直接补进贴片，正在加载/失败的也把状态搬过来
        for (var i = 0; i < _wallTiles.Count; i++)
        {
            SyncWallTile(i);
        }
    }

    /// <summary>
    /// 按画墙容器当前宽度重排贴片。只改尺寸，不重新下载图片。
    /// <para>
    /// 宽度取自承载贴片的 <see cref="WallPanel"/> 自身的 ActualWidth —— 它被 ScrollViewer
    /// 的视口左右拉伸（该 ScrollViewer 横向是 Disabled，内容被约束在视口内，WrapPanel 才会换行），
    /// 所以这就是真实可用宽度。原因见 <see cref="PhotobookCardLayout.AvailableWidth"/>。
    /// </para>
    /// </summary>
    private void ApplyWallLayout()
    {
        if (_wallTiles.Count == 0)
        {
            return;
        }
        var available = PhotobookCardLayout.AvailableWidth(WallPanel);
        if (available <= 0)
        {
            return;
        }

        var (_, baseTileWidth) = PhotobookCardLayout.Compute(
            available, WallMinTileWidth, WallMaxTileWidth, WallDefaultTileWidth);
        var spacing = Controls.GridCellSizer.Spacing;
        // AvailableWidth 预留了 1px 的换行安全量；这里加回来，让整行严格等于视口宽度。
        var rowWidth = available + PhotobookCardLayout.FitSafetyMargin;
        // 缩放值先决定目标宽度和列数，再用整行可用宽度反算真实贴片宽度。
        // 这样缩放只影响每行图片数量，行尾永远不会留下空白。
        var targetTileWidth = Math.Clamp(baseTileWidth * _wallZoom, Math.Min(80, rowWidth), rowWidth);
        var columns = Math.Max(1, (int)Math.Floor((rowWidth + spacing) / (targetTileWidth + spacing)));
        var tileWidth = Math.Max(1, (rowWidth - spacing * (columns - 1)) / columns);
        var tileHeight = Math.Floor(tileWidth * WallTileRatio);

        // 尺寸没变就不要再碰布局：贴片高度又反过来触发 WallPanel 的 SizeChanged，
        // 不设这道闸门会变成「重排 → 高度变 → 再重排」的自激循环。
        if (_wallLaidOut && columns == _wallColumns
            && Math.Abs(tileWidth - _wallTileWidth) < 0.01
            && Math.Abs(tileHeight - _wallTileHeight) < 0.01)
        {
            return;
        }

        _wallColumns = Math.Max(1, columns);
        _wallTileWidth = tileWidth;
        _wallTileHeight = tileHeight;
        _wallLaidOut = true;

        for (var i = 0; i < _wallTiles.Count; i++)
        {
            var tile = _wallTiles[i].Root;
            // 末列去掉右间距：WrapPanel 是「超出即换行」的严格布局，
            // 整行哪怕只多出一个间距也会把最后一张挤到下一行（与卡片网格同一个坑）。
            var isRowEnd = (i + 1) % _wallColumns == 0;
            tile.Width = _wallTileWidth;
            tile.Height = _wallTileHeight;
            tile.Margin = new Thickness(0, 0, isRowEnd ? 0 : spacing, spacing);
        }
    }

    /// <summary>画墙当前可视区的页索引区间（上下各带一段预加载余量）。</summary>
    private (int First, int Last) VisibleWallRange()
    {
        if (_hosts.Count == 0)
        {
            return (0, -1);
        }

        // 尚未完成首次布局：保守地只取最前两行，等 WallPanel_SizeChanged 再精确计算
        if (!_wallLaidOut || _wallTileHeight <= 0)
        {
            var fallback = Math.Min(_hosts.Count - 1, Math.Max(1, _wallColumns) * 2 - 1);
            return (0, fallback);
        }

        var rowHeight = _wallTileHeight + Controls.GridCellSizer.Spacing;
        var top = Math.Max(0, WallScroller.VerticalOffset - WallOverscan);
        var bottom = WallScroller.VerticalOffset
                     + Math.Max(1, WallScroller.ViewportHeight) + WallOverscan;
        var firstRow = (int)(top / rowHeight);
        var lastRow = (int)(bottom / rowHeight);
        var first = Math.Clamp(firstRow * _wallColumns, 0, _hosts.Count - 1);
        var last = Math.Clamp((lastRow + 1) * _wallColumns - 1, 0, _hosts.Count - 1);
        return (first, Math.Max(first, last));
    }

    /// <summary>某一页在画墙里的纵向偏移，用于从单列流切进画墙时的落点。</summary>
    private double WallTileTop(int index)
    {
        if (_hosts.Count == 0 || _wallTileHeight <= 0)
        {
            return 0;
        }
        var clamped = Math.Clamp(index, 0, _hosts.Count - 1);
        var rowHeight = _wallTileHeight + Controls.GridCellSizer.Spacing;
        return Math.Max(0, clamped / Math.Max(1, _wallColumns) * rowHeight);
    }

    /// <summary>
    /// 把某一页的加载状态映射到画墙贴片。贴片不持有加载状态，唯一的真相在
    /// <see cref="_hosts"/> / <see cref="_loaded"/>，所以每次状态变化都往贴片同步一次。
    /// </summary>
    private void SyncWallTile(int index)
    {
        if (index < 0 || index >= _wallTiles.Count || index >= _hosts.Count)
        {
            return;
        }
        var host = _hosts[index];
        var tile = _wallTiles[index];
        tile.Image.Source = host.Image.Source;
        tile.StateLayer.Visibility = host.StateLayer.Visibility;
        tile.StateIndicator.Visibility = host.StateIndicator.Visibility;
        tile.StateText.Text = host.StateText.Text;
        tile.StateText.Visibility = host.StateText.Visibility;
    }

    private void WallTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int index })
        {
            return;
        }
        _currentPage = Math.Clamp(index, 0, Math.Max(0, _hosts.Count - 1));
        // 点一张就进单图查看：切到翻页模式并定位到该页，不再单独做一层大图层
        SwitchMode(ReaderMode.Page);
    }

    private void WallScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_mode != ReaderMode.Wall)
        {
            return;
        }
        // 与滚动模式同样防抖：贴片虽小，但每张都是真实下载 + 解码
        _scrollDebounce.Stop();
        _scrollDebounce.Start();
    }

    private void WallPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_mode != ReaderMode.Wall)
        {
            return;
        }
        ApplyWallLayout();
        UpdateVisible();
    }

    private void UpdatePlaceholderSize(int index)
    {
        var host = _hosts[index];
        var aspect = _aspectRatio;
        var limit = PageSlotWidth;
        double w;
        double h;
        switch (_fitMode)
        {
            case FitMode.FitHeight:
                // 一张图：高=视口高，宽按该图宽高比
                h = Math.Max(1, _viewHeight * _zoom);
                w = Math.Max(1, h * aspect);
                break;
            case FitMode.FitPage:
                // 一张图：按比例缩放铺满视口
                var scale = Math.Min((limit * _zoom) / aspect, _viewHeight * _zoom);
                w = Math.Max(1, aspect * scale);
                h = Math.Max(1, scale);
                break;
            case FitMode.Actual:
                w = Math.Max(1, limit);
                h = Math.Max(1, w / aspect);
                break;
            default:
                // FitWidth：一张图宽=视口宽，高按该图宽高比
                w = Math.Max(1, limit * _zoom);
                h = Math.Max(1, w / aspect);
                break;
        }
        ApplyRootSize(host, w, h);
    }

    private void ApplyFitSize(PageHost host)
    {
        var pw = Math.Max(1, host.PixelWidth);
        var ph = Math.Max(1, host.PixelHeight);
        var limit = PageSlotWidth;
        double w;
        double h;
        switch (_fitMode)
        {
            case FitMode.FitHeight:
                h = Math.Max(1, _viewHeight * _zoom);
                w = Math.Max(1, h * pw / ph);
                break;
            case FitMode.FitPage:
                var scale = Math.Min((limit * _zoom) / pw, (_viewHeight * _zoom) / ph);
                w = Math.Max(1, pw * scale);
                h = Math.Max(1, ph * scale);
                break;
            case FitMode.Actual:
                w = pw;
                h = ph;
                break;
            default:
                w = Math.Max(1, limit * _zoom);
                h = Math.Max(1, w * ph / pw);
                break;
        }
        host.Image.Width = w;
        host.Image.Height = h;
        ApplyRootSize(host, w, h);
    }

    /// <summary>
    /// 写入页面根元素的尺寸与行内边距，并同步高度估值。
    /// <para>
    /// 双页模式下同一行两页之间要靠边距拉开间距：只在<b>左页</b>加右侧间距。
    /// 末列（右页）不能加，否则整行会比可用宽度多出一个间距，WrapPanel 会把它挤到下一行。
    /// </para>
    /// </summary>
    private void ApplyRootSize(PageHost host, double w, double h)
    {
        host.Root.Width = w;
        host.Root.Height = h;
        host.HeightEstimate = h;

        var perRow = PagesPerRow;
        var isRowEnd = perRow == 1 || (host.Index + 1) % perRow == 0;
        host.Root.Margin = new Thickness(0, 2, isRowEnd ? 0 : PageColumnGap, 2);
    }

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_mode == ReaderMode.Page)
        {
            // 翻页模式：滚动即翻页，立即加载
            UpdateVisible();
        }
        else
        {
            // 滚动模式：防抖，停止滚动后再加载可视区
            _scrollDebounce.Stop();
            _scrollDebounce.Start();
        }
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 用事件参数里的新尺寸而非 Scroller.ViewportWidth/Height：
        // 布局首拍 SizeChanged 触发时视口尚未就绪（可能为 0/旧值），
        // 之后尺寸不再变化就不会再触发事件，导致页宽高永远用错值。
        // 主题里的滚动条是自定义样式、宽 7px（不是系统默认的 17px），故只留 8px 余量；
        // 少给会让页面白白窄一截，多给 1px 就会让 WrapPanel 每行少排一张。
        _viewWidth = Math.Max(320, e.NewSize.Width - 8);
        _viewHeight = Math.Max(320, e.NewSize.Height - 8);
        ApplyZoomToAll();
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomBy(e.Delta > 0 ? 1.2 : 1 / 1.2);
            e.Handled = true;
            return;
        }
        if (_mode == ReaderMode.Page)
        {
            if (e.Delta > 0)
            {
                StepPage(-1);
            }
            else
            {
                StepPage(1);
            }
            e.Handled = true;
            return;
        }
        var speed = ScrollSpeedSlider != null ? ScrollSpeedSlider.Value : 1.0;
        if (speed <= 0) speed = 1.0;
        var delta = e.Delta * speed * 0.6;
        var newOffset = Scroller.VerticalOffset - delta;
        newOffset = Math.Clamp(newOffset, 0, Scroller.ScrollableHeight);
        Scroller.ScrollToVerticalOffset(newOffset);
        e.Handled = true;
    }

    private void ScrollSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateScrollSpeedText(e.NewValue);
        if (_suppressScrollSpeedSave) return;
        try
        {
            if (App.Services.GetService(typeof(ResourceGrab.Core.Services.ConfigService)) is ResourceGrab.Core.Services.ConfigService ocfg2)
            {
                ocfg2.Current.ReaderScrollSpeed = ResourceGrab.Core.Services.ConfigService.NormalizeScrollSpeed(e.NewValue);
                ocfg2.Save();
            }
        }
        catch { }
    }

    private void UpdateScrollSpeedText(double v)
    {
        if (ScrollSpeedValueText != null)
        {
            ScrollSpeedValueText.Text = $"{v:0.0}x";
        }
    }

    private void ModeToggle_Click(object sender, RoutedEventArgs e)
    {
        var mode = ReferenceEquals(sender, ScrollModeButton) ? ReaderMode.Scroll
            : ReferenceEquals(sender, WallModeButton) ? ReaderMode.Wall
            : ReaderMode.Page;
        SwitchMode(mode);
    }

    /// <summary>
    /// 切换查看方式。三种模式共用同一份 <see cref="_loaded"/> 位图缓存，切回时不会重新下载；
    /// 切换只切容器可见性与「哪些索引该在可视区」的算法（单列流靠 <see cref="_tops"/>，画墙靠行列推算）。
    /// </summary>
    private void SwitchMode(ReaderMode mode)
    {
        if (_mode == mode)
        {
            UpdateModeUI();
            return;
        }

        // 离开当前模式前先把「看到哪一页」记下来，作为进入新模式后的落点
        if (_mode == ReaderMode.Scroll && _tops.Length > 0)
        {
            _currentPage = Math.Clamp(
                FindIndexAt(Scroller.VerticalOffset + Scroller.ViewportHeight / 2), 0, Math.Max(0, _hosts.Count - 1));
        }

        var from = _mode;
        _mode = mode;
        var wall = mode == ReaderMode.Wall;
        Scroller.Visibility = wall ? Visibility.Collapsed : Visibility.Visible;
        WallScroller.Visibility = wall ? Visibility.Visible : Visibility.Collapsed;
        UpdateModeUI();

        if (wall)
        {
            EnsureWallBuilt();
            ApplyWallLayout();
            if (_hosts.Count > 0)
            {
                WallScroller.ScrollToVerticalOffset(WallTileTop(_currentPage));
            }
            UpdateVisible();
            return;
        }

        // 从画墙切回单列流：Scroller 刚从 Collapsed 恢复，必须同步跑完一次布局，
        // 否则 _viewWidth/_viewHeight 还是折叠期的旧值（SizeChanged 是异步排队的）。
        Scroller.UpdateLayout();
        RefreshViewSize();
        ApplyZoomToAll();

        // 画墙 → 翻页：直接定位到之前记下的那一页（ApplyZoomToAll 里已按 _currentPage 定位，这里再稳一次）
        if (mode == ReaderMode.Page)
        {
            ScrollToPage(_currentPage);
        }
        else if (from == ReaderMode.Wall)
        {
            // 画墙 → 滚动：按记下的页滚到对应位置（_tops 刚由 ApplyZoomToAll 重建）
            var target = Math.Clamp(_currentPage, 0, Math.Max(0, _hosts.Count - 1));
            if (_tops.Length > target)
            {
                Scroller.ScrollToVerticalOffset(Math.Max(0, _tops[target] - 8));
            }
            UpdateVisible();
        }
    }

    /// <summary>重新读取单列流视口尺寸。折叠期 Scroller 尺寸为 0，不能直接采信。</summary>
    private void RefreshViewSize()
    {
        if (Scroller.ActualWidth > 0)
        {
            _viewWidth = Math.Max(320, Scroller.ActualWidth - 8);
        }
        if (Scroller.ActualHeight > 0)
        {
            _viewHeight = Math.Max(320, Scroller.ActualHeight - 8);
        }
    }

    /// <summary>同步查看方式按钮、状态提示，以及按模式启停的控件。</summary>
    private void UpdateModeUI()
    {
        ScrollModeButton.IsChecked = _mode == ReaderMode.Scroll;
        PageModeButton.IsChecked = _mode == ReaderMode.Page;
        WallModeButton.IsChecked = _mode == ReaderMode.Wall;
        SpreadToggleButton.IsChecked = _spread;
        // 双页是「同一行放两页」，与画墙的网格容器互不兼容
        SpreadToggleButton.IsEnabled = _mode != ReaderMode.Wall;

        // 这里只放「当前模式怎么操作」的短句。完整说明在各按钮的 ToolTip 里，
        // 原来那条 40 多字的常驻长文案白占了近 190px，且只在切模式时才变。
        ModeHintText.Text = _mode switch
        {
            ReaderMode.Page => _spread ? "← / → 翻一跨 · Ctrl + 滚轮缩放" : "← / → 翻页 · Ctrl + 滚轮缩放",
            ReaderMode.Wall => "滚轮滚动 · 缩放调整图片宽度 · 点击进入单图",
            _ => _spread ? "滚轮滚动 · 每行两页 · Ctrl + 滚轮缩放" : "滚轮滚动 · Ctrl + 滚轮缩放",
        };

        // 画墙下缩放改为调整贴片宽度，适应方式仍只对单列流有效。
        FitModeCombo.IsEnabled = _mode != ReaderMode.Wall;
        ZoomOutButton.IsEnabled = true;
        ZoomInButton.IsEnabled = true;
        ZoomSlider.IsEnabled = true;
        ZoomResetButton.IsEnabled = true;
        ZoomSlider.ToolTip = _mode == ReaderMode.Wall
            ? "调整画墙图片宽度（50% ~ 300%），100% 恢复自适应宽度"
            : "拖动缩放图片（50% ~ 300%），条漫建议 100% ~ 200%";

        // 滚动速度只对滚动模式生效：原实现常驻可拖，但翻页/画墙下拖了没有任何反应
        ScrollSpeedPanel.Visibility = _mode == ReaderMode.Scroll
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 画墙宽度与漫画阅读器缩放是两套状态，切模式时各自回显。
        UpdateZoomUI();
        UpdateFitCombo();
        UpdatePageText();
    }

    /// <summary>把 <see cref="_fitMode"/> 回写到下拉框（不触发 SelectionChanged）。</summary>
    private void UpdateFitCombo()
    {
        var index = _fitMode switch
        {
            FitMode.FitWidth => 0,
            FitMode.FitHeight => 1,
            FitMode.FitPage => 2,
            _ => 3,
        };
        if (FitModeCombo.SelectedIndex == index)
        {
            return;
        }
        _suppressFitCombo = true;
        try
        {
            FitModeCombo.SelectedIndex = index;
        }
        finally
        {
            _suppressFitCombo = false;
        }
    }

    /// <summary>适应方式下拉。原来是并排的四个按钮，既占宽，又与缩放滑杆重复表达同一件事。</summary>
    private void FitModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFitCombo || FitModeCombo.SelectedIndex < 0)
        {
            return;
        }
        _fitMode = FitModeCombo.SelectedIndex switch
        {
            0 => FitMode.FitWidth,
            1 => FitMode.FitHeight,
            2 => FitMode.FitPage,
            _ => FitMode.Actual,
        };
        _zoom = 1.0;
        ApplyZoomToAll();
        UpdateZoomUI();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(1.2);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.2);

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != ReaderMode.Wall && _fitMode == FitMode.Actual)
        {
            _fitMode = FitMode.FitWidth;
            UpdateFitCombo();
        }
        SetZoom(1.0);
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressZoomSlider)
        {
            return;
        }
        SetZoom(e.NewValue, fromSlider: true);
    }

    private void ZoomBy(double factor)
        => SetZoom((_mode == ReaderMode.Wall ? _wallZoom : _zoom) * factor);

    /// <summary>统一缩放入口：钳制范围、保持滚动位置、刷新布局与缩放 UI。</summary>
    private void SetZoom(double newZoom, bool fromSlider = false)
    {
        newZoom = Math.Clamp(newZoom, ZoomMin, ZoomMax);
        if (_mode == ReaderMode.Wall)
        {
            if (Math.Abs(newZoom - _wallZoom) < 0.001)
            {
                UpdateZoomUI();
                return;
            }
            var wallAnchor = VisibleWallRange();
            _wallZoom = newZoom;
            ApplyWallLayout();
            if (_hosts.Count > 0)
            {
                var anchor = Math.Clamp((wallAnchor.First + wallAnchor.Last) / 2, 0, _hosts.Count - 1);
                WallScroller.ScrollToVerticalOffset(WallTileTop(anchor));
            }
            UpdateVisible();
            UpdateZoomUI(fromSlider);
            return;
        }
        if (Math.Abs(newZoom - _zoom) < 0.001)
        {
            UpdateZoomUI();
            return;
        }
        if (_mode != ReaderMode.Wall && _fitMode == FitMode.Actual)
        {
            // 「实际大小」下没法谈缩放百分比，任何缩放动作都退回「适应宽度」
            _fitMode = FitMode.FitWidth;
            UpdateFitCombo();
        }
        // 滚动模式下按比例保持阅读位置，避免缩放后跳回顶部
        double ratio = -1;
        if (_mode == ReaderMode.Scroll && Scroller.ScrollableHeight > 0)
        {
            ratio = Scroller.VerticalOffset / Scroller.ScrollableHeight;
        }
        _zoom = newZoom;
        ApplyZoomToAll();
        if (!fromSlider && _mode == ReaderMode.Scroll && ratio >= 0 && Scroller.ScrollableHeight > 0)
        {
            Scroller.ScrollToVerticalOffset(Math.Clamp(ratio * Scroller.ScrollableHeight, 0, Scroller.ScrollableHeight));
        }
        UpdateZoomUI(fromSlider);
    }

    private void UpdateZoomUI(bool fromSlider = false)
    {
        var displayZoom = _mode == ReaderMode.Wall ? _wallZoom : _zoom;
        if (ZoomSlider != null && !fromSlider)
        {
            _suppressZoomSlider = true;
            try
            {
                ZoomSlider.Value = displayZoom;
            }
            finally
            {
                _suppressZoomSlider = false;
            }
        }
        if (ZoomResetButton != null)
        {
            ZoomResetButton.Content = $"{displayZoom * 100:0}%";
        }
    }

    /// <summary>键盘快捷键：Ctrl + +/- 缩放、Ctrl + 0 复位；翻页模式下 ←/→ 翻页。</summary>
    private void OnlineReaderView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl + 缩放快捷键（输入框聚焦时也生效：只响应带 Ctrl 的组合，不干扰打字）
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            switch (e.Key)
            {
                case Key.Add:
                case Key.OemPlus:
                    ZoomBy(1.2);
                    e.Handled = true;
                    return;
                case Key.Subtract:
                case Key.OemMinus:
                    ZoomBy(1 / 1.2);
                    e.Handled = true;
                    return;
                case Key.D0:
                case Key.NumPad0:
                    SetZoom(1.0);
                    e.Handled = true;
                    return;
            }
        }
        // 输入框里打字不触发翻页快捷键
        if (Keyboard.FocusedElement is TextBox or ComboBox)
        {
            return;
        }
        // ←/→ 翻页只在翻页模式下有意义（画墙用原生滚动，滚动模式用方向键会与平滑滚动打架）
        if (_mode != ReaderMode.Page)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Left:
            case Key.Up:
            case Key.PageUp:
                StepPage(-1);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Down:
            case Key.PageDown:
                StepPage(1);
                e.Handled = true;
                break;
        }
    }

    private void ApplyZoomToAll()
    {
        // 画墙按缩放值重排贴片；单列流的缩放/占位与它无关。
        if (_mode == ReaderMode.Wall)
        {
            ApplyWallLayout();
            UpdateVisible();
            return;
        }
        for (var i = 0; i < _hosts.Count; i++)
        {
            var host = _hosts[i];
            if (host.IsLoaded)
            {
                ApplyFitSize(host);
            }
            else
            {
                UpdatePlaceholderSize(i);
            }
        }
        SyncImageStackWidth();
        RebuildTops();
        if (_mode == ReaderMode.Page && _hosts.Count > 0)
        {
            var page = Math.Clamp(_currentPage, 0, _hosts.Count - 1);
            Scroller.ScrollToVerticalOffset(Math.Max(0, _tops[page] - 4));
        }
        UpdateVisible();
    }

    /// <summary>
    /// 控制图片流容器的宽度。
    /// <para>
    /// <b>双页时必须显式给宽度</b>：横向 <c>Auto</c> 的 ScrollViewer 一律以「无限宽」测量内容，
    /// WrapPanel 因此永远不会换行（实测：不给宽度时 60 张会排成 1 行、panel 宽 7188）。
    /// </para>
    /// <para>
    /// 单页时反而要清掉宽度，让容器按最宽的一张图撑开自己 —— 这样把图放大到超过视口时，
    /// ScrollViewer 的横向 extent 会跟着长、横向滚动条才够得着。实测给了显式宽度后，
    /// 放大到 3000px 时横向可滚动量仍然是 0，内容被裁掉且滚不到。
    /// </para>
    /// </summary>
    private void SyncImageStackWidth()
    {
        var singlePage = PagesPerRow <= 1;
        ImageStack.Orientation = singlePage ? Orientation.Vertical : Orientation.Horizontal;
        if (singlePage)
        {
            ImageStack.ClearValue(FrameworkElement.WidthProperty);
            return;
        }
        var widest = 0.0;
        foreach (var host in _hosts)
        {
            widest = Math.Max(widest, host.Root.Width);
        }
        // 某页被放大到超过单页槽位时，把容器放宽到该页宽度，避免内容被裁得滚不到
        ImageStack.Width = Math.Max(_viewWidth, widest + PageColumnGap);
    }

    /// <summary>所在行的第一页下标（双页模式下 0/2/4… 起行）。</summary>
    private int RowStart(int index) => Math.Max(0, index - index % Math.Max(1, PagesPerRow));

    /// <summary>
    /// 翻一「屏」：单页就是一页，双页是一整跨（两页）。
    /// 否则在双页模式下会停在半跨（比如显示「第 12-13 页」），既难看也和落点不一致。
    /// </summary>
    private void StepPage(int direction) => ScrollToPage(_currentPage + PagesPerRow * direction);

    private void ScrollToPage(int page)
    {
        _currentPage = Math.Clamp(page, 0, Math.Max(0, _hosts.Count - 1));
        if (PagesPerRow > 1)
        {
            _currentPage = RowStart(_currentPage);
        }
        if (_hosts.Count == 0)
        {
            return;
        }
        Scroller.ScrollToVerticalOffset(Math.Max(0, _tops[_currentPage] - 4));
        UpdateVisible();
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e) => StepPage(-1);

    private void NextPage_Click(object sender, RoutedEventArgs e) => StepPage(1);

    private void SpreadToggle_Click(object sender, RoutedEventArgs e)
        => SetSpread(SpreadToggleButton.IsChecked == true);

    /// <summary>切换单页 / 双页并排。行结构变了，必须重算页尺寸、重建 _tops，并把落点对齐到行首。</summary>
    private void SetSpread(bool spread)
    {
        if (!_allowSpread)
        {
            spread = false;
        }
        if (_spread == spread)
        {
            UpdateModeUI();
            return;
        }
        _spread = spread;
        SpreadToggleButton.IsChecked = spread;
        _currentPage = RowStart(_currentPage);
        ApplyZoomToAll();
        UpdateModeUI();
        if (_mode == ReaderMode.Page)
        {
            ScrollToPage(_currentPage);
        }
    }

    /// <summary>
    /// 翻页模式下点阅读区左右边缘翻页（阅读器标配）。
    /// 用 MouseUp 而不是 MouseDown：避免按下即翻页，也避免与拖滚动条混淆。
    /// </summary>
    private void Scroller_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode != ReaderMode.Page || _hosts.Count == 0)
        {
            return;
        }
        // 点滚动条（拖它是另一种翻页方式）、点加载失败遮罩（那是「重试」）都不该翻页
        if (IsInsideChrome(e.OriginalSource as DependencyObject))
        {
            return;
        }
        var width = Scroller.ActualWidth;
        if (width <= 0)
        {
            return;
        }
        var x = e.GetPosition(Scroller).X;
        if (x < width * EdgeZoneRatio)
        {
            StepPage(-1);
        }
        else if (x > width * (1 - EdgeZoneRatio))
        {
            StepPage(1);
        }
    }

    /// <summary>命中点是否落在「应该自己处理点击」的部件里：滚动条，或页面的加载/失败遮罩。</summary>
    private static bool IsInsideChrome(DependencyObject? node)
    {
        while (node is Visual visual)
        {
            if (visual is ScrollBar)
            {
                return true;
            }
            if (visual is FrameworkElement { Tag: StateLayerTag })
            {
                return true;
            }
            node = VisualTreeHelper.GetParent(visual);
        }
        return false;
    }

    // ====================== 页码跳转 ======================

    private void PageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_hosts.Count == 0)
        {
            return;
        }
        PageJumpTotalText.Text = $"/ {_hosts.Count}";
        PageJumpBox.Text = (_currentPage + 1).ToString();
        PageJumpPopup.IsOpen = true;
    }

    private void PageJumpBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                PageJumpGo_Click(sender, e);
                break;
            case Key.Escape:
                e.Handled = true;
                PageJumpPopup.IsOpen = false;
                break;
        }
    }

    private void PageJumpGo_Click(object sender, RoutedEventArgs e)
    {
        PageJumpPopup.IsOpen = false;
        if (!int.TryParse(PageJumpBox.Text?.Trim(), out var page))
        {
            ToastService.Show($"请输入 1 ~ {_hosts.Count} 之间的页码", ToastKind.Info);
            return;
        }
        ScrollToPage(page - 1);
    }

    private void UpdatePageText()
    {
        PageButton.IsEnabled = _hosts.Count > 0 && _mode != ReaderMode.Wall;
        if (_hosts.Count == 0)
        {
            PageText.Text = "";
            return;
        }
        if (_mode == ReaderMode.Wall)
        {
            // 画墙没有「当前页」的概念，报总数更有意义；翻页按钮在网格里也不适用
            PageText.Text = $"共 {_hosts.Count} 张";
            PrevPageButton.IsEnabled = false;
            NextPageButton.IsEnabled = false;
            return;
        }
        var current = Math.Clamp(
            _mode == ReaderMode.Page
                ? _currentPage + 1
                : FindIndexAt(Scroller.VerticalOffset + Scroller.ViewportHeight / 2) + 1,
            1, _hosts.Count);

        if (PagesPerRow > 1)
        {
            // 双页模式报整跨的范围，读作「第 11-12 / 26 页」比只报左页更符合直觉
            var start = RowStart(current - 1) + 1;
            var end = Math.Min(_hosts.Count, start + PagesPerRow - 1);
            PageText.Text = end > start
                ? $"第 {start}-{end} / {_hosts.Count} 页"
                : $"第 {start} / {_hosts.Count} 页";
            PrevPageButton.IsEnabled = start > 1;
            NextPageButton.IsEnabled = end < _hosts.Count;
            return;
        }

        PageText.Text = $"第 {current} / {_hosts.Count} 页";
        PrevPageButton.IsEnabled = current > 1;
        NextPageButton.IsEnabled = current < _hosts.Count;
    }

    private void UpdatePagingButtons()
    {
        PrevPageButton.IsEnabled = false;
        NextPageButton.IsEnabled = false;
        PageText.Text = _hosts.Count == 0 ? "" : $"第 1 / {_hosts.Count} 页";
    }

    // ====================== 进度 ======================

    private void SaveProgress()
    {
        if (_hosts.Count == 0 || _chapters.Count == 0)
        {
            return;
        }
        _progress[ProgressKey] = new ProgressEntry { Chapter = _chapterIndex, Image = _currentPage };
        ReadingProgress.Save(_progress);
    }

    private static BitmapSource DecodeBytes(byte[] bytes, int maxWidth)
    {
        // webp 内容直接走 ImageSharp 像素解码：系统 WIC 在部分环境解码 webp 只显示左上部分，
        // 且不抛异常，仅 try/catch 无法覆盖，因此按 magic 主动识别并绕开 WIC。
        if (IsWebp(bytes))
        {
            return DecodeWebp(bytes, maxWidth);
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = maxWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            // WIC 不可用（如旧系统缺 webp 解码器）且实际是 webp：再走 ImageSharp，其余交给调用方报错
            if (IsWebp(bytes))
            {
                return DecodeWebp(bytes, maxWidth);
            }
            throw;
        }
    }

    private static bool IsWebp(byte[] bytes)
        => bytes.Length >= 12
           && bytes[0] == 82 && bytes[1] == 73 && bytes[2] == 70 && bytes[3] == 70
           && bytes[8] == 87 && bytes[9] == 69 && bytes[10] == 66 && bytes[11] == 80;

    private static BitmapSource DecodeWebp(byte[] bytes, int maxWidth)
    {
        var decoded = WebpImageDecoder.Decode(bytes, maxWidth);
        if (decoded is null)
        {
            throw new InvalidOperationException("WebP 图片解码失败");
        }
        var source = BitmapSource.Create(
            decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null,
            decoded.BgraPixels, decoded.Width * 4);
        source.Freeze();
        return source;
    }
}
