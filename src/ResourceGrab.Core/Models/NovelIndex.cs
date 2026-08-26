using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Models;

public class NovelIndexFile
{
    [JsonPropertyName("root")] public string Root { get; set; } = "";
    [JsonPropertyName("generated")] public string Generated { get; set; } = "";
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("structure")] public List<string> Structure { get; set; } = new();
    [JsonPropertyName("files")] public List<NovelIndexEntry> Files { get; set; } = new();
}

public class NovelIndexEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("primaryTag")] public string PrimaryTag { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("relativePath")] public string RelativePath { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public class NovelResource
{
    public string Id => RelativePath;
    public string Name { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string PrimaryTag { get; init; } = "";
    public List<string> Tags { get; init; } = new();
    public long Size { get; init; }
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Name);
    public string NormalizedPrimary => PrimaryTag.Replace("\\","/").Replace("//","/");
}
