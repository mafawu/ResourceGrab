using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Models;

/// <summary>漫画用户数据（阅读进度 / 评分 / 备注），按漫画路径索引，持久化到 comic-user-data.json。</summary>
public class ComicUserData
{
    [JsonPropertyName("readImageCount")]
    public int ReadImageCount { get; set; }

    [JsonPropertyName("totalImageCount")]
    public int TotalImageCount { get; set; }

    [JsonPropertyName("readProgressPercent")]
    public double ReadProgressPercent { get; set; }

    [JsonPropertyName("firstReadAt")]
    public string? FirstReadAt { get; set; }

    [JsonPropertyName("lastReadAt")]
    public string? LastReadAt { get; set; }

    [JsonPropertyName("readCount")]
    public int ReadCount { get; set; }

    [JsonPropertyName("rating")]
    public int Rating { get; set; }

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";
}