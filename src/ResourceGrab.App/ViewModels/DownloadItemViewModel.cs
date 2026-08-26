using ResourceGrab.App.Common;

namespace ResourceGrab.App.ViewModels;

/// <summary>下载队列中的单个章节任务。</summary>
public class DownloadItemViewModel : ObservableObject
{
    public long ChapterId { get; init; }
    public string AlbumTitle { get; init; } = "";
    public string ChapterTitle { get; init; } = "";

    public string DisplayTitle => $"{AlbumTitle} · {ChapterTitle}";

    private string _statusText = "等待中…";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private long _totalCount;
    public long TotalCount
    {
        get => _totalCount;
        set => SetProperty(ref _totalCount, value);
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value);
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set => SetProperty(ref _isDownloading, value);
    }

    private bool _isDone;
    public bool IsDone
    {
        get => _isDone;
        set => SetProperty(ref _isDone, value);
    }

    private bool _isFailed;
    public bool IsFailed
    {
        get => _isFailed;
        set => SetProperty(ref _isFailed, value);
    }

    /// <summary>完成时间（历史记录用，未完成/进行中为 null）。</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>状态展示文本（含完成时间）。</summary>
    public string StatusDisplayText => StatusText + (CompletedAt is { } time ? $" · {time:MM-dd HH:mm}" : "");
}
