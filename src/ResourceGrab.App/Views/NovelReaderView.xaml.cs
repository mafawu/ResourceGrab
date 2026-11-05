using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ResourceGrab.App.Common;
using ResourceGrab.App.Services;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Views;

public partial class NovelReaderView : UserControl
{
    private string _path = "";
    private string _content = "";
    private List<string> _chunks = new();
    private int _page = 1;
    private int _pageCount = 1;
    private int _charsPerPage = 1000;
    private const int ScrollChunkSize = 1200;
    private double _fontSize = 17;
    private bool _isScrollMode = false;
    private int _bgIndex = 0;
    private string _fontFamily = "Microsoft YaHei UI";
    private double _lineHeight = 1.9;
    private double _paragraphIndentEm = 2;
    private bool _autoDetectEncoding = true;
    private NovelReadingHistoryService? _history;
    private NovelReaderSettingsService? _settings;

    /// <summary>阅读进度键：本地书=文件路径，在线书=online:{source}:{book}:{chapter}。</summary>
    private string _historyKey = "";

    // 在线阅读态
    private NovelOnlineSession? _session;
    private int _onlineIndex = -1;
    private bool _switchingChapter;
    private readonly List<ChapterNavItem> _chapterItems = new();

    public NovelReaderView()
    {
        InitializeComponent();
        try { _history = App.Services.GetService<NovelReadingHistoryService>(); } catch { }
        try { _settings = App.Services.GetService<NovelReaderSettingsService>(); } catch { }
        Loaded += NovelReaderView_Loaded;
        SizeChanged += (_, _) => { if (_isScrollMode) ScrollList.UpdateLayout(); };
    }

    private void NovelReaderView_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_settings == null) _settings = App.Services.GetService<NovelReaderSettingsService>();
            if (_settings != null)
            {
                _isScrollMode = _settings.Current.IsScrollMode;
                _bgIndex = Math.Clamp(_settings.Current.BgIndex, 0, NovelReaderBgPresets.Presets.Length - 1);
                _fontSize = _settings.Current.FontSize;
                _fontFamily = _settings.Current.FontFamily;
                _lineHeight = _settings.Current.LineHeight;
                _paragraphIndentEm = _settings.Current.ParagraphIndentEm;
                _autoDetectEncoding = _settings.Current.AutoDetectEncoding;
            }
        }
        catch { }
        BuildBgPanel();
        ApplyMode();
        ApplyBg(_bgIndex);
        ApplyTypography();
    }

    private void ApplyTypography()
    {
        FontFamily family;
        try { family = new FontFamily(_fontFamily); }
        catch { family = new FontFamily("Microsoft YaHei UI"); }

        ContentText.FontFamily = family;
        ScrollList.FontFamily = family;
        // 字号以前只改了行高，正文大小一直没跟着变；这里一起设上
        ContentText.FontSize = _fontSize;
        ScrollList.FontSize = _fontSize;
        FontSizeText.Text = _fontSize.ToString("0.#");
        var lineHeight = _fontSize * _lineHeight;
        TextBlock.SetLineHeight(ContentText, lineHeight);
        TextBlock.SetLineHeight(ScrollList, lineHeight);
    }

    private void BuildBgPanel()
    {
        try
        {
            BgPanel.Children.Clear();
            for (int i = 0; i < NovelReaderBgPresets.Presets.Length; i++)
            {
                var preset = NovelReaderBgPresets.Presets[i];
                var idx = i;
                var btn = new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(6),
                    Background = (Brush)new BrushConverter().ConvertFromString(preset.Bg)!,
                    BorderBrush = (Brush)FindResource("CardBorderBrush"), BorderThickness = new Thickness(1),
                    Margin = new Thickness(3,0,0,0), Cursor = Cursors.Hand, ToolTip = preset.Name
                };
                if (idx == _bgIndex) { btn.BorderBrush = (Brush)FindResource("PrimaryBrush"); btn.BorderThickness = new Thickness(2); }
                btn.MouseLeftButtonUp += (_, _) => { _bgIndex = idx; ApplyBg(idx); _settings?.SetBg(idx); };
                BgPanel.Children.Add(btn);
            }
        }
        catch { }
    }

    private void ApplyBg(int idx)
    {
        try
        {
            idx = Math.Clamp(idx, 0, NovelReaderBgPresets.Presets.Length - 1);
            var p = NovelReaderBgPresets.Presets[idx];
            var bg = (Brush)new BrushConverter().ConvertFromString(p.Bg)!;
            var fg = (Brush)new BrushConverter().ConvertFromString(p.Fg)!;
            ReaderBorder.Background = bg;
            ContentText.Foreground = fg;
            // 章节头跟着正文一起换色，否则"夜"主题下标题是黑的看不见
            ChapterTitleText.Foreground = fg;
            ChapterNoText.Foreground = WithOpacity(fg, 0.55);
            ChapterMetaText.Foreground = WithOpacity(fg, 0.5);
            ScrollList.Background = Brushes.Transparent;
            BuildBgPanel();
        }
        catch { }
    }

    private static Brush WithOpacity(Brush brush, double opacity)
    {
        if (brush is SolidColorBrush solid)
            return new SolidColorBrush(solid.Color) { Opacity = opacity };
        return brush;
    }

    private void ApplyMode()
    {
        PageModeBtn.Opacity = _isScrollMode ? 0.55 : 1.0;
        ScrollModeBtn.Opacity = _isScrollMode ? 1.0 : 0.55;
        PageModeBtn.FontWeight = _isScrollMode ? FontWeights.Normal : FontWeights.SemiBold;
        ScrollModeBtn.FontWeight = _isScrollMode ? FontWeights.SemiBold : FontWeights.Normal;
        if (_isScrollMode)
        {
            ReaderScroll.Visibility = Visibility.Collapsed;
            ScrollList.Visibility = Visibility.Visible;
            PageBar.Visibility = Visibility.Collapsed;
            ScrollBar.Visibility = Visibility.Visible;
            ProgressText.Text = "0%";
            UpdateProgressFill(0);
            if (_chunks.Count > 0) ScrollList.ItemsSource = _chunks;
            Dispatcher.BeginInvoke(new Action(() => { ScrollToTopVirtualized(); UpdateScrollProgress(); }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            ScrollList.Visibility = Visibility.Collapsed;
            ReaderScroll.Visibility = Visibility.Visible;
            PageBar.Visibility = Visibility.Visible;
            ScrollBar.Visibility = Visibility.Collapsed;
            RenderPage();
        }
    }

    private void ScrollToTopVirtualized()
    {
        try
        {
            if (ScrollList.Items.Count > 0) ScrollList.ScrollIntoView(ScrollList.Items[0]);
            var sv = FindScrollViewer(ScrollList);
            sv?.ScrollToTop();
        }
        catch { }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is ScrollViewer sv) return sv;
            var res = FindScrollViewer(child);
            if (res != null) return res;
        }
        return null;
    }

    // ------------------------------------------------------------ 载入

    public async void LoadFile(string path) => await LoadFileAsync(path, 0);

    public async Task LoadFileAsync(string path, int charOffset)
    {
        _path = path;
        _historyKey = path;
        DetachOnline();
        ChapterHeaderPanel.Visibility = Visibility.Collapsed;
        ChapterPosText.Text = "";
        TitleText.Text = System.IO.Path.GetFileNameWithoutExtension(path);
        ReloadSettings();

        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var result = await Task.Run(() =>
            {
                var text = DecodeBytes(bytes, _autoDetectEncoding);
                var chks = SplitChunks(text, ScrollChunkSize);
                return (text, chks);
            });
            ApplyContent(result.text, result.chks, charOffset);
        }
        catch (Exception ex)
        {
            ContentText.Text = $"读取失败: {ex.Message}";
            PagerText.Text = "0 / 0";
        }
    }

    private void ReloadSettings()
    {
        try
        {
            if (_settings == null) _settings = App.Services.GetService<NovelReaderSettingsService>();
            if (_settings != null)
            {
                _isScrollMode = _settings.Current.IsScrollMode;
                _bgIndex = _settings.Current.BgIndex;
                _fontSize = _settings.Current.FontSize;
                _fontFamily = _settings.Current.FontFamily;
                _lineHeight = _settings.Current.LineHeight;
                _paragraphIndentEm = _settings.Current.ParagraphIndentEm;
                _autoDetectEncoding = _settings.Current.AutoDetectEncoding;
            }
        }
        catch { }
    }

    /// <summary>直接载入一段正文（在线章节也走这里）。</summary>
    private void ApplyContent(string text, List<string> chunks, int charOffset)
    {
        _content = text;
        _chunks = chunks;
        ApplyTypography();
        _pageCount = Math.Max(1, (int)Math.Ceiling(_content.Length / (double)_charsPerPage));
        JumpTotalText.Text = $"/ {_pageCount}";

        var hist = _history?.Get(_historyKey);
        if (charOffset > 0)
            _page = Math.Clamp(charOffset / _charsPerPage + 1, 1, _pageCount);
        else if (hist != null && hist.CharsPerPage == _charsPerPage && hist.TotalChars == _content.Length)
            _page = Math.Clamp(hist.Page, 1, _pageCount);
        else
            _page = 1;

        // 字号由「设置 → 小说」统一管理，不再从历史里恢复：
        // 否则老记录里的旧字号会把新排版永久锁死。
        ApplyBg(_bgIndex);
        ApplyTypography();
        ApplyMode();
        if (!_isScrollMode) RenderPage();
        SaveHistory();
    }

    private static List<string> SplitChunks(string text, int size)
    {
        var list = new List<string>((text.Length / size) + 1);
        for (int i = 0; i < text.Length; i += size)
        {
            int len = Math.Min(size, text.Length - i);
            list.Add(text.Substring(i, len));
        }
        if (list.Count == 0) list.Add("");
        return list;
    }

    private static string DecodeBytes(byte[] bytes, bool autoDetect)
    {
        if (bytes.Length == 0) return "";
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (!autoDetect) return Encoding.UTF8.GetString(bytes);
        try
        {
            var utf8Strict = Encoding.GetEncoding("utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var s = utf8Strict.GetString(bytes);
            if (!s.Contains('\uFFFD')) return s;
        }
        catch { }
        try
        {
            var s2 = Encoding.UTF8.GetString(bytes);
            if (!s2.Contains('\uFFFD')) return s2;
        }
        catch { }
        try { return Encoding.GetEncoding(936).GetString(bytes); } catch { }
        try { return Encoding.GetEncoding("GBK").GetString(bytes); } catch { }
        try { return Encoding.GetEncoding("GB18030").GetString(bytes); } catch { }
        return Encoding.UTF8.GetString(bytes);
    }

    // ------------------------------------------------------------ 在线阅读

    /// <summary>是否处于在线阅读态（决定返回键回到书籍详情还是本地索引）。</summary>
    public bool IsOnline => _session is not null;

    /// <summary>进入在线阅读态：显示切章控件、建目录、载入指定章。</summary>
    public async Task AttachOnlineAsync(NovelOnlineSession session, int startIndex)
    {
        _session = session;
        _path = "";
        ChapterBar.Visibility = Visibility.Visible;
        ChapterNavPanel.Visibility = Visibility.Visible;
        BuildChapterItems();
        await ShowChapterAsync(startIndex);
    }

    private void DetachOnline()
    {
        _session = null;
        _onlineIndex = -1;
        _chapterItems.Clear();
        try
        {
            ChapterBar.Visibility = Visibility.Collapsed;
            ChapterDrawer.Visibility = Visibility.Collapsed;
            // 本地 txt 没有章节概念，切章 / 跳章整组收起来
            ChapterNavPanel.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private async Task ShowChapterAsync(int index)
    {
        if (_session is null || _switchingChapter) return;
        if (index < 0 || index >= _session.Chapters.Count) return;

        _switchingChapter = true;
        try
        {
            var text = await _session.LoadTextAsync(index);
            _onlineIndex = index;
            var chapter = _session.Chapters[index];

            TitleText.Text = $"{_session.Detail.Title} · 第 {index + 1} / {_session.Chapters.Count} 章";
            _historyKey = _session.HistoryKey(index);

            // 章节头单独排（稿子里是居中三行），正文只放正文
            ChapterNoText.Text = $"第 {index + 1} 章 · {_session.Detail.Title}";
            ChapterTitleText.Text = StripChapterNumber(chapter.Title, index + 1);
            ChapterMetaText.Text = BuildChapterMeta(text);
            ChapterHeaderPanel.Visibility = Visibility.Visible;
            // 底部：上一章 [9 / 128] 下一章 [跳至 __ 章] [跳转]
            ChapterPosText.Text = $"{index + 1} / {_session.Chapters.Count}";
            ChapterJumpBox.Text = (index + 1).ToString();
            PrevChapterButton.IsEnabled = index > 0;
            NextChapterButton.IsEnabled = index < _session.Chapters.Count - 1;

            ApplyContent(text, SplitChunks(text, ScrollChunkSize), 0);
            RefreshDrawerState();

            // 预取下一章：滚动模式下翻到章尾时不再等网络
            _ = _session.PrefetchAsync(index + 1);
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex, "这一章没取到：");
        }
        finally
        {
            _switchingChapter = false;
        }
    }

    private void BuildChapterItems()
    {
        _chapterItems.Clear();
        if (_session is null) return;
        var digits = _session.Chapters.Count >= 1000 ? 4 : 3;
        for (var i = 0; i < _session.Chapters.Count; i++)
        {
            var chapter = _session.Chapters[i];
            _chapterItems.Add(new ChapterNavItem
            {
                Index = i,
                IndexText = (i + 1).ToString().PadLeft(digits, '0'),
                Title = chapter.Title,
                IsCurrent = i == _onlineIndex,
                IsRead = IsChapterRead(chapter),
            });
        }
        DrawerList.ItemsSource = null;
        DrawerList.ItemsSource = _chapterItems;
        DrawerSubtitleText.Text = $"{_session.Detail.Title} · 共 {_chapterItems.Count} 章";
        DrawerFootText.Text = $"已缓存 {_session.CachedCount()} 章";
        UpdateDrawerCurrent();
    }

    private void UpdateDrawerCurrent()
    {
        if (_session is null || _onlineIndex < 0 || _onlineIndex >= _session.Chapters.Count)
        {
            DrawerCurrentText.Text = "";
            return;
        }
        DrawerCurrentText.Text = $"当前第 {_onlineIndex + 1} 章：{StripChapterNumber(_session.Chapters[_onlineIndex].Title, _onlineIndex + 1)}";
    }

    /// <summary>"第86章 灯下故人" → "灯下故人"；标题里没有章号就原样返回。</summary>
    private static string StripChapterNumber(string title, int index)
    {
        if (string.IsNullOrWhiteSpace(title)) return $"第 {index} 章";
        var trimmed = title.Trim();
        var m = System.Text.RegularExpressions.Regex.Match(trimmed,
            @"^第\s*[0-9零一二三四五六七八九十百千万两]+\s*[章节回卷话][\s·:：、.-]*");
        return m.Success && m.Length < trimmed.Length ? trimmed[m.Length..].Trim() : trimmed;
    }

    /// <summary>章节头下面那行：作者 · 约 N 分钟 · 来源。</summary>
    private string BuildChapterMeta(string text)
    {
        var parts = new List<string>();
        if (_session is not null && !string.IsNullOrWhiteSpace(_session.Detail.Author))
            parts.Add(_session.Detail.Author);
        var minutes = Math.Max(1, (int)Math.Round(text.Length / 400.0));
        parts.Add($"约 {minutes} 分钟");
        if (_session is not null) parts.Add(_session.Source.Info.DisplayName);
        return string.Join(" · ", parts);
    }

    private bool IsChapterRead(ResourceGrab.Core.Sources.Novel.NovelChapter chapter)
    {
        try
        {
            if (_session is null) return false;
            return _history?.Get($"online:{_session.Source.Info.Id}:{_session.Detail.Id}:{chapter.Id}") is not null;
        }
        catch
        {
            return false;
        }
    }

    private void RefreshDrawerState()
    {
        if (_session is null) return;
        for (var i = 0; i < _chapterItems.Count; i++)
        {
            _chapterItems[i].IsCurrent = i == _onlineIndex;
            if (i == _onlineIndex) _chapterItems[i].IsRead = true;
        }
        DrawerList.Items.Refresh();
        DrawerFootText.Text = $"已缓存 {_session.CachedCount()} 章";
        UpdateDrawerCurrent();

        var item = _chapterItems.FirstOrDefault(x => x.IsCurrent);
        if (item != null) DrawerList.ScrollIntoView(item);
    }

    private async void PrevChapter_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _onlineIndex <= 0) return;
        await ShowChapterAsync(_onlineIndex - 1);
    }

    private async void NextChapter_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _onlineIndex >= _session.Chapters.Count - 1) return;
        await ShowChapterAsync(_onlineIndex + 1);
    }

    private void ChapterList_Click(object sender, RoutedEventArgs e)
    {
        var show = ChapterDrawer.Visibility != Visibility.Visible;
        ChapterDrawer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DrawerClose_Click(object sender, RoutedEventArgs e) => ChapterDrawer.Visibility = Visibility.Collapsed;

    private void DrawerScrim_Click(object sender, MouseButtonEventArgs e) => ChapterDrawer.Visibility = Visibility.Collapsed;

    private async void DrawerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DrawerList.SelectedItem is not ChapterNavItem item) return;
        DrawerList.SelectedItem = null;
        ChapterDrawer.Visibility = Visibility.Collapsed;
        if (item.Index != _onlineIndex) await ShowChapterAsync(item.Index);
    }

    // ------------------------------------------------------------ 分页

    private void RenderPage()
    {
        if (_isScrollMode) return;
        var start = (_page - 1) * _charsPerPage;
        var len = Math.Min(_charsPerPage, Math.Max(0, _content.Length - start));
        ContentText.Text = len > 0 ? _content.Substring(start, len) : "";
        PagerText.Text = $"{_page} / {_pageCount}";
        JumpTotalText.Text = $"/ {_pageCount}";
        JumpBox.Text = _page.ToString();
        var percent = (int)Math.Round(_page * 100.0 / _pageCount);
        PageText.Text = $"翻页模式 · 第 {_page} / {_pageCount} 页";
        ProgressText.Text = $"{percent}%";
        UpdateProgressFill(percent);
        // 章节头只在第一页出现，翻到后面不再"钉"在顶上
        if (_session is not null)
            ChapterHeaderPanel.Visibility = _page == 1 ? Visibility.Visible : Visibility.Collapsed;
        PrevBtn.IsEnabled = _page > 1;
        NextBtn.IsEnabled = _page < _pageCount;
        ReaderScroll.ScrollToTop();
    }

    private void UpdateProgressFill(int percent)
    {
        // 布局还没跑完时 ActualWidth 是 0，等布局完成再补一次，
        // 否则进度条会一直停在 0 宽度（看起来就是"没有进度条"）。
        var width = ProgressTrack.ActualWidth;
        if (width <= 0)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (ProgressTrack.ActualWidth > 0) UpdateProgressFill(percent);
            }));
            return;
        }
        if (percent <= 0) { ProgressFill.Width = 0; return; }
        ProgressFill.Width = Math.Max(3, Math.Min(width, width * percent / 100.0));
    }

    /// <summary>滚动模式也用同一条进度条：按滚动位置算百分比。</summary>
    private void UpdateScrollProgress()
    {
        try
        {
            var sv = FindScrollViewer(ScrollList);
            if (sv is null) return;
            var percent = sv.ScrollableHeight <= 1
                ? 100
                : (int)Math.Round(Math.Clamp(sv.VerticalOffset / sv.ScrollableHeight, 0, 1) * 100);
            ProgressText.Text = $"{percent}%";
            UpdateProgressFill(percent);
        }
        catch { }
    }

    private void ScrollList_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateScrollProgress();

    private void ProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isScrollMode) { UpdateScrollProgress(); return; }
        var percent = _pageCount > 0 ? (int)Math.Round(_page * 100.0 / _pageCount) : 0;
        UpdateProgressFill(percent);
    }

    // ------------------------------------------------------------ 章节跳转

    private void ChapterJumpButton_Click(object sender, RoutedEventArgs e) => DoChapterJump();

    private void ChapterJumpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { DoChapterJump(); e.Handled = true; }
    }

    private async void DoChapterJump()
    {
        if (_session is null) return;
        if (int.TryParse(ChapterJumpBox.Text.Trim(), out var n))
        {
            n = Math.Clamp(n, 1, _session.Chapters.Count);
            ChapterJumpBox.Text = n.ToString();
            if (n - 1 != _onlineIndex) await ShowChapterAsync(n - 1);
            return;
        }
        ChapterJumpBox.Text = (_onlineIndex + 1).ToString();
    }

    private void SaveHistory()
    {
        try
        {
            if (string.IsNullOrEmpty(_historyKey)) return;
            _history?.Save(_historyKey, _page, _pageCount, _charsPerPage, _fontSize, _content.Length, TitleText.Text);
        }
        catch { }
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_isScrollMode) { var sv = FindScrollViewer(ScrollList); if (sv != null) sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset - 400)); return; }
        if (_page > 1) { _page--; RenderPage(); SaveHistory(); }
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_isScrollMode) { var sv = FindScrollViewer(ScrollList); if (sv != null) sv.ScrollToVerticalOffset(Math.Min(sv.ScrollableHeight, sv.VerticalOffset + 400)); return; }
        if (_page < _pageCount) { _page++; RenderPage(); SaveHistory(); }
    }

    private void ReaderScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_isScrollMode && _pageCount > 1)
        {
            if (e.Delta > 0) { if (_page > 1) { _page--; RenderPage(); SaveHistory(); } }
            else { if (_page < _pageCount) { _page++; RenderPage(); SaveHistory(); } }
            e.Handled = true;
        }
    }

    private void JumpButton_Click(object sender, RoutedEventArgs e) => DoJump();
    private void JumpBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { DoJump(); e.Handled = true; } }

    private void DoJump()
    {
        if (_isScrollMode) return;
        if (int.TryParse(JumpBox.Text.Trim(), out var p))
        {
            p = Math.Clamp(p, 1, _pageCount);
            _page = p;
            RenderPage();
            SaveHistory();
        }
        else
        {
            JumpBox.Text = _page.ToString();
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => Navigation.BackHandler?.Invoke();

    private void FontDec_Click(object sender, RoutedEventArgs e)
    {
        _fontSize = Math.Max(10, _fontSize - 1);
        ApplyTypography();
        _settings?.SetFontSize(_fontSize);
        SaveHistory();
    }

    private void FontInc_Click(object sender, RoutedEventArgs e)
    {
        _fontSize = Math.Min(24, _fontSize + 1);
        ApplyTypography();
        _settings?.SetFontSize(_fontSize);
        SaveHistory();
    }

    private void ModePage_Click(object sender, RoutedEventArgs e)
    {
        _isScrollMode = false;
        _settings?.SetScrollMode(false);
        ApplyMode();
    }

    private void ModeScroll_Click(object sender, RoutedEventArgs e)
    {
        _isScrollMode = true;
        _settings?.SetScrollMode(true);
        PageText.Text = "滚动模式";
        ProgressText.Text = "";
        ProgressFill.Width = 0;
        JumpTotalText.Text = $"/ {_pageCount}";
        ApplyMode();
    }
}

/// <summary>目录抽屉里的一行。</summary>
public sealed class ChapterNavItem
{
    public int Index { get; init; }
    public string IndexText { get; init; } = "";
    public string Title { get; init; } = "";
    public bool IsCurrent { get; set; }
    public bool IsRead { get; set; }
}
