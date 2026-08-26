using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Models;

public class NovelReadingProgress
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("page")] public int Page { get; set; } = 1;
    [JsonPropertyName("pageCount")] public int PageCount { get; set; } = 1;
    [JsonPropertyName("charsPerPage")] public int CharsPerPage { get; set; } = 1000;
    [JsonPropertyName("fontSize")] public double FontSize { get; set; } = 14;
    [JsonPropertyName("totalChars")] public int TotalChars { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.Now;
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    public double Progress => PageCount <= 1 ? 0 : (double)Page / PageCount;
    public string ProgressText => PageCount <= 1 ? "未读" : $"{Page}/{PageCount} ({(int)(Progress*100)}%)";
}

public class NovelHistoryFile
{
    [JsonPropertyName("items")] public Dictionary<string, NovelReadingProgress> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
