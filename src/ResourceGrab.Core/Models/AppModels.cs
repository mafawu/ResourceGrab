using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Models;

/// <summary>UI 使用的漫画模型（含章节列表与下载状态）。</summary>
public class Album
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("addtime")] public string Addtime { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("total_views")] public string TotalViews { get; set; } = "";
    [JsonPropertyName("likes")] public string Likes { get; set; } = "";
    [JsonPropertyName("chapterInfos")] public List<ChapterInfo> ChapterInfos { get; set; } = new();
    [JsonPropertyName("series_id")] public string SeriesId { get; set; } = "";
    [JsonPropertyName("series")] public List<SeriesRespData> Series { get; set; } = new();
    [JsonPropertyName("comment_total")] public string CommentTotal { get; set; } = "";
    [JsonPropertyName("author")] public List<string> Author { get; set; } = new();
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("works")] public List<string> Works { get; set; } = new();
    [JsonPropertyName("actors")] public List<string> Actors { get; set; } = new();
    [JsonPropertyName("related_list")] public List<RelatedListRespData> RelatedList { get; set; } = new();
    [JsonPropertyName("liked")] public bool Liked { get; set; }
    [JsonPropertyName("is_favorite")] public bool IsFavorite { get; set; }
    [JsonPropertyName("is_aids")] public bool IsAids { get; set; }
}

public class ChapterInfo
{
    [JsonPropertyName("chapterId")] public long ChapterId { get; set; }
    [JsonPropertyName("chapterTitle")] public string ChapterTitle { get; set; } = "";
    [JsonPropertyName("albumId")] public long AlbumId { get; set; }
    [JsonPropertyName("albumTitle")] public string AlbumTitle { get; set; } = "";
    [JsonPropertyName("isDownloaded")] public bool IsDownloaded { get; set; }
}

/// <summary>搜索结果的两种形态：列表，或命中唯一漫画时直接返回专辑。</summary>
public class SearchResp
{
    public SearchRespData? SearchRespData { get; set; }
    public AlbumRespData? AlbumRespData { get; set; }
    public bool IsAlbum => AlbumRespData is not null;
}
