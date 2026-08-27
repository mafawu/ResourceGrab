using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M11: 演员元数据模型
// ---------------------------------------------------------------------------

public sealed class VideoActorMetadata
{
    public required string Name { get; set; }
    public string? Birthday { get; set; }
    public string? Birthplace { get; set; }
    public int? Height { get; set; }
    public List<string> Aliases { get; set; } = [];
    public List<string> ImageUrls { get; set; } = [];
    public Dictionary<string, string> SourceUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FieldSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

// ---------------------------------------------------------------------------
// M11: 演员来源接口
// ---------------------------------------------------------------------------

public enum ActorSourceOutcome
{
    Success,
    NotFound,
    HttpError,
    Blocked,
    ParseError,
    Cancelled,
    Disabled
}

public sealed class ActorSourceFetchResult
{
    public required string SourceId { get; init; }
    public ActorSourceOutcome Outcome { get; init; }
    public string? Error { get; init; }
    public VideoActorMetadata? Metadata { get; init; }
    public int ElapsedMs { get; init; }

    public static ActorSourceFetchResult Success(string sourceId, VideoActorMetadata metadata, int elapsedMs = 0) =>
        new() { SourceId = sourceId, Outcome = ActorSourceOutcome.Success, Metadata = metadata, ElapsedMs = elapsedMs };

    public static ActorSourceFetchResult NotFound(string sourceId, int elapsedMs = 0) =>
        new() { SourceId = sourceId, Outcome = ActorSourceOutcome.NotFound, ElapsedMs = elapsedMs };

    public static ActorSourceFetchResult Failure(string sourceId, ActorSourceOutcome outcome, string? reason, int elapsedMs = 0) =>
        new() { SourceId = sourceId, Outcome = outcome, Error = reason, ElapsedMs = elapsedMs };
}

public interface IVideoActorSource
{
    string Id { get; }
    string DisplayName { get; }
    ValueTask<ActorSourceFetchResult> FetchAsync(string actorName, CancellationToken ct);
}
