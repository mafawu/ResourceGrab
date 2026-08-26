using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using ResourceGrab.App.Common;
using ResourceGrab.App.Services;
using ResourceGrab.Core.Downloading;
using ResourceGrab.Core.Services;

namespace ResourceGrab.App.ViewModels;

/// <summary>
/// 下载面板视图模型：订阅 DownloadManager 事件，
/// 汇总章节进度、全局进度与实时速度；历史记录持久化到 download-history.json。
/// </summary>
public class DownloadPanelViewModel : ObservableObject, IDisposable
{
    private readonly DownloadManager _manager;
    private readonly Dispatcher _dispatcher;
    private readonly DownloadHistoryService _history;
    private readonly Dictionary<long, DownloadItemViewModel> _itemsById = new();
    private const int MaxVisibleHistoryCount = 80;

    public DownloadPanelViewModel(DownloadManager manager)
    {
        _manager = manager;
        _dispatcher = Application.Current.Dispatcher;
        _history = new DownloadHistoryService();

        _manager.ChapterPending += OnChapterPending;
        _manager.ChapterStart += OnChapterStart;
        _manager.ImageSuccess += OnImageSuccess;
        _manager.ImageError += OnImageError;
        _manager.ChapterEnd += OnChapterEnd;
        _manager.OverallProgress += OnOverallProgress;
        _manager.SpeedChanged += OnSpeedChanged;

        LoadHistory();
    }

    public ObservableCollection<DownloadItemViewModel> CurrentItems { get; } = new();

    public ObservableCollection<DownloadItemViewModel> HistoryItems { get; } = new();

    private double _overallProgress;
    public double OverallProgress
    {
        get => _overallProgress;
        private set => SetProperty(ref _overallProgress, value);
    }

    private string _progressSummary = "";
    public string ProgressSummary
    {
        get => _progressSummary;
        private set => SetProperty(ref _progressSummary, value);
    }

    private string _speed = "0.00 MB/s";
    public string Speed
    {
        get => _speed;
        private set => SetProperty(ref _speed, value);
    }

    private bool _hasDownloads;
    public bool HasDownloads
    {
        get => _hasDownloads;
        private set => SetProperty(ref _hasDownloads, value);
    }

    public bool HasCurrentDownloads => CurrentItems.Count > 0;

    public bool HasHistoryDownloads => HistoryItems.Count > 0;
    private bool _allDone;
    /// <summary>全部任务均已完成（用于面板全局进度绿色高亮）。</summary>
    public bool AllDone
    {
        get => _allDone;
        private set => SetProperty(ref _allDone, value);
    }

    private void OnChapterPending(object? sender, ChapterPendingEventArgs e) => _dispatcher.Invoke(() =>
    {
        var item = GetOrAdd(e.ChapterId, e.AlbumTitle, e.ChapterTitle);
        item.StatusText = "等待中…";
        item.IsDownloading = false;
        UpdateCounts();
        AllDone = false;
    });

    private void OnChapterStart(object? sender, ChapterStartEventArgs e) => _dispatcher.Invoke(() =>
    {
        if (!_itemsById.TryGetValue(e.ChapterId, out var item))
        {
            return;
        }
        item.TotalCount = e.Total;
        item.Progress = 0;
        item.ProgressText = $"0 / {e.Total}";
        item.StatusText = "下载中…";
        item.IsDownloading = true;
        item.IsFailed = false;
        item.IsDone = false;
        UpdateCounts();
        AllDone = false;
    });

    private void OnImageSuccess(object? sender, ImageSuccessEventArgs e) => _dispatcher.Invoke(() =>
    {
        if (!_itemsById.TryGetValue(e.ChapterId, out var item))
        {
            return;
        }
        var total = item.TotalCount > 0 ? item.TotalCount : e.DownloadedCount;
        item.Progress = total == 0 ? 100 : e.DownloadedCount * 100.0 / total;
        item.ProgressText = $"{e.DownloadedCount} / {total}";
    });

    private void OnImageError(object? sender, ImageErrorEventArgs e) => _dispatcher.Invoke(() =>
    {
        if (!_itemsById.TryGetValue(e.ChapterId, out var item))
        {
            return;
        }
        item.StatusText = "下载出错";
        item.IsFailed = true;
        AllDone = false;
    });

    private void OnChapterEnd(object? sender, ChapterEndEventArgs e) => _dispatcher.Invoke(() =>
    {
        if (!_itemsById.TryGetValue(e.ChapterId, out var item))
        {
            return;
        }
        item.IsDownloading = false;
        if (e.ErrMsg is null)
        {
            item.StatusText = "已完成";
            item.IsDone = true;
            item.Progress = 100;
            item.CompletedAt = DateTime.Now;
            Navigation.NotifyDownloadedKeysChanged();
            _history.Append(new DownloadHistoryEntry
            {
                AlbumTitle = item.AlbumTitle,
                ChapterTitle = item.ChapterTitle,
                Status = "成功",
                CompletedAt = DateTime.Now,
                ImageCount = item.TotalCount,
            });
            ToastService.Show($"「{item.ChapterTitle}」下载完成", ToastKind.Success);
        }
        else
        {
            item.StatusText = "下载失败";
            item.IsFailed = true;
            item.CompletedAt = DateTime.Now;
            _history.Append(new DownloadHistoryEntry
            {
                AlbumTitle = item.AlbumTitle,
                ChapterTitle = item.ChapterTitle,
                Status = "失败",
                CompletedAt = DateTime.Now,
            });
            ToastService.Show(e.ErrMsg, ToastKind.Error);
        }
        MoveToHistory(item);
        AllDone = CurrentItems.Count == 0;
    });

    private void OnOverallProgress(object? sender, OverallProgressEventArgs e) => _dispatcher.Invoke(() =>
    {
        OverallProgress = e.Percentage;
        ProgressSummary = $"{e.DownloadedImageCount} / {e.TotalImageCount} 张";
    });

    private void OnSpeedChanged(object? sender, SpeedEventArgs e) => _dispatcher.Invoke(() =>
    {
        Speed = e.Speed;
    });

    /// <summary>启动时加载持久化历史记录，最新记录排在最前。</summary>
    private void LoadHistory()
    {
        var history = _history.Load();
        history.Reverse();
        foreach (var entry in history)
        {
            var item = new DownloadItemViewModel
            {
                ChapterId = -1, // 历史条目无真实 chapter id，仅展示
                AlbumTitle = entry.AlbumTitle,
                ChapterTitle = entry.ChapterTitle,
                StatusText = entry.Status == "成功" ? "已完成" : "下载失败",
                IsDone = entry.Status == "成功",
                IsFailed = entry.Status != "成功",
                Progress = entry.Status == "成功" ? 100 : 0,
                TotalCount = entry.ImageCount,
                ProgressText = entry.ImageCount > 0 ? $"{entry.ImageCount} / {entry.ImageCount}" : "",
                CompletedAt = entry.CompletedAt,
            };
            HistoryItems.Add(item);
        }
        UpdateCounts();
    }

    private DownloadItemViewModel GetOrAdd(long chapterId, string albumTitle, string chapterTitle)
    {
        if (_itemsById.TryGetValue(chapterId, out var item))
        {
            return item;
        }
        item = new DownloadItemViewModel
        {
            ChapterId = chapterId,
            AlbumTitle = albumTitle,
            ChapterTitle = chapterTitle,
        };
        _itemsById[chapterId] = item;
        CurrentItems.Insert(0, item);
        UpdateCounts();
        return item;
    }

    private void MoveToHistory(DownloadItemViewModel item)
    {
        CurrentItems.Remove(item);
        _itemsById.Remove(item.ChapterId);
        HistoryItems.Insert(0, item);

        // 历史持久化仍由服务限制为 200 条；界面只保留最近一段，避免长列表拖慢面板。
        while (HistoryItems.Count > MaxVisibleHistoryCount)
        {
            HistoryItems.RemoveAt(HistoryItems.Count - 1);
        }
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        HasDownloads = CurrentItems.Count > 0 || HistoryItems.Count > 0;
        OnPropertyChanged(nameof(HasCurrentDownloads));
        OnPropertyChanged(nameof(HasHistoryDownloads));
    }

    public void Dispose()
    {
        _manager.ChapterPending -= OnChapterPending;
        _manager.ChapterStart -= OnChapterStart;
        _manager.ImageSuccess -= OnImageSuccess;
        _manager.ImageError -= OnImageError;
        _manager.ChapterEnd -= OnChapterEnd;
        _manager.OverallProgress -= OnOverallProgress;
        _manager.SpeedChanged -= OnSpeedChanged;
    }
}

