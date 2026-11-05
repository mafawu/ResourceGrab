using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ResourceGrab.App.Common;
using ResourceGrab.App.Services;
using ResourceGrab.Core;
using ResourceGrab.Core.Downloading;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Services.VideoScrape;

namespace ResourceGrab.App.ViewModels;

/// <summary>传输条目的媒体类型。</summary>
public enum TransferKind
{
    Manga,
    Video,
}

/// <summary>传输条目的状态。</summary>
public enum TransferStatus
{
    Queued,
    Running,
    Failed,
    Done,
}

/// <summary>
/// 下载页的一行：漫画章节（DownloadManager 事件驱动）或视频文件（视频任务队列轮询）。
/// 两类进度来源不同，对外都收成同一组展示字段，页面只认这一种模型。
/// </summary>
public sealed class TransferItem : ObservableObject
{
    public string Key { get; init; } = "";
    public TransferKind Kind { get; init; }

    /// <summary>漫画所属漫画名（拼历史目录用）。</summary>
    public string AlbumTitle { get; set; } = "";

    /// <summary>漫画章节名（拼历史目录 / 历史记录用；标题里已经拼上主标题）。</summary>
    public string ChapterTitle { get; set; } = "";

    /// <summary>漫画章节的事件 id（重试用）；视频为 0。</summary>
    public long ChapterId { get; set; }

    /// <summary>漫画章节预计张数。</summary>
    public long TotalCount { get; set; }

    /// <summary>漫画章节已下张数。</summary>
    public long DoneCount { get; set; }

    /// <summary>最近一张图片的错误（章节成败以 ChapterEnd 为准）。</summary>
    public string? LastImageError { get; set; }

    /// <summary>视频番号；漫画为空。</summary>
    public string Number { get; init; } = "";

    /// <summary>番号角标是否显示：短剧的 Number 是剧名（标题里已有），不重复展示。</summary>
    public bool ShowNumber { get; init; }

    private string _title = "";
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private string _meta = "";
    public string Meta
    {
        get => _meta;
        set => SetProperty(ref _meta, value);
    }

    private string _sub = "";
    public string Sub
    {
        get => _sub;
        set => SetProperty(ref _sub, value);
    }

    private string _percentText = "";
    public string PercentText
    {
        get => _percentText;
        set => SetProperty(ref _percentText, value);
    }

    private double _percent;
    public double Percent
    {
        get => _percent;
        set => SetProperty(ref _percent, value);
    }

    private TransferStatus _status = TransferStatus.Queued;
    public TransferStatus Status
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusKey));
            OnPropertyChanged(nameof(ShowCancel));
            OnPropertyChanged(nameof(ShowRetry));
            OnPropertyChanged(nameof(ShowOpenFile));
            OnPropertyChanged(nameof(ShowOpenFolder));
            OnPropertyChanged(nameof(IsDone));
        }
    }

    /// <summary>XAML 触发器用的稳定字符串。</summary>
    public string StatusKey => Status switch
    {
        TransferStatus.Running => "running",
        TransferStatus.Failed => "failed",
        TransferStatus.Done => "done",
        _ => "queued",
    };

    private string? _errorText;
    public string? ErrorText
    {
        get => _errorText;
        set
        {
            if (!SetProperty(ref _errorText, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool IsDone => Status == TransferStatus.Done;

    /// <summary>视频任务 Id（取消用）。</summary>
    public string VideoTaskId { get; set; } = "";

    /// <summary>重试所需的任务载荷（视频下载）。</summary>
    public VideoDownloadRequest? Request { get; set; }

    /// <summary>完成后可打开的本地文件（视频）。</summary>
    public string? FilePath { get; set; }

    /// <summary>完成后可打开的目录（漫画章节目录 / 视频所在目录）。</summary>
    public string? FolderPath { get; set; }

    public DateTime CompletedAt { get; set; }

    /// <summary>历史分组标题（今天 / 昨天 / 日期）。</summary>
    public string DayLabel { get; set; } = "";

    /// <summary>是否该分组的首行（渲染日期头）。</summary>
    public bool IsFirstOfDay { get; set; }

    /// <summary>漫画可取消？下载引擎没有章节级取消，先如实置灰。</summary>
    public bool ShowCancel => Kind == TransferKind.Video && Status is TransferStatus.Running or TransferStatus.Queued;

    /// <summary>失败后可重试：漫画凭章节事件 id 重新入队，视频重新提交下载请求。</summary>
    public bool ShowRetry => Status == TransferStatus.Failed
        && (Kind == TransferKind.Video ? Request is not null : ChapterId > 0);

    public bool ShowOpenFile => Status == TransferStatus.Done && !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath);

    public bool ShowOpenFolder => Status == TransferStatus.Done && !string.IsNullOrEmpty(FolderPath);

    public void SetStatus(TransferStatus status, string? error = null)
    {
        Status = status;
        ErrorText = status == TransferStatus.Failed ? error : null;
    }

    public void SetProgress(double percent, string percentText)
    {
        Percent = Math.Clamp(percent, 0, 100);
        PercentText = percentText;
    }
}

/// <summary>
/// 下载页视图模型：把「漫画章节下载」（DownloadManager）与「视频直存下载」（视频任务队列）
/// 合成同一份传输列表，并提供计数、总速度与历史（漫画 + 视频两份持久化记录）。
/// </summary>
public sealed class DownloadPageViewModel : ObservableObject, IDisposable
{
    private readonly DownloadManager _mangaManager;
    private readonly VideoScrapeTaskQueue _videoQueue;
    private readonly ConfigService _config;
    private readonly DownloadHistoryService _mangaHistory;
    private readonly VideoDownloadHistoryService _videoHistory;
    private readonly Dispatcher _dispatcher;

    private readonly Dictionary<long, TransferItem> _mangaItems = new();
    private readonly Dictionary<string, TransferItem> _videoItems = new();
    private readonly HashSet<string> _videoRecorded = new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherTimer _timer;
    private long _mangaBytesPerSec;
    private double _videoBytesPerSec;
    private bool _disposed;

    public DownloadPageViewModel(
        DownloadManager mangaManager,
        VideoScrapeTaskQueue videoQueue,
        ConfigService config,
        DownloadHistoryService? mangaHistory = null,
        VideoDownloadHistoryService? videoHistory = null)
    {
        _mangaManager = mangaManager;
        _videoQueue = videoQueue;
        _config = config;
        _mangaHistory = mangaHistory ?? new DownloadHistoryService();
        _videoHistory = videoHistory ?? new VideoDownloadHistoryService();
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _mangaManager.ChapterPending += OnChapterPending;
        _mangaManager.ChapterStart += OnChapterStart;
        _mangaManager.ImageSuccess += OnImageSuccess;
        _mangaManager.ImageError += OnImageError;
        _mangaManager.ChapterEnd += OnChapterEnd;
        _mangaManager.SpeedChanged += OnMangaSpeed;

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => SyncVideoQueue();

        LoadHistory();
        SyncVideoQueue();
        _timer.Start();
    }

    public ObservableCollection<TransferItem> CurrentItems { get; } = new();

    public ObservableCollection<TransferItem> HistoryItems { get; } = new();

    /// <summary>进行中 / 排队 / 失败 / 今日完成 / 总速度 变化时触发，供顶栏角标使用。</summary>
    public event EventHandler? StateChanged;

    public int ActiveCount => CurrentItems.Count(i => i.Status == TransferStatus.Running);

    public int QueuedCount => CurrentItems.Count(i => i.Status == TransferStatus.Queued);

    public int FailedCount => CurrentItems.Count(i => i.Status == TransferStatus.Failed);

    public int RunningTotal => ActiveCount + QueuedCount;

    public int TodayDoneCount => HistoryItems.Count(i => i.CompletedAt.Date == DateTime.Today);

    public string SpeedText
    {
        get
        {
            var total = _mangaBytesPerSec + _videoBytesPerSec;
            if (total <= 0) return "—";
            return total < 1024 * 1024
                ? $"{total / 1024.0:F0} KB/s"
                : $"{total / 1024.0 / 1024.0:F2} MB/s";
        }
    }

    public bool HasCurrent => CurrentItems.Count > 0;

    public bool HasHistory => HistoryItems.Count > 0;

    /// <summary>当前列表里有可重试的失败任务（页头「重试全部失败」按钮用）。</summary>
    public bool HasFailed => FailedCount > 0;

    // ====================== 漫画（DownloadManager） ======================

    private void OnChapterPending(object? sender, ChapterPendingEventArgs e) => OnUi(() =>
    {
        var item = GetOrAddManga(e.ChapterId, e.ChapterTitle, e.AlbumTitle);
        item.LastImageError = null;
        item.SetStatus(TransferStatus.Queued);
        item.PercentText = "等待中";
        RefreshMangaMeta(item);
        SortCurrent();
        Publish();
    });

    private void OnChapterStart(object? sender, ChapterStartEventArgs e) => OnUi(() =>
    {
        if (!_mangaItems.TryGetValue(e.ChapterId, out var item)) return;
        item.TotalCount = e.Total;
        item.DoneCount = 0;
        item.SetStatus(TransferStatus.Running);
        item.SetProgress(0, "0%");
        RefreshMangaMeta(item);
        Publish();
    });

    private void OnImageSuccess(object? sender, ImageSuccessEventArgs e) => OnUi(() =>
    {
        if (!_mangaItems.TryGetValue(e.ChapterId, out var item)) return;
        item.DoneCount = e.DownloadedCount;
        var total = Math.Max(1, item.TotalCount);
        item.SetProgress(item.DoneCount * 100.0 / total, $"{(int)(item.DoneCount * 100.0 / total)}%");
        RefreshMangaMeta(item);
        Publish();
    });

    private void OnImageError(object? sender, ImageErrorEventArgs e) => OnUi(() =>
    {
        if (!_mangaItems.TryGetValue(e.ChapterId, out var item)) return;
        // 单张失败不立刻翻状态：章节结束时才知道是"部分失败"还是"整章失败"
        item.LastImageError = e.Error;
    });

    private void OnChapterEnd(object? sender, ChapterEndEventArgs e) => OnUi(() =>
    {
        if (!_mangaItems.TryGetValue(e.ChapterId, out var item)) return;
        if (e.ErrMsg is null)
        {
            item.SetStatus(TransferStatus.Done);
            item.SetProgress(100, "完成");
            item.CompletedAt = DateTime.Now;
            item.FolderPath = item.FolderPath ?? Path.Combine(_config.Current.DownloadDir, item.AlbumTitle, item.ChapterTitle);
            _mangaHistory.Append(new DownloadHistoryEntry
            {
                AlbumTitle = item.AlbumTitle,
                ChapterTitle = item.ChapterTitle,
                Status = "成功",
                CompletedAt = item.CompletedAt,
                ImageCount = item.TotalCount,
            });
            MoveToHistory(item);
        }
        else
        {
            item.SetStatus(TransferStatus.Failed, BuildMangaError(item, e.ErrMsg));
            item.PercentText = "失败";
            _mangaHistory.Append(new DownloadHistoryEntry
            {
                AlbumTitle = item.AlbumTitle,
                ChapterTitle = item.ChapterTitle,
                Status = "失败",
                CompletedAt = DateTime.Now,
            });
        }
        SortCurrent();
        Publish();
    });

    private void OnMangaSpeed(object? sender, SpeedEventArgs e) => OnUi(() =>
    {
        _mangaBytesPerSec = e.BytesPerSecond;
        OnPropertyChanged(nameof(SpeedText));
    });

    private TransferItem GetOrAddManga(long chapterId, string chapterTitle, string albumTitle)
    {
        if (_mangaItems.TryGetValue(chapterId, out var existing)) return existing;
        var item = new TransferItem
        {
            Key = $"manga:{chapterId}",
            Kind = TransferKind.Manga,
            AlbumTitle = albumTitle,
            ChapterTitle = chapterTitle,
            ChapterId = chapterId,
            Title = ComposeTitle(albumTitle, chapterTitle),
        };
        _mangaItems[chapterId] = item;
        CurrentItems.Insert(0, item);
        return item;
    }

    /// <summary>主标题 · 副标题；两者相同（或副标题为空）时只显示一次。</summary>
    private static string ComposeTitle(string mainTitle, string subTitle)
    {
        var main = mainTitle?.Trim() ?? "";
        var sub = subTitle?.Trim() ?? "";
        if (main.Length == 0) return sub;
        if (sub.Length == 0 || string.Equals(main, sub, StringComparison.Ordinal)) return main;
        return $"{main} · {sub}";
    }

    /// <summary>漫画行副标题：已下/总数 张（漫画名已经在主标题里，不再重复）。</summary>
    private static void RefreshMangaMeta(TransferItem item)
    {
        item.Meta = item.TotalCount > 0
            ? $"漫画 · {item.DoneCount} / {item.TotalCount} 张"
            : "漫画 · 准备中";
    }

    /// <summary>
    /// 章节级失败原因：图片缺失时补上最后一张的真实错误（超时 / 403 / 分块损坏），
    /// 否则用户只看到"少了几张"，不知道该不该重试。
    /// </summary>
    private static string BuildMangaError(TransferItem item, string message)
    {
        var detail = item.LastImageError;
        if (string.IsNullOrWhiteSpace(detail)) return message;
        if (message.Contains(detail, StringComparison.Ordinal)) return message;
        if (detail.Length > 120) detail = detail[..117] + "…";
        return $"{message}；最后错误：{detail}";
    }

    // ====================== 视频（视频任务队列） ======================

    private void SyncVideoQueue()
    {
        if (_disposed) return;
        IReadOnlyCollection<VideoScrapeTask> tasks;
        try
        {
            tasks = _videoQueue.GetAllTasks();
        }
        catch
        {
            return;
        }

        var downloads = tasks.Where(t => t.Type == VideoTaskType.DownloadVideo).ToList();
        double videoSpeed = 0;

        foreach (var task in downloads)
        {
            switch (task.Status)
            {
                case VideoTaskStatus.Completed:
                case VideoTaskStatus.Skipped:
                    RecordVideoDone(task);
                    break;

                case VideoTaskStatus.Failed:
                    {
                        var item = GetOrAddVideo(task);
                        item.SetStatus(TransferStatus.Failed, string.IsNullOrWhiteSpace(task.Error) ? "下载失败" : task.Error);
                        item.PercentText = "失败";
                        item.Title = DisplayTitle(task);
                        item.Meta = BuildVideoMeta(task);
                        item.Sub = "";
                        break;
                    }

                case VideoTaskStatus.Cancelled:
                    {
                        if (_videoItems.Remove(task.Id, out var cancelled))
                        {
                            CurrentItems.Remove(cancelled);
                        }
                        break;
                    }

                case VideoTaskStatus.Running:
                    {
                        var item = GetOrAddVideo(task);
                        item.SetStatus(TransferStatus.Running);
                        item.Title = DisplayTitle(task);
                        item.Meta = BuildVideoMeta(task);
                        var percent = Math.Clamp(task.Completed, 0, 100);
                        item.SetProgress(percent, $"{percent}%");
                        item.Sub = BuildVideoSub(task);
                        videoSpeed += Math.Max(0, task.SpeedBytesPerSec);
                        break;
                    }

                default:
                    {
                        var item = GetOrAddVideo(task);
                        item.SetStatus(TransferStatus.Queued);
                        item.Title = DisplayTitle(task);
                        item.Meta = BuildVideoMeta(task);
                        item.PercentText = "等待中";
                        item.Sub = "";
                        break;
                    }
            }
        }

        _videoBytesPerSec = videoSpeed;
        SortCurrent();
        Publish();
    }

    private TransferItem GetOrAddVideo(VideoScrapeTask task)
    {
        if (_videoItems.TryGetValue(task.Id, out var existing)) return existing;
        var item = new TransferItem
        {
            Key = $"video:{task.Id}",
            Kind = TransferKind.Video,
            Number = task.Number,
            // 短剧的 Number 是剧名（标题里已有）；普通视频标题恰好等于番号时也不重复显示角标
            ShowNumber = !IsDramaTask(task)
                && !string.Equals(task.Number, task.DownloadTitle, StringComparison.OrdinalIgnoreCase),
            VideoTaskId = task.Id,
            Request = new VideoDownloadRequest(
                task.Number,
                task.DownloadTitle ?? "",
                task.DownloadUrl ?? "",
                task.DownloadReferer ?? "",
                task.DownloadProxy,
                task.DownloadDurationText ?? "",
                task.DownloadMasterUrl ?? "",
                task.DownloadVariantLabel ?? "",
                task.DownloadOutputDir,
                task.DownloadCategory),
        };
        item.Title = DisplayTitle(task);
        _videoItems[task.Id] = item;
        CurrentItems.Insert(0, item);
        return item;
    }

    /// <summary>短剧任务在下载页的显示名：剧名 + 集号（普通视频没有剧名，直接用标题）。</summary>
    private static string DisplayTitle(VideoScrapeTask task)
        => IsDramaTask(task)
            ? $"{task.Number} · {task.DownloadTitle}".Trim(' ', '·')
            : task.DownloadTitle ?? task.Number;

    private static bool IsDramaTask(VideoScrapeTask task)
        => string.Equals(task.DownloadCategory, "shortdrama", StringComparison.OrdinalIgnoreCase);

    private static string BuildVideoMeta(VideoScrapeTask task)
    {
        var done = VideoDownloadService.FormatSize(task.DownloadedBytes);
        var total = task.TotalBytes > 0 ? VideoDownloadService.FormatSize(task.TotalBytes) : "";
        var kind = IsDramaTask(task) ? "短剧" : "视频";
        return string.IsNullOrEmpty(total) || done == total
            ? $"{kind} · {done}"
            : $"{kind} · {done} / {total}";
    }

    private static string BuildVideoSub(VideoScrapeTask task)
    {
        if (task.SpeedBytesPerSec <= 0) return "";
        var speed = VideoDownloadService.FormatBytes(task.SpeedBytesPerSec);
        return task.EtaSeconds >= 0
            ? $"{speed} · 剩 {VideoDownloadService.FormatEta(task.EtaSeconds)}"
            : speed;
    }

    /// <summary>任务完成后落盘一次历史，再把它从「当前」移到「已完成」。</summary>
    private void RecordVideoDone(VideoScrapeTask task)
    {
        if (!_videoRecorded.Add(task.Id)) return;
        var path = task.DownloadOutputPath ?? "";
        long size = 0;
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                size = new FileInfo(path).Length;
            }
        }
        catch
        {
            // 读不到大小不影响记录
        }

        var entry = new VideoDownloadHistoryEntry
        {
            Number = task.Number,
            Title = task.DownloadTitle ?? "",
            OutputPath = path,
            SizeBytes = size,
            CompletedAt = (task.CompletedAt?.ToLocalTime().DateTime) ?? DateTime.Now,
        };
        _videoHistory.Append(entry);

        if (_videoItems.Remove(task.Id, out var item))
        {
            CurrentItems.Remove(item);
        }
        item = new TransferItem
        {
            Key = $"video:{task.Id}:done",
            Kind = TransferKind.Video,
            Number = task.Number,
            ShowNumber = !IsDramaTask(task),
            Title = string.IsNullOrWhiteSpace(entry.Title) ? task.Number : entry.Title,
            Meta = $"视频 · {VideoDownloadService.FormatSize(size)} · {entry.CompletedAt:HH:mm} 完成",
            PercentText = "完成",
            FilePath = path,
            FolderPath = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path),
            CompletedAt = entry.CompletedAt,
        };
        item.SetStatus(TransferStatus.Done);
        HistoryItems.Insert(0, item);
        RebuildDayLabels();
    }

    // ====================== 历史 ======================

    public void LoadHistory()
    {
        HistoryItems.Clear();
        foreach (var entry in _mangaHistory.Load())
        {
            var folder = Path.Combine(_config.Current.DownloadDir, entry.AlbumTitle, entry.ChapterTitle);
            var item = new TransferItem
            {
                Key = $"manga:{entry.AlbumTitle}|{entry.ChapterTitle}|{entry.CompletedAt.Ticks}",
                Kind = TransferKind.Manga,
                AlbumTitle = entry.AlbumTitle,
                ChapterTitle = entry.ChapterTitle,
                Title = ComposeTitle(entry.AlbumTitle, entry.ChapterTitle),
                Meta = entry.Status == "成功"
                    ? $"漫画 · {entry.ImageCount} 张 · {entry.CompletedAt:HH:mm} 完成"
                    : $"漫画 · 下载失败 · {entry.CompletedAt:HH:mm}",
                PercentText = entry.Status == "成功" ? "完成" : "失败",
                FolderPath = folder,
                CompletedAt = entry.CompletedAt,
            };
            item.SetStatus(entry.Status == "成功" ? TransferStatus.Done : TransferStatus.Failed,
                entry.Status == "成功" ? null : "上次下载未完成");
            HistoryItems.Add(item);
        }

        foreach (var entry in _videoHistory.Load())
        {
            var item = new TransferItem
            {
                Key = $"video:{entry.OutputPath}",
                Kind = TransferKind.Video,
                Number = entry.Number,
                Title = string.IsNullOrWhiteSpace(entry.Title) ? entry.Number : entry.Title,
                Meta = $"视频 · {VideoDownloadService.FormatSize(entry.SizeBytes)} · {entry.CompletedAt:HH:mm} 完成",
                PercentText = "完成",
                FilePath = entry.OutputPath,
                FolderPath = string.IsNullOrEmpty(entry.OutputPath) ? null : Path.GetDirectoryName(entry.OutputPath),
                CompletedAt = entry.CompletedAt,
            };
            item.SetStatus(TransferStatus.Done);
            HistoryItems.Add(item);
        }

        var ordered = HistoryItems.OrderByDescending(i => i.CompletedAt).ToList();
        HistoryItems.Clear();
        foreach (var item in ordered) HistoryItems.Add(item);
        RebuildDayLabels();
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(TodayDoneCount));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>重算"今天 / 昨天 / 日期"分组头。</summary>
    private void RebuildDayLabels()
    {
        string? last = null;
        foreach (var item in HistoryItems)
        {
            var date = item.CompletedAt.Date;
            var label = date == DateTime.Today ? "今天"
                : date == DateTime.Today.AddDays(-1) ? "昨天"
                : date.ToString("yyyy-MM-dd");
            item.DayLabel = label;
            item.IsFirstOfDay = label != last;
            last = label;
            OnPropertyChanged(nameof(TransferItem.DayLabel));
            OnPropertyChanged(nameof(TransferItem.IsFirstOfDay));
        }
    }

    public void ClearHistory()
    {
        _mangaHistory.Clear();
        _videoHistory.Clear();
        HistoryItems.Clear();
        RebuildDayLabels();
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(TodayDoneCount));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ====================== 操作 ======================

    public void Cancel(TransferItem item)
    {
        if (item.Kind != TransferKind.Video || string.IsNullOrEmpty(item.VideoTaskId)) return;
        _videoQueue.Cancel(item.VideoTaskId);
        SyncVideoQueue();
    }

    /// <summary>重试单个失败任务：漫画按章节事件 id 重新入队，视频重新提交下载请求。</summary>
    /// <returns>是否已成功重新入队。</returns>
    public async Task<bool> RetryAsync(TransferItem item)
    {
        if (item.Kind == TransferKind.Manga)
        {
            if (item.ChapterId <= 0) return false;
            try
            {
                if (!await _mangaManager.RetryChapterAsync(item.ChapterId))
                {
                    ToastService.Show("暂时无法重试：任务可能正在队列中或已失效", ToastKind.Info);
                    return false;
                }
                // 先本地翻回排队态：章节事件回到 UI 线程前用户还能再点一次
                item.LastImageError = null;
                item.SetStatus(TransferStatus.Queued);
                item.PercentText = "等待中";
                RefreshMangaMeta(item);
                SortCurrent();
                Publish();
                return true;
            }
            catch (Exception ex)
            {
                ToastService.ShowError(ex, "重试失败：");
                return false;
            }
        }

        if (item.Request is null) return false;
        try
        {
            _videoQueue.EnqueueDownload(item.Request);
            CurrentItems.Remove(item);
            _videoItems.Remove(item.VideoTaskId);
            Publish();
            return true;
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex, "重新下载失败：");
            return false;
        }
    }

    /// <summary>批量重试当前列表里所有可重试的失败任务，返回实际提交的数量。</summary>
    public async Task<int> RetryAllFailedAsync()
    {
        var targets = CurrentItems.Where(i => i.ShowRetry).ToList();
        var submitted = 0;
        foreach (var item in targets)
        {
            if (await RetryAsync(item)) submitted++;
        }
        return submitted;
    }

    /// <summary>下载根目录：漫画一个、视频一个，页头按钮让用户自己选。</summary>
    public string MangaDownloadDir => _config.Current.DownloadDir;

    public string VideoDownloadDir =>
        string.IsNullOrWhiteSpace(_config.Current.VideoScraping?.VideoDownloadDir)
            ? Path.Combine(AppPaths.AppDataDir, "视频下载")
            : _config.Current.VideoScraping!.VideoDownloadDir;
    /// <summary>短剧下载目录（与设置里「短剧下载目录」同源；留空即 config\短剧下载）。</summary>
    public string ShortDramaDownloadDir
        => ShortDramaSettings.ResolveDownloadDir(_config.Current.ShortDrama?.DownloadDir);

    // ====================== 基础设施 ======================

    private void MoveToHistory(TransferItem item)
    {
        CurrentItems.Remove(item);
        item.IsFirstOfDay = HistoryItems.Count == 0 || HistoryItems[0].CompletedAt.Date != item.CompletedAt.Date;
        HistoryItems.Insert(0, item);
        RebuildDayLabels();
        OnPropertyChanged(nameof(HasHistory));
    }

    private void SortCurrent()
    {
        var ordered = CurrentItems
            .OrderBy(i => i.Status switch
            {
                TransferStatus.Running => 0,
                TransferStatus.Queued => 1,
                TransferStatus.Failed => 2,
                _ => 3,
            })
            .ToList();
        if (ordered.SequenceEqual(CurrentItems)) return;
        CurrentItems.Clear();
        foreach (var item in ordered) CurrentItems.Add(item);
    }

    private void Publish()
    {
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(RunningTotal));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(HasCurrent));
        OnPropertyChanged(nameof(HasFailed));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnUi(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _mangaManager.ChapterPending -= OnChapterPending;
        _mangaManager.ChapterStart -= OnChapterStart;
        _mangaManager.ImageSuccess -= OnImageSuccess;
        _mangaManager.ImageError -= OnImageError;
        _mangaManager.ChapterEnd -= OnChapterEnd;
        _mangaManager.SpeedChanged -= OnMangaSpeed;
    }
}
