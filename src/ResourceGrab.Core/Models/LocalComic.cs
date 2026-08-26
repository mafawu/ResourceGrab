using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Models;

/// <summary>本地漫画元数据（保存于专辑目录 album.json，供本地模式离线展示）。</summary>
public class AlbumMetadata
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("nameCn")] public string NameCn { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("author")] public List<string> Author { get; set; } = new();
    [JsonPropertyName("works")] public List<string> Works { get; set; } = new();
    [JsonPropertyName("actors")] public List<string> Actors { get; set; } = new();
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("addtime")] public string Addtime { get; set; } = "";
    [JsonPropertyName("total_views")] public string TotalViews { get; set; } = "";
    [JsonPropertyName("likes")] public string Likes { get; set; } = "";
    [JsonPropertyName("comment_total")] public string CommentTotal { get; set; } = "";
    [JsonPropertyName("series_id")] public string SeriesId { get; set; } = "";
    [JsonPropertyName("series")] public List<SeriesRespData> Series { get; set; } = new();
    [JsonPropertyName("chapterInfos")] public List<ChapterInfo> ChapterInfos { get; set; } = new();
    [JsonPropertyName("related_list")] public List<RelatedListRespData> RelatedList { get; set; } = new();
    [JsonPropertyName("liked")] public bool Liked { get; set; }
    [JsonPropertyName("is_favorite")] public bool IsFavorite { get; set; }
    [JsonPropertyName("is_aids")] public bool IsAids { get; set; }
}

/// <summary>本地模式下扫描到的漫画（来自本地目录，可离线展示）。</summary>
public class LocalComic
{
    /// <summary>所属内容源 id（如 "wnacg"）；空表示禁漫。旧版禁漫下载无此字段，扫描时回退为禁漫。</summary>
    public string SourceId { get; init; } = "";

    public long? AlbumId { get; init; }
    public string Name { get; init; } = "";
    /// <summary>中文名：优先来自元数据，否则扫描时提取/翻译，用于搜索与展示。</summary>
    public string NameCn { get; set; } = "";
    public string Path { get; init; } = "";
    public string CoverPath { get; init; } = "";
    public List<string> Tags { get; init; } = new();
    public List<string> Author { get; init; } = new();
    public int ChapterCount { get; init; }
    public long ImageCount { get; init; }
    public DateTime ModifiedAt { get; init; }

    /// <summary>元数据文件（album.json / 元数据.json）最后修改时间，用于增量扫描判断元数据是否变化。</summary>
    public DateTime? MetadataStamp { get; init; }
    public bool HasMetadata { get; init; }
    public string TotalViews { get; init; } = "";
    public string Likes { get; init; } = "";
    public string CommentTotal { get; init; } = "";
    // ====================== 用户数据（阅读进度 / 评分 / 备注）======================

    /// <summary>已读图片数（跨章节累计），由阅读器在保存进度时更新。</summary>
    [JsonPropertyName("readImageCount")]
    public int ReadImageCount { get; set; }

    /// <summary>总图片数（所有章节图片之和），用于计算阅读进度百分比。</summary>
    [JsonPropertyName("totalImageCount")]
    public int TotalImageCount { get; set; }

    /// <summary>阅读进度百分比 0-100。</summary>
    [JsonPropertyName("readProgress")]
    public double ReadProgress => TotalImageCount > 0 ? Math.Clamp(100.0 * ReadImageCount / TotalImageCount, 0, 100) : 0;

    /// <summary>首次阅读时间（ISO 8601）。</summary>
    [JsonPropertyName("firstReadAt")]
    public string? FirstReadAt { get; set; }

    /// <summary>最后阅读时间（ISO 8601）。</summary>
    [JsonPropertyName("lastReadAt")]
    public string? LastReadAt { get; set; }

    /// <summary>阅读次数（每次打开阅读器 +1）。</summary>
    [JsonPropertyName("readCount")]
    public int ReadCount { get; set; }

    /// <summary>用户评分（1-5 星，0 表示未评分）。</summary>
    [JsonPropertyName("rating")]
    public int Rating { get; set; }

    /// <summary>用户备注。</summary>
    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";
}

/// <summary>本地漫画库磁盘缓存：按根目录分组保存扫描结果，供增量扫描复用。</summary>
public class LocalLibraryCache
{
    /// <summary>缓存写入时间。</summary>
    public DateTime SavedAt { get; set; }

    /// <summary>根目录（绝对路径）→ 该目录下已扫描到的漫画列表。</summary>
    public Dictionary<string, List<LocalComic>> Roots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
