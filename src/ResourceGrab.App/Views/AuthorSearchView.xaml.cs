using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Common;
using ResourceGrab.App.Services;
using ResourceGrab.Core.Downloading;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Services;

namespace ResourceGrab.App.Views;

/// <summary>作者合集结果行：勾选态默认 = 是否中文版。</summary>
public sealed class AuthorWorkRow : ObservableObject
{
    private bool _isSelected;

    public required string SourceId { get; init; }
    public required string SourceName { get; init; }
    public required string ComicId { get; init; }
    public required string Title { get; init; }
    public required string Author { get; init; }
    public bool HasChinese { get; init; }
    public bool IsDownloaded { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }
}

/// <summary>
/// 作者合集页：搜作者名 → 禁漫天堂 + 绅士漫画合并去重 → 默认勾中文 → 下载选中。
/// 侧栏 manga.author 路由。
/// </summary>
public partial class AuthorSearchView : UserControl
{
    private readonly SourceManager _sourceManager;
    private readonly ConfigService _config;
    private readonly DownloadManager _downloadManager;
    private readonly LocalLibraryService _localLibrary;
    private readonly ILogger _logger;
    private readonly ObservableCollection<AuthorWorkRow> _rows = new();
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _downloadCts;
    private bool _busy;

    public AuthorSearchView()
    {
        InitializeComponent();
        _sourceManager = App.Services.GetRequiredService<SourceManager>();
        _config = App.Services.GetRequiredService<ConfigService>();
        _downloadManager = App.Services.GetRequiredService<DownloadManager>();
        _localLibrary = App.Services.GetRequiredService<LocalLibraryService>();
        _logger = App.Services.GetRequiredService<ILogger>();
        ResultList.ItemsSource = _rows;
        UpdateCount();
    }

    private void AuthorBox_SearchSubmitted(object sender, string text) => _ = SearchAsync();

    private void SearchButton_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _searchCts?.Cancel();

    private async Task SearchAsync()
    {
        var author = AuthorBox.Text.Trim();
        if (string.IsNullOrEmpty(author) || _busy) return;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        SetBusy(true);
        _rows.Clear();
        UpdateCount();
        try
        {
            // 去重优先级禁漫 > 绅士：顺序即优先级
            var sources = _sourceManager.Sources
                .Where(s => s.Info.Id is "jm" or "wnacg")
                .OrderBy(s => s.Info.Id == "jm" ? 0 : 1)
                .ToList();
            if (sources.Count == 0)
            {
                StatusText.Text = "禁漫天堂与绅士漫画均未注册";
                return;
            }
            var service = new AuthorWorksService(sources);
            var result = await Task.Run(() => service.SearchAuthorAsync(
                author, msg => Dispatcher.BeginInvoke(() => StatusText.Text = msg), ct), ct);
            HashSet<string> downloaded;
            try
            {
                downloaded = await Task.Run(
                    () => _localLibrary.GetDownloadedKeys(_config.Current.DownloadDir), ct);
            }
            catch
            {
                downloaded = [];
            }
            foreach (var item in result.Items)
            {
                _rows.Add(new AuthorWorkRow
                {
                    SourceId = item.SourceId,
                    SourceName = item.SourceName,
                    ComicId = item.ComicId,
                    Title = item.Title,
                    Author = item.Author,
                    HasChinese = item.HasChinese,
                    IsDownloaded = downloaded.Contains(
                        LocalLibraryService.KeyFor(item.SourceId, item.ComicId)),
                    IsSelected = item.HasChinese,
                });
            }
            var summary = $"“{author}”：去重后 {result.Items.Count} 本" +
                (result.DroppedDuplicates > 0 ? $"（跨源同名去重 {result.DroppedDuplicates} 本）" : "") +
                $"，默认勾选 {_rows.Count(r => r.IsSelected)} 本中文版";
            if (result.Errors.Count > 0) summary += $"；{string.Join("；", result.Errors)}";
            StatusText.Text = summary;
            UpdateCount();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消搜索";
        }
        catch (Exception ex)
        {
            _logger.Error("[AuthorSearch] 搜索失败", ex);
            StatusText.Text = $"搜索失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SearchButton.IsEnabled = !busy;
        DownloadButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) StatusText.Text = "搜索中…";
    }

    private void UpdateCount()
    {
        var selected = _rows.Count(r => r.IsSelected);
        CountText.Text = _rows.Count == 0 ? "" : $"共 {_rows.Count} 本 · 已选 {selected} 本";
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsSelected = true;
        UpdateCount();
    }

    private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsSelected = false;
        UpdateCount();
    }

    private void SelectChineseButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsSelected = r.HasChinese;
        UpdateCount();
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var targets = _rows.Where(r => r.IsSelected).ToList();
        if (targets.Count == 0)
        {
            ToastService.Show("请先勾选要下载的漫画", ToastKind.Info);
            return;
        }
        if (_busy) return;
        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;
        SetBusy(true);
        var ok = 0;
        var skipped = 0;
        var failed = new List<string>();
        try
        {
            // 已下载的自动跳过（以当前勾选快照为准）
            HashSet<string> downloaded;
            try
            {
                downloaded = await Task.Run(
                    () => _localLibrary.GetDownloadedKeys(_config.Current.DownloadDir), ct);
            }
            catch
            {
                downloaded = [];
            }
            var i = 0;
            foreach (var row in targets)
            {
                ct.ThrowIfCancellationRequested();
                i++;
                DownloadStatusText.Text = $"正在加入队列 {i}/{targets.Count}：{row.Title}";
                if (downloaded.Contains(LocalLibraryService.KeyFor(row.SourceId, row.ComicId)))
                {
                    skipped++;
                    continue;
                }
                try
                {
                    var source = _sourceManager.Get(row.SourceId);
                    var (count, _) = await DownloadHelper.EnqueueAllAsync(
                        source, _config, _downloadManager, row.ComicId, ct);
                    if (count > 0) ok++;
                    else failed.Add($"{row.Title}（无章节）");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Warn($"[AuthorSearch] 入队失败 {row.Title}: {ex.Message}");
                    failed.Add(row.Title);
                }
            }
            Navigation.NotifyDownloadedKeysChanged();
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText.Text = "已取消";
        }
        finally
        {
            SetBusy(false);
        }
        DownloadStatusText.Text = "";
        ToastService.Show(
            $"作者合集下载：成功 {ok} 本，跳过已下载 {skipped} 本" +
            (failed.Count > 0 ? $"，失败 {failed.Count} 本（{string.Join("、", failed.Take(3))}）" : ""),
            failed.Count > 0 ? ToastKind.Error : ToastKind.Success);
    }
}
