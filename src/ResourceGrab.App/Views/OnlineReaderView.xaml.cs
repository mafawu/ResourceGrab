using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    private sealed class PageHost
    {
        public required Grid Root { get; init; }
        public required Image Image { get; init; }
        public required Border StateLayer { get; init; }
        public required TextBlock StateText { get; init; }
        public required Controls.LoadingIndicator StateIndicator { get; init; }
        public int PixelWidth;
        public int PixelHeight;
        public double HeightEstimate = 800;
        public bool IsLoaded;
        public bool IsLoading;
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
    private bool _pageMode = true;
    private int _currentPage;
    private int _pendingScrollTo = -1;
    private double _viewWidth = 1200;
    private double _viewHeight = 800;
    private double _zoom = 1.0;
    private const double ZoomMin = 0.5;
    private const double ZoomMax = 3.0;
    private bool _suppressZoomSlider;
    private bool _suppressChapterCombo;
    private bool _suppressScrollSpeedSave;

    private readonly DispatcherTimer _progressTimer;
    /// <summary>滚动模式防抖：停止滚动 120ms 后才加载可视区，避免惯性滚动触发大量图片请求。</summary>
    private readonly DispatcherTimer _scrollDebounce;
    private Dictionary<string, ProgressEntry> _progress = new();

    public OnlineReaderView(IComicSource source, IReadOnlyList<Chapter> chapters, int startIndex)
    {
        InitializeComponent();
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

        _suppressChapterCombo = true;
        ChapterCombo.ItemsSource = chapters.Select(c => c.Title).ToList();

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
            if (_pageMode)
            {
                _currentPage = startPage;
                Scroller.ScrollToTop();
                ScrollToPage(startPage);
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

    private void BuildPlaceholders()
    {
        var background = (Brush)FindResource("HoverBgBrush");
        var stateBackground = (Brush)FindResource("SurfaceBrush");
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
            var stateLayer = new Border
            {
                Background = stateBackground,
                Child = stateStack,
                Visibility = Visibility.Collapsed,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
            };
            var root = new Grid
            {
                Background = background,
                Margin = new Thickness(0, 2, 0, 2),
            };
            root.Children.Add(image);
            root.Children.Add(stateLayer);

            var host = new PageHost { Root = root, Image = image, StateLayer = stateLayer, StateText = stateText, StateIndicator = stateIndicator };
            stateLayer.MouseLeftButtonDown += (_, _) => RetryPage(_hosts.IndexOf(host));
            ImageStack.Children.Add(root);
            _hosts.Add(host);
            UpdatePlaceholderSize(i);
        }
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
        if (_pageMode)
        {
            var page = Math.Clamp(_currentPage, 0, _hosts.Count - 1);
            EnsureImage(page);
            if (page > 0)
            {
                EnsureImage(page - 1);
            }
            if (page < _hosts.Count - 1)
            {
                EnsureImage(page + 1);
            }
            for (var i = 0; i < _hosts.Count; i++)
            {
                if (i != page && i != page - 1 && i != page + 1)
                {
                    ReleaseImage(i);
                }
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

    private void EnsureImage(int index)
    {
        if (index < 0 || index >= _hosts.Count)
        {
            return;
        }
        var host = _hosts[index];
        if (host.IsLoaded || host.IsLoading)
        {
            return;
        }
        host.IsLoading = true;
        host.StateLayer.Visibility = Visibility.Visible;
        host.StateIndicator.Visibility = Visibility.Visible;
        host.StateText.Visibility = Visibility.Collapsed;
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
            return DecodeBytes(bytes);
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
        if (host.IsLoaded) return;
        host.IsLoading = false;
        host.StateLayer.Visibility = Visibility.Collapsed;
    }

    /// <summary>取消可视窗口之外的在途加载（滚动停稳时调用）。翻页模式保留当前页±4，滚动模式保留可视区±8。</summary>
    private void CancelOutOfWindowLoads()
    {
        if (_loadCts.Count == 0) return;
        int keepFrom, keepTo;
        if (_pageMode)
        {
            var page = Math.Clamp(_currentPage, 0, _hosts.Count - 1);
            keepFrom = page - 4;
            keepTo = page + 4;
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
        RebuildTops();

        if (_pageMode && index == _currentPage)
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
        host.StateLayer.Visibility = Visibility.Visible;
        host.StateIndicator.Visibility = Visibility.Collapsed;
        host.StateText.Visibility = Visibility.Visible;
        host.StateText.Text = message;
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
            host.StateLayer.Visibility = Visibility.Collapsed;
        }
    }

    private void ReleaseAll()
    {
        ImageStack.Children.Clear();
        _hosts.Clear();
        _loaded.Clear();
        _loading.Clear();
        _tops = Array.Empty<double>();
        _pages = Array.Empty<ImagePage>();
    }

    // ====================== 布局与缩放 ======================

    private void RebuildTops()
    {
        _tops = new double[_hosts.Count + 1];
        for (var i = 0; i < _hosts.Count; i++)
        {
            _tops[i + 1] = _tops[i] + _hosts[i].HeightEstimate + 4;
        }
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

    private void UpdatePlaceholderSize(int index)
    {
        var host = _hosts[index];
        var aspect = _aspectRatio;
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
                var scale = Math.Min((_viewWidth * _zoom) / aspect, _viewHeight * _zoom);
                w = Math.Max(1, aspect * scale);
                h = Math.Max(1, scale);
                break;
            case FitMode.Actual:
                w = Math.Max(1, _viewWidth);
                h = Math.Max(1, w / aspect);
                break;
            default:
                // FitWidth：一张图宽=视口宽，高按该图宽高比
                w = Math.Max(1, _viewWidth * _zoom);
                h = Math.Max(1, w / aspect);
                break;
        }
        host.Root.Width = w;
        host.Root.Height = h;
        host.HeightEstimate = h;
    }
    private void ApplyFitSize(PageHost host)
    {
        var pw = Math.Max(1, host.PixelWidth);
        var ph = Math.Max(1, host.PixelHeight);
        double w;
        double h;
        switch (_fitMode)
        {
            case FitMode.FitHeight:
                h = Math.Max(1, _viewHeight * _zoom);
                w = Math.Max(1, h * pw / ph);
                break;
            case FitMode.FitPage:
                var scale = Math.Min((_viewWidth * _zoom) / pw, (_viewHeight * _zoom) / ph);
                w = Math.Max(1, pw * scale);
                h = Math.Max(1, ph * scale);
                break;
            case FitMode.Actual:
                w = pw;
                h = ph;
                break;
            default:
                w = Math.Max(1, _viewWidth * _zoom);
                h = Math.Max(1, w * ph / pw);
                break;
        }
        host.Image.Width = w;
        host.Image.Height = h;
        host.Root.Width = w;
        host.Root.Height = h;
        host.HeightEstimate = h;
    }

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_pageMode)
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
        // 与本地阅读器一致，减掉滚动条/边距余量并设下限。
        _viewWidth = Math.Max(320, e.NewSize.Width - 16);
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
        if (_pageMode)
        {
            if (e.Delta > 0)
            {
                ScrollToPage(_currentPage - 1);
            }
            else
            {
                ScrollToPage(_currentPage + 1);
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
        _pageMode = ReferenceEquals(sender, PageModeButton);
        ScrollModeButton.IsChecked = !_pageMode;
        PageModeButton.IsChecked = _pageMode;
        ModeHintText.Text = _pageMode
            ? "翻页浏览 · ←/→ 或滚轮翻页 · 缩放：滑杆/± 或 Ctrl + 滚轮"
            : "滚动浏览 · 缩放：滑杆/± 或 Ctrl + 滚轮";
        if (_pageMode)
        {
            _currentPage = Math.Clamp(FindIndexAt(Scroller.VerticalOffset + Scroller.ViewportHeight / 2), 0, Math.Max(0, _hosts.Count - 1));
            ScrollToPage(_currentPage);
        }
        else
        {
            UpdateVisible();
        }
    }

    private void FitWidth_Click(object sender, RoutedEventArgs e)
    {
        _fitMode = FitMode.FitWidth;
        _zoom = 1.0;
        ApplyZoomToAll();
        UpdateZoomUI();
    }

    private void FitHeight_Click(object sender, RoutedEventArgs e)
    {
        _fitMode = FitMode.FitHeight;
        _zoom = 1.0;
        ApplyZoomToAll();
        UpdateZoomUI();
    }

    private void FitPage_Click(object sender, RoutedEventArgs e)
    {
        _fitMode = FitMode.FitPage;
        _zoom = 1.0;
        ApplyZoomToAll();
        UpdateZoomUI();
    }

    private void ActualSize_Click(object sender, RoutedEventArgs e)
    {
        _fitMode = FitMode.Actual;
        _zoom = 1.0;
        ApplyZoomToAll();
        UpdateZoomUI();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(1.2);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.2);

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        if (_fitMode == FitMode.Actual)
        {
            _fitMode = FitMode.FitWidth;
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

    private void ZoomBy(double factor) => SetZoom(_zoom * factor);

    /// <summary>统一缩放入口：钳制范围、保持滚动位置、刷新布局与缩放 UI。</summary>
    private void SetZoom(double newZoom, bool fromSlider = false)
    {
        newZoom = Math.Clamp(newZoom, ZoomMin, ZoomMax);
        if (Math.Abs(newZoom - _zoom) < 0.001)
        {
            UpdateZoomUI();
            return;
        }
        if (_fitMode == FitMode.Actual)
        {
            _fitMode = FitMode.FitWidth;
        }
        // 滚动模式下按比例保持阅读位置，避免缩放后跳回顶部
        double ratio = -1;
        if (!_pageMode && Scroller.ScrollableHeight > 0)
        {
            ratio = Scroller.VerticalOffset / Scroller.ScrollableHeight;
        }
        _zoom = newZoom;
        ApplyZoomToAll();
        if (!fromSlider && !_pageMode && ratio >= 0 && Scroller.ScrollableHeight > 0)
        {
            Scroller.ScrollToVerticalOffset(Math.Clamp(ratio * Scroller.ScrollableHeight, 0, Scroller.ScrollableHeight));
        }
        UpdateZoomUI(fromSlider);
    }

    private void UpdateZoomUI(bool fromSlider = false)
    {
        if (ZoomSlider != null && !fromSlider)
        {
            _suppressZoomSlider = true;
            try
            {
                ZoomSlider.Value = _zoom;
            }
            finally
            {
                _suppressZoomSlider = false;
            }
        }
        if (ZoomResetButton != null)
        {
            ZoomResetButton.Content = $"{_zoom * 100:0}%";
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
        if (!_pageMode)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Left:
            case Key.Up:
            case Key.PageUp:
                ScrollToPage(_currentPage - 1);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Down:
            case Key.PageDown:
                ScrollToPage(_currentPage + 1);
                e.Handled = true;
                break;
        }
    }

    private void ApplyZoomToAll()
    {
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
        RebuildTops();
        if (_pageMode && _hosts.Count > 0)
        {
            var page = Math.Clamp(_currentPage, 0, _hosts.Count - 1);
            Scroller.ScrollToVerticalOffset(Math.Max(0, _tops[page] - 4));
        }
        UpdateVisible();
    }

    private void ScrollToPage(int page)
    {
        _currentPage = Math.Clamp(page, 0, Math.Max(0, _hosts.Count - 1));
        if (_hosts.Count == 0)
        {
            return;
        }
        Scroller.ScrollToVerticalOffset(Math.Max(0, _tops[_currentPage] - 4));
        UpdateVisible();
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e) => ScrollToPage(_currentPage - 1);

    private void NextPage_Click(object sender, RoutedEventArgs e) => ScrollToPage(_currentPage + 1);

    private void UpdatePageText()
    {
        if (_hosts.Count == 0)
        {
            PageText.Text = "";
            return;
        }
        var current = _pageMode
            ? _currentPage + 1
            : FindIndexAt(Scroller.VerticalOffset + Scroller.ViewportHeight / 2) + 1;
        PageText.Text = $"第 {Math.Clamp(current, 1, _hosts.Count)} / {_hosts.Count} 页";
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

    private static BitmapSource DecodeBytes(byte[] bytes)
    {
        // webp 内容直接走 ImageSharp 像素解码：系统 WIC 在部分环境解码 webp 只显示左上部分，
        // 且不抛异常，仅 try/catch 无法覆盖，因此按 magic 主动识别并绕开 WIC。
        if (IsWebp(bytes))
        {
            return DecodeWebp(bytes);
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 1600;
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
                return DecodeWebp(bytes);
            }
            throw;
        }
    }

    private static bool IsWebp(byte[] bytes)
        => bytes.Length >= 12
           && bytes[0] == 82 && bytes[1] == 73 && bytes[2] == 70 && bytes[3] == 70
           && bytes[8] == 87 && bytes[9] == 69 && bytes[10] == 66 && bytes[11] == 80;

    private static BitmapSource DecodeWebp(byte[] bytes)
    {
        var decoded = WebpImageDecoder.Decode(bytes, 1600);
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
