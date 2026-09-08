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
    TranslateMetadata,
    /// <summary>在线视频 M3U8/MP4 直存下载（VideoDownloadService + ffmpeg）。</summary>
    DownloadVideo,
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

    /// <summary>批内单条刮削结果流水（最新在前，执行器实时回填，供看板明细展示）。</summary>
    public List<VideoTaskItemResult> ItemResults { get; set; } = [];

    // ---- DownloadVideo 任务载荷（其他类型任务保持 null） ----
    /// <summary>待下载的流地址（HLS m3u8 或 MP4 直链）。</summary>
    public string? DownloadUrl { get; set; }
    /// <summary>展示用标题（番号缺失时回退用）。</summary>
    public string? DownloadTitle { get; set; }
    /// <summary>请求流所需的 Referer 头。</summary>
    public string? DownloadReferer { get; set; }
    /// <summary>下载走的代理（可空）。</summary>
    public string? DownloadProxy { get; set; }
    /// <summary>展示用时长文本（如 "12:34" / "90 分钟"），用于估算进度；未知留空。</summary>
    public string? DownloadDurationText { get; set; }
    /// <summary>下载完成后的本地文件路径（成功/跳过时由执行器回填）。</summary>
    public string? DownloadOutputPath { get; set; }
    /// <summary>主 playlist 地址（重探新鲜分片地址用）。</summary>
    public string? DownloadMasterUrl { get; set; }
    /// <summary>用户所选档位名（如 "720p"），空表示默认最高档。</summary>
    public string? DownloadVariantLabel { get; set; }
}

/// <summary>批内单条刮削的明细：命中来源、刮到的标题、耗时或失败原因。</summary>
public sealed class VideoTaskItemResult
{
    public string Number { get; init; } = "";
    public string FileName { get; init; } = "";
    /// <summary>成功 / 未匹配 / 失败 / 跳过。</summary>
    public string Outcome { get; init; } = "";
    /// <summary>命中的来源，如 "javbus+javdb"。</summary>
    public string Sources { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>补充信息：成功时为演员/标签数，失败时为原因。</summary>
    public string Detail { get; init; } = "";
    public int ElapsedMs { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
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
