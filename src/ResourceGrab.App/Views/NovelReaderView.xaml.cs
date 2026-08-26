using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ResourceGrab.App.Common;
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
    private double _fontSize = 14;
    private bool _isScrollMode = false;
    private int _bgIndex = 0;
    private string _fontFamily = "Microsoft YaHei UI";
    private double _lineHeight = 1.6;
    private double _paragraphIndentEm = 2;
    private bool _autoDetectEncoding = true;
    private NovelReadingHistoryService? _history;
    private NovelReaderSettingsService? _settings;

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
        var lineHeight = _fontSize * _lineHeight;
        var indent = _fontSize * _paragraphIndentEm;
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
            ScrollList.Background = Brushes.Transparent;
            // ListBox items inherit via binding to ContentText, also update ScrollList foreground for scroll bar
            BuildBgPanel();
        }
        catch { }
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
            if (_chunks.Count > 0) ScrollList.ItemsSource = _chunks;
            // 延迟滚动到顶部，避免虚拟化未就绪
            Dispatcher.BeginInvoke(new Action(() => ScrollToTopVirtualized()), System.Windows.Threading.DispatcherPriority.Loaded);
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

    public async void LoadFile(string path)
    {
        _path = path;
        TitleText.Text = System.IO.Path.GetFileNameWithoutExtension(path);
        try { _history = App.Services.GetService<NovelReadingHistoryService>(); } catch { }
        try { _settings = App.Services.GetService<NovelReaderSettingsService>(); } catch { }
        try
        {
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

        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            // 解码与分块放后台，避免 UI 卡顿
            var result = await Task.Run(() =>
            {
                var text = DecodeBytes(bytes, _autoDetectEncoding);
                var chks = SplitChunks(text, ScrollChunkSize);
                return (text, chks);
            });
            _content = result.text;
            _chunks = result.chks;
            ApplyTypography();
            _pageCount = Math.Max(1, (int)Math.Ceiling(_content.Length / (double)_charsPerPage));
            JumpTotalText.Text = $"/ {_pageCount}";

            var hist = _history?.Get(path);
            if (hist != null && hist.CharsPerPage == _charsPerPage && hist.TotalChars == _content.Length)
                _page = Math.Clamp(hist.Page, 1, _pageCount);
            else
                _page = 1;

            if (hist != null && hist.FontSize >= 10 && hist.FontSize <= 24)
            {
                _fontSize = hist.FontSize;
                ApplyTypography();
                _settings?.SetFontSize(_fontSize);
            }

            ApplyBg(_bgIndex);
            ApplyTypography();
            ApplyMode();
            if (!_isScrollMode) RenderPage();
            SaveHistory();
        }
        catch (Exception ex)
        {
            ContentText.Text = $"读取失败: {ex.Message}";
            PagerText.Text = "0 / 0";
        }
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

    private void RenderPage()
    {
        if (_isScrollMode) return;
        var start = (_page - 1) * _charsPerPage;
        var len = Math.Min(_charsPerPage, Math.Max(0, _content.Length - start));
        ContentText.Text = len > 0 ? _content.Substring(start, len) : "";
        PagerText.Text = $"{_page} / {_pageCount}";
        JumpTotalText.Text = $"/ {_pageCount}";
        JumpBox.Text = _page.ToString();
        PageText.Text = $"{_content.Length} 字";
        ProgressText.Text = $"{(int)(_page * 100.0 / _pageCount)}%";
        PrevBtn.IsEnabled = _page > 1;
        NextBtn.IsEnabled = _page < _pageCount;
        ReaderScroll.ScrollToTop();
    }

    private void SaveHistory()
    {
        try { _history?.Save(_path, _page, _pageCount, _charsPerPage, _fontSize, _content.Length, TitleText.Text); } catch { }
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
        PageText.Text = $"{_content.Length} 字";
        ProgressText.Text = "滚动模式";
        JumpTotalText.Text = $"/ {_pageCount}";
        ApplyMode();
    }
}
