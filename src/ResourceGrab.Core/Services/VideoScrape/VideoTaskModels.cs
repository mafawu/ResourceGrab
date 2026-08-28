using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M5: 任务类型与状态
// ---------------------------------------------------------------------------

public enum VideoTaskType
{
    ScrapeVideo,
    RescrapeVideo,
    DownloadResources,
    CleanupResources,
    ActorScrape,
    TranslateMetadata
}

public enum VideoTaskStatus
{
    Pending,
    Running,
    WaitingRetry,
    Completed,
    Failed,
    Cancelled,
    Skipped
}

public sealed class VideoScrapeTask
{
    public required string Id { get; init; }
    public required VideoTaskType Type { get; init; }
    public string? VideoItemId { get; init; }
    public required string Number { get; init; }
    public VideoTaskStatus Status { get; set; } = VideoTaskStatus.Pending;
    public int Priority { get; init; }
    public int Attempt { get; set; }
    public int MaxRetries { get; init; } = 3;
    public List<VideoSourceAttempt> Attempts { get; set; } = [];
    public List<VideoScrapeFollowUp> FollowUps { get; set; } = [];
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<string>? ItemIds { get; set; }

    /// <summary>批次任务的总条目数；单任务固定为 1。</summary>
    public int Total { get; set; }

    /// <summary>执行器实时回填的批内进度。</summary>
    public int Completed { get; set; }
    public int SuccessCount { get; set; }
    public int NoMatchCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<string> Logs { get; set; } = [];
}

public sealed class VideoScrapeFollowUp
{
    public required VideoTaskType Type { get; init; }
    public required string Number { get; init; }
    public string? VideoItemId { get; init; }
}

/// <summary>任务进度报告。</summary>
public sealed class VideoTaskProgress
{
    public int Total { get; init; }
    public int Completed { get; init; }
    public int Running { get; init; }
    public int Pending { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public List<string> RecentLogs { get; set; } = [];
}
