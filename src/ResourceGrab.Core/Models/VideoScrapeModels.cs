using System.Text.Json.Serialization;

using ResourceGrab.Core.Services;

namespace ResourceGrab.Core.Models;

public sealed class VideoScrapeMetadata
{
    public string Source { get; set; } = "";
    public string Title { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Actors { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string Series { get; set; } = "";
    public string Studio { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Director { get; set; } = "";
    public DateTime? ReleaseDate { get; set; }
    public int RuntimeMinutes { get; set; }
    public double Score { get; set; }
    public int ScoreVotes { get; set; }
    public bool HasMagnet { get; set; }
    public bool HasChineseSubtitle { get; set; }
    public CensorType CensorType { get; set; }
    public string CoverUrl { get; set; } = "";
    public List<string> PreviewImageUrls { get; set; } = [];
    public List<string> RelatedNumbers { get; set; } = [];
    public List<string> Reviews { get; set; } = [];
    public List<string> PreviewSourceUrls { get; set; } = [];
    public List<string> SeriesNumbers { get; set; } = [];
    public string SeriesUrl { get; set; } = "";
    public Dictionary<string, string> SourceUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface IVideoScraper
{
    string Id { get; }
    string DisplayName { get; set; }
    bool Supports(VideoNumberKind kind);
    Task<VideoScrapeMetadata?> SearchAsync(string number, CancellationToken ct);
}

public sealed class VideoScrapeSettings
{
    public bool Enabled { get; set; } = true;
    public string Proxy { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 12;
    public int Retry { get; set; } = 3;
    public int Concurrency { get; set; } = 2;
    public int RequestIntervalMs { get; set; } = 1000;
    public bool AutoScrapeNewFiles { get; set; } = true;
    public bool DownloadExtraFanart { get; set; }
    public bool WriteNfo { get; set; } = true;
    public string JavBusBaseUrl { get; set; } = "https://www.javbus.com";
    public string JavBusCookie { get; set; } = "";
    public string JavDbBaseUrl { get; set; } = "https://javdb.com";
    public string JavDbCookie { get; set; } = "";
    [JsonPropertyName("airav")] public bool AiravEnabled { get; set; } = true;
}
