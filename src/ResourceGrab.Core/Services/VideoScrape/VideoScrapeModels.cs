using System.Text.Json.Serialization;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M0: VideoContentKind — 与 Amane 对齐的七种内容分类
// ---------------------------------------------------------------------------

public enum VideoContentKind
{
    Unknown = 0,
    Censored,
    Fc2,
    Uncensored,
    Chinese,
    Amateur,
    Western,
    Hentai
}

// ---------------------------------------------------------------------------
// M0: 元数据字段名常量，避免硬编码散落
// ---------------------------------------------------------------------------

public static class VideoMetadataFields
{
    public const string Title = "title";
    public const string OriginalTitle = "original_title";
    public const string Description = "description";
    public const string Actors = "actors";
    public const string Tags = "tags";
    public const string Series = "series";
    public const string Studio = "studio";
    public const string Publisher = "publisher";
    public const string Director = "director";
    public const string ReleaseDate = "release_date";
    public const string RuntimeMinutes = "runtime_minutes";
    public const string CoverUrl = "cover_url";
    public const string PreviewImageUrls = "preview_image_urls";
    public const string RelatedNumbers = "related_numbers";
    public const string Score = "score";
    public const string HasChineseSubtitle = "has_chinese_subtitle";
    public const string HasMagnet = "has_magnet";
}

// ---------------------------------------------------------------------------
// M1: 来源请求与返回模型
// ---------------------------------------------------------------------------

public enum VideoSourceOutcome
{
    Success,
    NoMatch,
    HttpError,
    Blocked,
    ParseError,
    Cancelled,
    Disabled,
    CacheHit
}

public sealed record VideoSourceAttempt(
    string SourceId,
    string? Language,
    VideoSourceOutcome Outcome,
    string? Reason,
    int ElapsedMs,
    DateTimeOffset At);

public sealed record VideoSourceRequest(
    string Number,
    VideoContentKind ContentKind,
    string? Language,
    string? FilePath,
    VideoSourceFetchOptions Options);

public sealed class VideoSourceFetchOptions
{
    public bool ForceRefresh { get; init; }
    public bool CacheOnly { get; init; }
}

public sealed class VideoSourceFetchResult
{
    public required string SourceId { get; init; }
    public string? Language { get; init; }
    public VideoSourceOutcome Outcome { get; init; }
    public string? Error { get; init; }
    public VideoScrapeMetadata? Metadata { get; init; }
    public int ElapsedMs { get; init; }

    public static VideoSourceFetchResult Success(string sourceId, VideoScrapeMetadata metadata, int elapsedMs = 0, string? language = null) =>
        new() { SourceId = sourceId, Outcome = VideoSourceOutcome.Success, Metadata = metadata, ElapsedMs = elapsedMs, Language = language };

    public static VideoSourceFetchResult NoMatch(string sourceId, int elapsedMs = 0) =>
        new() { SourceId = sourceId, Outcome = VideoSourceOutcome.NoMatch, ElapsedMs = elapsedMs };

    public static VideoSourceFetchResult Failure(string sourceId, VideoSourceOutcome outcome, string? reason, int elapsedMs = 0) =>
        new() { SourceId = sourceId, Outcome = outcome, Error = reason, ElapsedMs = elapsedMs };
}

// ---------------------------------------------------------------------------
// M1: 站点快照
// ---------------------------------------------------------------------------

public sealed class VideoSourceSnapshot
{
    public required string Number { get; init; }
    public required string SourceId { get; init; }
    public string? Language { get; init; }
    public required string SchemaVersion { get; init; }
    public required VideoScrapeMetadata Metadata { get; init; }
    public Dictionary<string, object?> RawExtras { get; set; } = [];
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
}

// ---------------------------------------------------------------------------
// M3: 聚合结果
// ---------------------------------------------------------------------------

public sealed class VideoScrapeAggregate
{
    public required string Number { get; init; }
    public required VideoScrapeMetadata Metadata { get; init; }
    /// <summary>key = 元数据字段名；value = 来源 ID 或多个来源 ID（+分隔）。</summary>
    public Dictionary<string, string> FieldSources { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<VideoSourceAttempt> Attempts { get; init; } = [];
    public bool FromCacheOnly { get; init; }
}
