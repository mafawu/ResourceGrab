using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceGrab.App.Common;
using ResourceGrab.App.Services;
using ResourceGrab.Core;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources.Novel;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Views;

/// <summary>书籍详情页：站点书目 + 本地优先 + 章节目录 + 导出 TXT。</summary>
public partial class NovelBookView : UserControl
{
    private readonly INovelSource _source;
    private readonly NovelSummary _summary;
    private readonly NovelIndexService _index;
    private readonly NovelLocalMatcher _matcher;
    private readonly NovelChapterCache _cache;
    private readonly NovelReadingHistoryService _history;
    private readonly NovelReaderSettingsService _settings;
    private readonly NovelExportService _exporter;
    private readonly NovelShelfService _shelf;

    private NovelDetail? _detail;
    private NovelLocalMatch? _local;
    private IReadOnlyList<NovelTextSpan> _localSpans = Array.Empty<NovelTextSpan>();
    private bool _useOnline;
    private bool _sortDesc;
    private bool _introExpanded;
    private bool _exporting;
    private readonly string? _startChapterId;
    private bool _autoOpenDone;
    private bool _loaded;
    private readonly List<NovelChapterRow> _rows = new();

    public NovelBookView(INovelSource source, NovelSummary summary, string? startChapterId = null)
    {
        InitializeComponent();
        _source = source;
        _summary = summary;
        _startChapterId = startChapterId;

        _index = App.Services.GetRequiredService<NovelIndexService>();
        _matcher = new NovelLocalMatcher(_index);
        _cache = App.Services.GetRequiredService<NovelChapterCache>();
        _history = App.Services.GetRequiredService<NovelReadingHistoryService>();
        _settings = App.Services.GetRequiredService<NovelReaderSettingsService>();
        _exporter = App.Services.GetRequiredService<NovelExportService>();
        _shelf = App.Services.GetRequiredService<NovelShelfService>();

        TitleText.Text = summary.Title;
        CoverFallback.Text = string.IsNullOrEmpty(summary.Title) ? "N" : summary.Title[..1];
        FactsText.Text = BuildFacts(summary);
        // 只加载一次：从别的页签切回来时保持详情页原样（目录滚动、简介展开状态都不重置）
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            _ = LoadAsync();
        };
    }

    private void Tag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || string.IsNullOrWhiteSpace(tag)) return;
        Navigation.OpenNovelTag(tag.Trim());
    }

    private async Task LoadAsync()
    {
        try
        {
            _detail = await _source.GetDetailAsync(_summary.Id);
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex, "打开书籍失败：");
            FactsText.Text = "详情加载失败，仍可重试";
            return;
        }

        var detail = _detail;
        TitleText.Text = string.IsNullOrWhiteSpace(detail.Title) ? _summary.Title : detail.Title;
        FactsText.Text = BuildFacts(detail);
        TagsHost.ItemsSource = detail.Tags;
        IntroText.Text = string.IsNullOrWhiteSpace(detail.Intro) ? "暂无简介" : detail.Intro;
        IntroText.MaxHeight = CollapsedIntroHeight;
        _introExpanded = false;
        IntroToggle.Content = "展开";
        _ = Dispatcher.BeginInvoke(new Action(UpdateIntroToggle), System.Windows.Threading.DispatcherPriority.Loaded);
        ActionHint.Text = "";

        if (!string.IsNullOrWhiteSpace(detail.CoverUrl))
        {
            CoverFallback.Visibility = Visibility.Collapsed;
            try
            {
                ImageLoader.SetHeaders(CoverImage, _source.Info.CoverHeaders);
                ImageLoader.SetSource(CoverImage, detail.CoverUrl);
            }
            catch { }
        }

        await MatchLocalAsync(detail);
        RebuildRows();
        UpdateReadButton();
        UpdateShelfButton();
        TryAutoOpenChapter();
    }

    /// <summary>从阅读历史进来时，直接续到上次那章。</summary>
    private void TryAutoOpenChapter()
    {
        if (_autoOpenDone || _detail is null || string.IsNullOrWhiteSpace(_startChapterId)) return;
        _autoOpenDone = true;

        var want = _startChapterId.Replace('\\', '/');
        var chapter = _detail.Chapters.FirstOrDefault(c =>
            string.Equals(c.Id, want, StringComparison.OrdinalIgnoreCase));
        if (chapter is null) return;
        Navigation.OpenNovelOnlineReader(_source, _detail, chapter.Index);
    }

    private void UpdateShelfButton()
    {
        var inShelf = _shelf.Contains(_source.Info.Id, _summary.Id);
        ShelfButton.Content = inShelf ? "移出书架" : "加入书架";
    }

    private void ShelfButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_shelf.Contains(_source.Info.Id, _summary.Id))
            {
                _shelf.Remove(_source.Info.Id, _summary.Id);
                ToastService.Show("已从书架移除", ToastKind.Info);
            }
            else
            {
                var detail = _detail;
                _shelf.Add(new NovelShelfItem
                {
                    SourceId = _source.Info.Id,
                    NovelId = _summary.Id,
                    Title = detail?.Title is { Length: > 0 } t ? t : _summary.Title,
                    Author = detail?.Author is { Length: > 0 } a ? a : _summary.Author,
                    CoverUrl = detail?.CoverUrl is { Length: > 0 } c ? c : _summary.CoverUrl,
                    WordCountText = detail?.WordCountText ?? _summary.WordCountText,
                    Status = detail?.Status ?? _summary.Status,
                    ChapterCount = detail?.Chapters.Count ?? 0,
                    AddedAt = DateTime.Now,
                });
                ToastService.Show("已加入书架", ToastKind.Success);
            }
            UpdateShelfButton();
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex);
        }
    }

    // ------------------------------------------------------------ 本地优先

    private async Task MatchLocalAsync(NovelDetail detail)
    {
        try
        {
            _local = _matcher.Find(detail.Title);
            if (_local is not { Kind: LocalMatchKind.Exact } match) return;

            var path = match.Resource.FullPath;
            if (!File.Exists(path)) return;

            var pattern = _settings.Current.ChapterPattern;
            var result = await Task.Run(async () => await NovelTextIndexer.ReadAndSplitAsync(path, pattern));
            _localSpans = result.Spans;

            LocalBar.Visibility = Visibility.Visible;
            LocalPath.Text = path;
            LocalPath.ToolTip = path;
            LocalTitle.Text = _localSpans.Count > 0 ? $"本地已有 · {_localSpans.Count} 章" : "本地已有";
            UseOnlineBtn.Content = "改用在线数据";
        }
        catch
        {
            _local = null;
            _localSpans = Array.Empty<NovelTextSpan>();
        }
    }

    private void UseOnline_Click(object sender, RoutedEventArgs e)
    {
        _useOnline = !_useOnline;
        UseOnlineBtn.Content = _useOnline ? "改用本地数据" : "改用在线数据";
        RebuildRows();
        UpdateReadButton();
    }

    private void OpenLocalFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _local?.Resource.FullPath;
            if (string.IsNullOrEmpty(path)) return;
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex);
        }
    }

    // ------------------------------------------------------------ 目录

    private void RebuildRows()
    {
        if (_detail is null) return;

        var useLocal = _local is { Kind: LocalMatchKind.Exact } && _localSpans.Count > 0 && !_useOnline;
        var rows = new List<NovelChapterRow>();

        if (useLocal)
        {
            for (var i = 0; i < _localSpans.Count; i++)
            {
                var span = _localSpans[i];
                rows.Add(new NovelChapterRow
                {
                    IndexText = IndexLabel(i, span.Title),
                    Title = span.Title,
                    TimeText = "",
                    IsRead = false,
                    Span = span,
                    Index = i,
                });
            }
        }
        else
        {
            foreach (var chapter in _detail.Chapters)
            {
                rows.Add(new NovelChapterRow
                {
                    IndexText = IndexLabel(chapter.Index, chapter.Title),
                    Title = chapter.Title,
                    TimeText = chapter.UpdatedText,
                    IsRead = IsChapterRead(chapter),
                    Chapter = chapter,
                    Index = chapter.Index,
                });
            }
        }

        if (_sortDesc) rows.Reverse();
        _rows.Clear();
        _rows.AddRange(rows);
        ChapterList.ItemsSource = null;
        ChapterList.ItemsSource = _rows;

        var readCount = _rows.Count(r => r.IsRead);
        var suffix = useLocal ? $" · 本地 {_localSpans.Count} 章" : "";
        ChapterCountText.Text = $"共 {_rows.Count} 章 · 已读 {readCount}{suffix}";
        SortButton.Content = _sortDesc ? "正序" : "倒序";
    }

    /// <summary>章节标题里已经带「第 N 章」时，不再重复一列序号。</summary>
    private static string IndexLabel(int index, string title)
        => System.Text.RegularExpressions.Regex.IsMatch(
            title, @"^\s*第\s*[0-9零一二三四五六七八九十百千万两]+\s*[章节卷回部]")
            ? ""
            : $"第 {index + 1} 章";

    private bool IsChapterRead(NovelChapter chapter)
    {
        try
        {
            return _history.Get(HistoryKey(chapter)) is not null;
        }
        catch
        {
            return false;
        }
    }

    private string HistoryKey(NovelChapter chapter) =>
        $"online:{_source.Info.Id}:{_detail?.Id}:{chapter.Id}";

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        _sortDesc = !_sortDesc;
        RebuildRows();
    }

    private void JumpLatest_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        var target = _sortDesc ? _rows[0] : _rows[^1];
        ChapterList.SelectedItem = target;
        ChapterList.ScrollIntoView(target);
        ChapterList.SelectedItem = null;
    }

    private void ChapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChapterList.SelectedItem is not NovelChapterRow row) return;
        ChapterList.SelectedItem = null;
        OpenRow(row);
    }

    private void OpenRow(NovelChapterRow row)
    {
        try
        {
            if (row.Span is { } span && _local is not null)
            {
                Navigation.OpenNovelReaderAt(_local.Resource.FullPath, span.Offset);
                return;
            }

            if (row.Chapter is not null && _detail is not null)
                Navigation.OpenNovelOnlineReader(_source, _detail, row.Chapter.Index);
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex, "打开章节失败：");
        }
    }

    // ------------------------------------------------------------ 主操作

    private void ReadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_local is { Kind: LocalMatchKind.Exact } && !_useOnline)
            {
                if (_localSpans.Count > 0)
                {
                    var readCount = _localSpans.Count;
                    var index = FindLastLocalIndex();
                    Navigation.OpenNovelReaderAt(_local.Resource.FullPath, _localSpans[Math.Clamp(index, 0, readCount - 1)].Offset);
                }
                else
                {
                    Navigation.OpenNovelReader(_local.Resource.FullPath);
                }
                return;
            }

            if (_detail is null || _detail.Chapters.Count == 0) return;
            var start = NextUnreadChapterIndex();
            Navigation.OpenNovelOnlineReader(_source, _detail, start);
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex, "打开阅读器失败：");
        }
    }

    private int FindLastLocalIndex()
    {
        // 本地章节没有逐章进度，按整本进度近似：取历史里记录的页码对应的章节
        try
        {
            var hist = _history.Get(_local!.Resource.FullPath);
            if (hist is null || _localSpans.Count == 0) return 0;
            var charIndex = Math.Max(0, (hist.Page - 1) * hist.CharsPerPage);
            var idx = 0;
            for (var i = 0; i < _localSpans.Count; i++)
            {
                if (_localSpans[i].Offset <= charIndex) idx = i;
                else break;
            }
            return idx;
        }
        catch
        {
            return 0;
        }
    }

    private int NextUnreadChapterIndex()
    {
        if (_detail is null) return 0;
        foreach (var chapter in _detail.Chapters)
        {
            if (!IsChapterRead(chapter)) return chapter.Index;
        }
        return 0;
    }

    private void UpdateReadButton()
    {
        var local = _local is { Kind: LocalMatchKind.Exact } && !_useOnline;
        if (local)
        {
            ReadButton.Content = "继续阅读 · 本地";
            ActionHint.Text = _localSpans.Count > 0 ? "章节取自本地文件" : "本地未识别出章节，整本阅读";
            return;
        }

        var start = NextUnreadChapterIndex();
        ReadButton.Content = start > 0 ? $"继续阅读 · 第 {start + 1} 章" : "开始阅读 · 第 1 章";
        ActionHint.Text = "";
    }

    private void IntroToggle_Click(object sender, RoutedEventArgs e)
    {
        _introExpanded = !_introExpanded;
        IntroText.MaxHeight = _introExpanded ? double.PositiveInfinity : CollapsedIntroHeight;
        IntroToggle.Content = _introExpanded ? "收起" : "展开";
    }

    private const double CollapsedIntroHeight = 58;

    /// <summary>实测简介是否被截断：只有真截断了才显示「展开」，短简介不摆一个没用的按钮。</summary>
    private void UpdateIntroToggle()
    {
        try
        {
            IntroText.MaxHeight = double.PositiveInfinity;
            IntroText.UpdateLayout();
            var desired = IntroText.ActualHeight;
            IntroText.MaxHeight = CollapsedIntroHeight;
            IntroToggle.Visibility = desired > CollapsedIntroHeight + 4 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            IntroToggle.Visibility = Visibility.Collapsed;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => Navigation.Back();

    // ------------------------------------------------------------ 导出

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting || _detail is null) return;

        _exporting = true;
        ExportButton.IsEnabled = false;
        StatusBar.Visibility = Visibility.Visible;
        StatusText.Text = "准备导出…";

        try
        {
            var dir = _exporter.DefaultDirectory;
            _exporter.Progress += OnExportProgress;
            var path = await Task.Run(() => _exporter.ExportAsync(_source, _detail!, dir));
            StatusText.Text = $"已导出：{path}";
            ToastService.Show("导出完成", ToastKind.Success);
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
            ToastService.ShowError(ex, "导出失败：");
        }
        finally
        {
            _exporter.Progress -= OnExportProgress;
            _exporting = false;
            ExportButton.IsEnabled = true;
        }
    }

    private void OnExportProgress(int done, int total, string title)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            StatusText.Text = total > 0
                ? $"导出中 {done} / {total}" + (string.IsNullOrEmpty(title) ? "" : $" · {title}")
                : "导出中…";
        }));
    }

    // ------------------------------------------------------------ 文案

    private static string BuildFacts(NovelSummary summary)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary.Author)) parts.Add(summary.Author);
        if (!string.IsNullOrWhiteSpace(summary.Category)) parts.Add(summary.Category);
        if (!string.IsNullOrWhiteSpace(summary.Status)) parts.Add(summary.Status);
        if (!string.IsNullOrWhiteSpace(summary.ChapterCountText)) parts.Add(summary.ChapterCountText);
        if (!string.IsNullOrWhiteSpace(summary.WordCountText)) parts.Add(summary.WordCountText);
        if (!string.IsNullOrWhiteSpace(summary.UpdatedText)) parts.Add("更新于 " + summary.UpdatedText);
        return string.Join(" · ", parts);
    }

    private static string BuildFacts(NovelDetail detail)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(detail.Author)) parts.Add(detail.Author);
        if (!string.IsNullOrWhiteSpace(detail.Category)) parts.Add(detail.Category);
        if (!string.IsNullOrWhiteSpace(detail.Status)) parts.Add(detail.Status);
        parts.Add($"{detail.ChapterCount} 章");
        if (!string.IsNullOrWhiteSpace(detail.WordCountText)) parts.Add(detail.WordCountText);
        if (!string.IsNullOrWhiteSpace(detail.LatestChapterTitle)) parts.Add("最新 " + detail.LatestChapterTitle);
        return string.Join(" · ", parts);
    }
}

/// <summary>目录行：本地切分与在线章节共用。</summary>
public sealed class NovelChapterRow
{
    public string IndexText { get; init; } = "";
    public string Title { get; init; } = "";
    public string TimeText { get; init; } = "";
    public bool IsRead { get; init; }
    public int Index { get; init; }
    public NovelChapter? Chapter { get; init; }
    public NovelTextSpan? Span { get; init; }
}
