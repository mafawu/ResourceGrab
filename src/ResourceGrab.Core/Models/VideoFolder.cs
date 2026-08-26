using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Models;

/// <summary>本地视频文件夹（对应 GreenResourcesManager 的 VideoFolder）。</summary>
public class VideoFolder
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("folderPath")]
    public string FolderPath { get; set; } = "";

    [JsonPropertyName("series")]
    public string Series { get; set; } = "";

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("actors")]
    public List<string> Actors { get; set; } = new();

    [JsonPropertyName("voiceActors")]
    public List<string> VoiceActors { get; set; } = new();

    [JsonPropertyName("productionTeam")]
    public List<string> ProductionTeam { get; set; } = new();

    [JsonPropertyName("director")]
    public string Director { get; set; } = "";

    [JsonPropertyName("addedDate")]
    public DateTime AddedDate { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("files")]
    public List<VideoFile> Files { get; set; } = new();

    // ===== 用户数据 =====

    [JsonPropertyName("watchProgress")]
    public double WatchProgress { get; set; }

    [JsonPropertyName("watchCount")]
    public int WatchCount { get; set; }

    [JsonPropertyName("lastWatchedAt")]
    public string? LastWatchedAt { get; set; }

    [JsonPropertyName("rating")]
    public int Rating { get; set; }

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    [JsonPropertyName("isFavorite")]
    public bool IsFavorite { get; set; }

    /// <summary>文件夹目录是否仍然存在。</summary>
    [JsonIgnore]
    public bool FileExists => string.IsNullOrEmpty(FolderPath) || Directory.Exists(FolderPath);

    /// <summary>已登记但磁盘上已丢失的视频文件数量。</summary>
    [JsonIgnore]
    public int MissingFileCount => Files.Count(f => !f.FileExists);

    /// <summary>文件夹中所有视频的总大小（字节）。</summary>
    [JsonIgnore]
    public long TotalSizeBytes => Files.Sum(f => f.FileSizeBytes);

    /// <summary>格式化的总大小。</summary>
    [JsonIgnore]
    public string TotalSizeText
    {
        get
        {
            if (TotalSizeBytes < 1024 * 1024) return $"{TotalSizeBytes / 1024.0:F0} KB";
            if (TotalSizeBytes < 1024L * 1024 * 1024) return $"{TotalSizeBytes / 1024.0 / 1024.0:F1} MB";
            return $"{TotalSizeBytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
        }
    }

    /// <summary>全部视频总时长（秒）。</summary>
    [JsonIgnore]
    public double TotalDurationSeconds => Files.Sum(f => f.DurationSeconds);

    /// <summary>格式化的总时长，如 "1:23:45"；无时长数据时返回空串。</summary>
    [JsonIgnore]
    public string TotalDurationText => FormatDuration(TotalDurationSeconds);

    /// <summary>封面：取第一个有缩略图的文件。</summary>
    [JsonIgnore]
    public string CoverPath => Files.FirstOrDefault(f => !string.IsNullOrEmpty(f.ThumbnailPath))?.ThumbnailPath ?? "";

    /// <summary>把秒数格式化为 "s" / "m:ss" / "h:mm:ss"；小于等于 0 返回空串。</summary>
    public static string FormatDuration(double seconds)
    {
        if (seconds <= 0)
        {
            return "";
        }
        var t = (long)seconds;
        return seconds switch
        {
            < 60 => $"{seconds:F0}s",
            < 3600 => $"{t / 60}:{t % 60:D2}",
            _ => $"{t / 3600}:{t % 3600 / 60:D2}:{t % 60:D2}",
        };
    }
}

/// <summary>单个视频文件。</summary>
public class VideoFile
{
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = "";

    [JsonPropertyName("fileSizeBytes")]
    public long FileSizeBytes { get; set; }

    [JsonPropertyName("durationSeconds")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = "";

    [JsonPropertyName("thumbnailPath")]
    public string? ThumbnailPath { get; set; }

    [JsonPropertyName("watchProgress")]
    public double WatchProgress { get; set; }

    [JsonPropertyName("lastWatchedAt")]
    public string? LastWatchedAt { get; set; }

    /// <summary>文件是否仍然存在于磁盘。</summary>
    [JsonIgnore]
    public bool FileExists => !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath);

    /// <summary>格式化时长角标文本，如 "12:34"；无数据返回空串。</summary>
    [JsonIgnore]
    public string DurationText => VideoFolder.FormatDuration(DurationSeconds);
}


public enum CensorType
{
    Unknown = 0,
    Censored,
    Uncensored,
    Chinese,
    Western,
}

public enum ScrapeStatus
{
    Pending = 0,
    Success,
    NoMatch,
    Failed,
    Skipped,
}

/// <summary>视频库主实体；一个本地视频文件一条记录。</summary>
public class VideoItem
{
    public string Id { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public double DurationSeconds { get; set; }
    public string Resolution { get; set; } = "";
    public string? ThumbnailPath { get; set; }
    public DateTime AddedDate { get; set; } = DateTime.UtcNow;

    public string Number { get; set; } = "";
    public string Part { get; set; } = "";
    public string Title { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Actors { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public string Series { get; set; } = "";
    public string Studio { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Director { get; set; } = "";
    public DateTime? ReleaseDate { get; set; }
    public double Score { get; set; }
    public CensorType CensorType { get; set; }
    public int ScoreVotes { get; set; }
    public bool HasMagnet { get; set; }
    public bool HasChineseSubtitle { get; set; }
    public string CoverPath { get; set; } = "";
    public string PosterPath { get; set; } = "";
    public List<string> PreviewImages { get; set; } = new();
    public List<string> PreviewSourceUrls { get; set; } = new();
    public int PreviewTotalCount { get; set; }

    /// <summary>详情页「同類影片」推荐的相关番号（刮削时抓取）。</summary>
    public List<string> RelatedNumbers { get; set; } = new();

    /// <summary>同一系列的番号列表（按来源页顺序）。</summary>
    public List<string> SeriesNumbers { get; set; } = new();

    /// <summary>外部详情页地址，键为刮削源 ID。</summary>
    public Dictionary<string, string> SourceUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>JavDB 用户短评（刮削时抓取）。</summary>
    public List<string> Reviews { get; set; } = new();

    public bool IsFavorite { get; set; }
    public int UserRating { get; set; }
    public double WatchProgress { get; set; }
    public int WatchCount { get; set; }
    public DateTime? LastWatchedAt { get; set; }
    public int OpenCount { get; set; }
    public DateTime? LastOpenedAt { get; set; }
    public List<string> UserTags { get; set; } = new();

    public ScrapeStatus ScrapeStatus { get; set; } = ScrapeStatus.Pending;
    public string ScrapeSource { get; set; } = "";
    public DateTime? ScrapedAt { get; set; }

    [JsonIgnore] public bool FileExists => !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath);

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
        ? (string.IsNullOrWhiteSpace(FileName) ? "(未命名)" : FileName)
        : Title;

    [JsonIgnore] public string DurationText => VideoFolder.FormatDuration(DurationSeconds);

    /// <summary>路径 SHA256 前 16 位；移动文件后可基于新路径重建。</summary>
    public static string CreateId(string filePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(filePath));
        return Convert.ToHexString(bytes)[..16];
    }
}
