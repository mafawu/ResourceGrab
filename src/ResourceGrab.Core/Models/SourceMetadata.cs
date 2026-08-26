using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Models;

/// <summary>
/// 通用来源元数据（source.json，各非禁漫源下载时写入）。
/// 供本地库识别来源与站点 id（"已下载"徽章、离线展示），与禁漫专属的 album.json 互不影响。
/// </summary>
public class SourceMetadata
{
    /// <summary>内容源 id（如 wnacg / hitomi）；空表示禁漫。</summary>
    [JsonPropertyName("source_id")] public string SourceId { get; set; } = "";

    /// <summary>站点侧漫画 id（字符串形式）。</summary>
    [JsonPropertyName("comic_id")] public string ComicId { get; set; } = "";

    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("authors")] public List<string> Authors { get; set; } = new();
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("cover_url")] public string CoverUrl { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
}
