using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ResourceGrab.App.Common;
using ResourceGrab.Core;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Utils;

namespace ResourceGrab.App.Views;

/// <summary>
/// 本地漫画阅读器：按章节浏览本地图片。
/// 性能设计：仅解码可视区域附近（±2 张）的图片，离开窗口即释放，控制大图内存；
/// 支持滚动/翻页两种模式；翻页模式可用 ←/→ 或滚轮翻页；
/// 适应宽度/适应高度/适应页面/实际大小缩放、Ctrl+滚轮缩放、阅读进度记忆。
/// </summary>
public partial class ReaderView : UserControl
{
    private enum FitMode { FitWidth, FitHeight, FitPage, Actual }

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };

    private readonly LocalComic _comic;
    private readonly List<string> _chapterDirs = new();
    private readonly Dictionary<string, List<string>> _chapterImages = new();
    private readonly Dictionary<int, BitmapSource> _loaded = new();
    private readonly HashSet<int> _loading = new();
    private readonly List<string> _images = new();
    private double[] _tops = Array.Empty<double>();
    private readonly List<double> _placeholders = new();

    private int _chapterIndex;
    private bool _suppressChapterCombo;
    private bool _switchingChapter;
    private double _viewWidth = 1200;
    private double _viewHeight = 800;
    private double _zoom = 1.0;
    private FitMode _fitMode = FitMode.FitPage;
    private bool _pageMode = true;
    private int _currentPage;
    private int _pendingScrollTo = -1;
    private bool _progressRestored;
    private bool _suppressScrollSpeedSave;

    private readonly DispatcherTimer _progressTimer;
    private Dictionary<string, ProgressEntry> _progress = new();

    public ReaderView(LocalComic comic)
    {
        InitializeComponent();
        _suppressScrollSpeedSave = true;
        if (App.Services.GetService(typeof(ResourceGrab.Core.Services.ConfigService)) is ResourceGrab.Core.Services.ConfigService cfg)
        {
            ScrollSpeedSlider.Value = ResourceGrab.Core.Services.ConfigService.NormalizeScrollSpeed(cfg.Current.ReaderScrollSpeed);
            UpdateScrollSpeedText(ScrollSpeedSlider.Value);
        }
        else
        {
            ScrollSpeedSlider.Value = 1.0;
            UpdateScrollSpeedText(1.0);
        }
        _suppressScrollSpeedSave = false;
        _comic = comic;
        TitleText.Text = string.IsNullOrEmpty(comic.NameCn) ? comic.Name : comic.NameCn;

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _progressTimer.Tick += (_, _) => SaveProgress();
        Unloaded += (_, _) => SaveProgress();

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _progress = ReadingProgress.Load();
        var dirs = await Task.Run(() =>
        {
            var result = Directory.EnumerateDirectories(_comic.Path)
                .Where(d => !Path.GetFileName(d).StartsWith(".下载中-", StringComparison.Ordinal))
                .OrderBy(Path.GetFileName, NaturalComparer.Instance)
                .ToList();
            return result.Count > 0 ? result : new List<string> { _comic.Path };
        });
        _chapterDirs.Clear();
        _chapterDirs.AddRange(dirs);
        ChapterCombo.ItemsSource = dirs.Select(Path.GetFileName).ToList();

        var restore = _progress.TryGetValue(_comic.Path, out var entry)
            ? entry
            : new ProgressEntry { Chapter = 0, Image = 0 };
        _progressRestored = true;
        LoadChapter(restore.Chapter, restore.Image);
    }

    // ====================== 章节 ======================

    private void LoadChapter(int chapterIndex, int scrollToImage = 0)
    {
        if (_switchingChapter)
        {
            return;
        }
        _switchingChapter = true;
        try
        {
            if (chapterIndex < 0) chapterIndex = 0;
            if (chapterIndex >= _chapterDirs.Count) chapterIndex = _chapterDirs.Count - 1;
            _chapterIndex = chapterIndex;

            ReleaseAll();
            var chapterDir = _chapterDirs[chapterIndex];
            _images.Clear();
            _images.AddRange(_chapterImages.TryGetValue(chapterDir, out var cached)
                ? cached
                : EnumerateImages(chapterDir));
            _chapterImages[chapterDir] = _images;

            _placeholders.Clear();
            foreach (var path in _images)
            {
                var placeholder = new Image
                {
                    Stretch = System.Windows.Media.Stretch.Fill,
                    Margin = new Thickness(0, 2, 0, 2),
                };
                ImageStack.Children.Add(placeholder);
                _placeholders.Add(800);
                UpdatePlaceholderSize(ImageStack.Children.Count - 1);
            }
            RebuildTops();

            _suppressChapterCombo = true;
            ChapterCombo.SelectedIndex = chapterIndex;
            _suppressChapterCombo = false;

            _pendingScrollTo = -1;
            Scroller.ScrollToTop();
            if (_pageMode)
            {
                ScrollToPage(scrollToImage);
            }
            else if (scrollToImage > 0 && scrollToImage < _tops.Length)
            {
                _pendingScrollTo = scrollToImage;
                Scroller.ScrollToVerticalOffset(Math.Max(0, _tops[scrollToImage] - 8));
            }
            UpdateVisible();
            UpdatePageText();
        }
        finally
        {
            _switchingChapter = false;
        }
    }

    private List<string> EnumerateImages(string chapterDir)
    {
        var result = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(chapterDir))
            {
                if (ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                {
                    result.Add(file);
                }
            }
        }
        catch
        {
            // 目录不存在或不可读时忽略
        }
        return result.OrderBy(Path.GetFileName, NaturalComparer.Instance).ToList();
    }

    private void ChapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChapterCombo || ChapterCombo.SelectedIndex < 0 || _chapterDirs.Count == 0)
        {
            return;
        }
        SaveProgress();
        LoadChapter(ChapterCombo.SelectedIndex, 0);
    }

    private void PrevChapter_Click(object sender, RoutedEventArgs e) => LoadChapter(_chapterIndex - 1, 0);

    private void NextChapter_Click(object sender, RoutedEventArgs e) => LoadChapter(_chapterIndex + 1, 0);

    // ====================== 图片懒加载 ======================

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateVisible();
        if (!_pageMode && !_switchingChapter && Scroller.ScrollableHeight > 300
            && Scroller.ScrollableHeight - Scroller.VerticalOffset < 150)
        {
            SaveProgress();
            LoadChapter(_chapterIndex + 1, 0);
            return;
        }
        _progressTimer.Stop();
        _progressTimer.Start();
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewWidth = Math.Max(320, e.NewSize.Width - 16);
        _viewHeight = Math.Max(320, e.NewSize.Height - 8);
        ResizeLoadedImages();
        if (_pageMode)
        {
            ScrollToPage(_currentPage);
        }
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ZoomBy(e.Delta > 0 ? 1.15 : 1 / 1.15);
            e.Handled = true;
            return;
        }
        if (_pageMode)
        {
            var pageHeight = _currentPage < _placeholders.Count ? _placeholders[_currentPage] : 0;
            if (pageHeight <= Scroller.ViewportHeight + 2)
            {
                GoToPage(_currentPage + (e.Delta > 0 ? -1 : 1));
                e.Handled = true;
            }
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
            if (App.Services.GetService(typeof(ResourceGrab.Core.Services.ConfigService)) is ResourceGrab.Core.Services.ConfigService cfg)
            {
                cfg.Current.ReaderScrollSpeed = ResourceGrab.Core.Services.ConfigService.NormalizeScrollSpeed(e.NewValue);
                cfg.Save();
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

    private void ZoomBy(double factor)
    {
        _zoom = Math.Clamp(_zoom * factor, 0.2, 8.0);
        if (_fitMode == FitMode.Actual)
        {
            _fitMode = FitMode.FitWidth;
        }
        ResizeLoadedImages();
        if (_pageMode)
        {
            ScrollToPage(_currentPage);
        }
    }

    private void FitWidth_Click(object sender, RoutedEventArgs e) => SetFitMode(FitMode.FitWidth);

    private void FitHeight_Click(object sender, RoutedEventArgs e) => SetFitMode(FitMode.FitHeight);

    private void FitPage_Click(object sender, RoutedEventArgs e) => SetFitMode(FitMode.FitPage);

    private void ActualSize_Click(object sender, RoutedEventArgs e) => SetFitMode(FitMode.Actual);

    private void SetFitMode(FitMode mode)
    {
        _fitMode = mode;
        _zoom = 1.0;
        ResizeLoadedImages();
        if (_pageMode)
        {
            ScrollToPage(_currentPage);
        }
    }

    private void ResizeLoadedImages()
    {
        for (var i = 0; i < _images.Count && i < ImageStack.Children.Count; i++)
        {
            if (ImageStack.Children[i] is not Image image)
            {
                continue;
            }
            if (_loaded.TryGetValue(i, out var bitmap))
            {
                ApplyFitSize(image, bitmap.PixelWidth, bitmap.PixelHeight);
                _placeholders[i] = image.Height;
            }
            else
            {
                UpdatePlaceholderSize(i);
            }
        }
        RebuildTops();
        UpdateVisible();
    }

    /// <summary>按当前适应方式计算图片显示尺寸。</summary>
    private void ApplyFitSize(Image image, double pixelWidth, double pixelHeight)
    {
        var pw = Math.Max(1, pixelWidth);
        var ph = Math.Max(1, pixelHeight);
        switch (_fitMode)
        {
            case FitMode.FitWidth:
                image.Width = Math.Max(1, _viewWidth * _zoom);
                image.Height = Math.Max(1, image.Width * ph / pw);
                break;
            case FitMode.FitHeight:
                image.Height = Math.Max(1, _viewHeight * _zoom);
                image.Width = Math.Max(1, image.Height * pw / ph);
                break;
            case FitMode.FitPage:
                var scale = Math.Min(_viewWidth / pw, _viewHeight / ph) * _zoom;
                image.Width = Math.Max(1, pw * scale);
                image.Height = Math.Max(1, ph * scale);
                break;
            case FitMode.Actual:
                image.Width = pw;
                image.Height = ph;
                break;
        }
    }

    /// <summary>未加载占位图：按适应方式估算尺寸，保证布局稳定。</summary>
    private void UpdatePlaceholderSize(int index)
    {
        if (index < 0 || index >= ImageStack.Children.Count)
        {
            return;
        }
        if (ImageStack.Children[index] is not Image image || _loaded.ContainsKey(index))
        {
            return;
        }
        switch (_fitMode)
        {
            case FitMode.FitHeight:
                image.Height = Math.Max(1, _viewHeight * _zoom);
                image.Width = Math.Max(1, _viewWidth * _zoom);
                break;
            case FitMode.FitPage:
                image.Width = Math.Max(1, _viewWidth * _zoom);
                image.Height = Math.Max(1, _viewHeight * _zoom);
                break;
            case FitMode.Actual:
                image.Width = _viewWidth;
                image.Height = 800;
                break;
            default:
                image.Width = Math.Max(1, _viewWidth * _zoom);
                image.Height = 800;
                break;
        }
        _placeholders[index] = image.Height;
    }

    private void UpdateVisible()
    {
        if (_images.Count == 0)
        {
            return;
        }
        if (_pageMode)
        {
            var page = Math.Clamp(_currentPage, 0, _images.Count - 1);
            EnsureImage(page);
            for (var i = 0; i < _images.Count; i++)
            {
                if (i != page)
                {
                    ReleaseImage(i);
                }
            }
            UpdatePageText();
            return;
        }
        var first = FindIndexAt(Math.Max(0, Scroller.VerticalOffset - 400));
        var last = FindIndexAt(Math.Min(Scroller.VerticalOffset + Scroller.ViewportHeight + 400, _tops.Length > 0 ? _tops[^1] : 0));
        for (var i = first; i <= last; i++)
        {
            EnsureImage(i);
        }
        for (var i = 0; i < _images.Count; i++)
        {
            if (i < first - 6 || i > last + 6)
            {
                ReleaseImage(i);
            }
        }
        UpdatePageText();
    }

    private void EnsureImage(int index)
    {
        if (index < 0 || index >= _images.Count || _loaded.ContainsKey(index) || _loading.Contains(index))
        {
            return;
        }
        _loading.Add(index);
        var path = _images[index];
        _ = Task.Run(() => DecodeImage(path, 1600))
            .ContinueWith(t =>
            {
                _loading.Remove(index);
                if (t.IsCompletedSuccessfully)
                {
                    AttachImage(index, t.Result);
                }
                else
                {
                    Console.Error.WriteLine($"阅读器图片解码失败: {path}: {t.Exception?.GetBaseException().Message}");
                    RebuildTops();
                    UpdateVisible();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void AttachImage(int index, BitmapSource bitmap)
    {
        if (index >= ImageStack.Children.Count)
        {
            return;
        }
        _loaded[index] = bitmap;
        if (ImageStack.Children[index] is Image image)
        {
            ApplyFitSize(image, bitmap.PixelWidth, bitmap.PixelHeight);
            image.Source = bitmap;
        }
        if (ImageStack.Children[index] is Image sized)
        {
            _placeholders[index] = sized.ActualHeight > 0 ? sized.ActualHeight : sized.Height;
        }
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

    private void ReleaseImage(int index)
    {
        if (_loading.Contains(index))
        {
            return;
        }
        if (_loaded.Remove(index, out var bitmap))
        {
            if (index < ImageStack.Children.Count && ImageStack.Children[index] is Image image)
            {
                image.Source = null;
            }
        }
    }

    private void ReleaseAll()
    {
        for (var i = 0; i < _images.Count; i++)
        {
            ReleaseImage(i);
        }
        ImageStack.Children.Clear();
        _images.Clear();
        _loading.Clear();
        _placeholders.Clear();
        _tops = Array.Empty<double>();
    }

    private void RebuildTops()
    {
        _tops = new double[_placeholders.Count + 1];
        for (var i = 0; i < _placeholders.Count; i++)
        {
            _tops[i + 1] = _tops[i] + _placeholders[i] + 4;
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

    private static BitmapSource DecodeImage(string path, int decodeWidth)
    {
        // 优先使用系统 WIC 直读（Windows 11 原生支持 webp，零转码开销）
        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            // 系统缺少解码器（如旧版 Windows 无 webp）：仅 webp 走 ImageSharp 像素拷贝兜底
            if (!string.Equals(Path.GetExtension(path), ".webp", StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }
            var decoded = WebpImageDecoder.Decode(path, decodeWidth);
            if (decoded is null)
            {
                throw;
            }
            var source = BitmapSource.Create(
                decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null,
                decoded.BgraPixels, decoded.Width * 4);
            source.Freeze();
            return source;
        }
    }

    // ====================== 页码 / 返回 ======================

    private void UpdatePageText()
    {
        if (_images.Count == 0)
        {
            PageText.Text = "0 / 0";
            if (JumpTotalText != null) JumpTotalText.Text = "0";
            return;
        }
        var index = (_pageMode ? _currentPage : FindIndexAt(Scroller.VerticalOffset)) + 1;
        PageText.Text = $"{index} / {_images.Count} · 第 {_chapterIndex + 1}/{_chapterDirs.Count} 章";
        if (JumpTotalText != null) JumpTotalText.Text = _images.Count.ToString();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        SaveProgress();
        Navigation.Back();
    }

    // ====================== 阅读模式 / 翻页 ======================

    private void ModeToggle_Click(object sender, RoutedEventArgs e)
    {
        var wantPageMode = ReferenceEquals(sender, PageModeButton);
        PageModeButton.IsChecked = wantPageMode;
        ScrollModeButton.IsChecked = !wantPageMode;
        SetPageMode(wantPageMode);
    }

    private void SetPageMode(bool enabled)
    {
        if (_pageMode == enabled)
        {
            return;
        }
        _pageMode = enabled;
        if (enabled)
        {
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            ModeHintText.Text = "← / → 或滚轮翻页 · Ctrl + 滚轮缩放";
            var page = _images.Count == 0 ? 0 : FindIndexAt(Scroller.VerticalOffset + Scroller.ViewportHeight / 2);
            ScrollToPage(page);
        }
        else
        {
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            ModeHintText.Text = "滚动浏览 · Ctrl + 滚轮缩放";
            UpdateVisible();
        }
    }

    private void GoToPage(int page)
    {
        if (_images.Count == 0)
        {
            return;
        }
        if (page < 0)
        {
            if (_chapterIndex > 0)
            {
                LoadChapter(_chapterIndex - 1, int.MaxValue);
            }
            return;
        }
        if (page >= _images.Count)
        {
            if (_chapterIndex < _chapterDirs.Count - 1)
            {
                LoadChapter(_chapterIndex + 1, 0);
            }
            return;
        }
        ScrollToPage(page);
    }

    private void ScrollToPage(int page)
    {
        if (_images.Count == 0)
        {
            _currentPage = 0;
            UpdatePageText();
            return;
        }
        _currentPage = Math.Clamp(page, 0, _images.Count - 1);
        var top = _tops[_currentPage];
        var offset = Math.Max(0, top - 8);
        if (_pageMode)
        {
            var pageHeight = _currentPage < _placeholders.Count ? _placeholders[_currentPage] : 0;
            offset = Math.Max(0, top - Math.Max(0, (Scroller.ViewportHeight - pageHeight) / 2));
        }
        Scroller.ScrollToVerticalOffset(offset);
        UpdateVisible();
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pageMode)
        {
            GoToPage(_currentPage - 1);
        }
        else
        {
            ScrollToPage(FindIndexAt(Scroller.VerticalOffset) - 1);
        }
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pageMode)
        {
            GoToPage(_currentPage + 1);
        }
        else
        {
            ScrollToPage(FindIndexAt(Scroller.VerticalOffset) + 1);
        }
    }

    private void PageJumpBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");
    }

    private void PageJumpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DoPageJump();
            e.Handled = true;
        }
    }

    private void PageJump_Click(object sender, RoutedEventArgs e) => DoPageJump();

    private void DoPageJump()
    {
        if (_images.Count == 0) return;
        if (!int.TryParse(PageJumpBox.Text?.Trim(), out var p)) return;
        var target = Math.Clamp(p - 1, 0, _images.Count - 1);
        if (_pageMode) GoToPage(target); else ScrollToPage(target);
        PageJumpBox.Text = (target + 1).ToString();
        PageJumpBox.SelectAll();
    }

    private void ReaderView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_pageMode || Keyboard.FocusedElement is ComboBox)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Left:
            case Key.Up:
            case Key.PageUp:
                GoToPage(_currentPage - 1);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Down:
            case Key.PageDown:
                GoToPage(_currentPage + 1);
                e.Handled = true;
                break;
        }
    }

    // ====================== 进度记忆 ======================

    private void SaveProgress()
    {
        _progressTimer.Stop();
        if (_images.Count == 0 || !_progressRestored)
        {
            return;
        }
        var index = _pageMode ? _currentPage : FindIndexAt(Math.Max(0, Scroller.VerticalOffset));
        _progress[_comic.Path] = new ProgressEntry { Chapter = _chapterIndex, Image = index };
        ReadingProgress.Save(_progress);
    }

    private class NaturalComparer : IComparer<string?>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? a, string? b)
        {
            if (a is null || b is null)
            {
                return string.Compare(a, b, StringComparison.Ordinal);
            }
            var ai = 0;
            var bi = 0;
            while (ai < a.Length && bi < b.Length)
            {
                var da = char.IsDigit(a[ai]);
                var db = char.IsDigit(b[bi]);
                if (da && db)
                {
                    var ae = ai;
                    while (ae < a.Length && char.IsDigit(a[ae])) ae++;
                    var be = bi;
                    while (be < b.Length && char.IsDigit(b[be])) be++;
                    var na = long.Parse(a.AsSpan(ai, ae - ai));
                    var nb = long.Parse(b.AsSpan(bi, be - bi));
                    if (na != nb)
                    {
                        return na.CompareTo(nb);
                    }
                    ai = ae;
                    bi = be;
                }
                else
                {
                    var cmp = string.Compare(a.Substring(ai, 1), b.Substring(bi, 1), StringComparison.OrdinalIgnoreCase);
                    if (cmp != 0)
                    {
                        return cmp;
                    }
                    ai++;
                    bi++;
                }
            }
            return a.Length.CompareTo(b.Length);
        }
    }
}

/// <summary>阅读进度（漫画路径 → 章节 + 图片索引），持久化到 reading-progress.json。</summary>
public class ProgressEntry
{
    public int Chapter { get; set; }
    public int Image { get; set; }
}

/// <summary>阅读进度读写（原子写入）。</summary>
public static class ReadingProgress
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(AppPaths.AppDataDir, "reading-progress.json");

    public static Dictionary<string, ProgressEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, ProgressEntry>>(File.ReadAllText(FilePath));
                if (data is not null)
                {
                    return data;
                }
            }
        }
        catch
        {
            // 损坏时返回空
        }
        return new Dictionary<string, ProgressEntry>();
    }

    public static void Save(Dictionary<string, ProgressEntry> data)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(temp, FilePath, true);
        }
        catch
        {
            // 保存失败不影响阅读
        }
    }
}



