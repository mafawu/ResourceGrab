using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M12: 插件 manifest 定义
// ---------------------------------------------------------------------------

/// <summary>外部来源插件清单。</summary>
public sealed class VideoScrapePluginManifest
{
    /// <summary>插件 ID，必须带命名空间前缀。</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0.0";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    /// <summary>支持的内容类型。</summary>
    [JsonPropertyName("supportedKinds")]
    public List<string> SupportedKinds { get; init; } = [];

    /// <summary>插件程序集路径（相对于插件目录）。</summary>
    [JsonPropertyName("entryPoint")]
    public required string EntryPoint { get; init; }

    /// <summary>实现 IVideoScrapeSource 的完整类型名。</summary>
    [JsonPropertyName("typeName")]
    public required string TypeName { get; init; }

    /// <summary>是否默认启用。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}

/// <summary>插件加载状态。</summary>
public enum PluginLoadStatus
{
    NotLoaded,
    Loaded,
    Failed,
    Disabled
}

/// <summary>插件实例描述。</summary>
public sealed class LoadedPlugin
{
    public required VideoScrapePluginManifest Manifest { get; init; }
    public required PluginLoadStatus Status { get; set; }
    public IVideoScrapeSource? Source { get; set; }
    public string? ErrorMessage { get; set; }
}
