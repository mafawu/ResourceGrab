using System.Text.Json.Serialization;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M2: 扩展 VideoScrapeSettings — 内容路由、字段优先级、来源启用配置
// ---------------------------------------------------------------------------

public static class VideoScrapeConfigExtensions
{
    /// <summary>将现有 VideoNumberKind 映射为新的 VideoContentKind。</summary>
    public static VideoContentKind ToContentKind(this VideoNumberKind kind) => kind switch
    {
        VideoNumberKind.Normal => VideoContentKind.Censored,
        VideoNumberKind.Fc2 => VideoContentKind.Fc2,
        VideoNumberKind.Uncensored => VideoContentKind.Uncensored,
        _ => VideoContentKind.Unknown
    };
}

// ---------------------------------------------------------------------------
// M2: 内容路由配置 — 每种内容类型对应的来源优先顺序
// ---------------------------------------------------------------------------

public sealed class ContentRouteEntry
{
    [JsonPropertyName("kind")] public VideoContentKind Kind { get; set; }
    [JsonPropertyName("sources")] public List<string> Sources { get; set; } = [];
}

// ---------------------------------------------------------------------------
// M2: 字段优先级配置 — 每个字段在不同内容类型下的来源顺序
// ---------------------------------------------------------------------------

public sealed class FieldPriorityEntry
{
    [JsonPropertyName("field")] public string Field { get; set; } = "";
    /// <summary>默认来源顺序（适用于所有内容类型）。</summary>
    [JsonPropertyName("defaultSources")] public List<string> DefaultSources { get; set; } = [];
    /// <summary>按内容类型覆盖的来源顺序。key = VideoContentKind 名称。</summary>
    [JsonPropertyName("kindOverrides")] public Dictionary<string, List<string>> KindOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

// ---------------------------------------------------------------------------
// M2: 单个来源配置
// ---------------------------------------------------------------------------

public sealed class VideoSourceConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "";
    [JsonPropertyName("cookie")] public string Cookie { get; set; } = "";
    [JsonPropertyName("rateLimitPerSecond")] public double RateLimitPerSecond { get; set; } = 1;
    [JsonPropertyName("useCurlFallback")] public bool UseCurlFallback { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "";
    [JsonPropertyName("dsn")] public string Dsn { get; set; } = "";
}

// ---------------------------------------------------------------------------
// M2: 扩展后的配置 — 嵌入 VideoScrapeSettings
// ---------------------------------------------------------------------------

public sealed class VideoScrapeAdvancedSettings
{
    [JsonPropertyName("contentRoutes")] public List<ContentRouteEntry> ContentRoutes { get; set; } = GetDefaultContentRoutes();
    [JsonPropertyName("fieldPriorities")] public List<FieldPriorityEntry> FieldPriorities { get; set; } = GetDefaultFieldPriorities();
    [JsonPropertyName("sourceConfigs")] public Dictionary<string, VideoSourceConfig> SourceConfigs { get; set; } = GetDefaultSourceConfigs();

    /// <summary>根据内容类型获取来源顺序。</summary>
    public IReadOnlyList<string> GetSourcesForKind(VideoContentKind kind) =>
        ContentRoutes.FirstOrDefault(r => r.Kind == kind)?.Sources ?? [];

    /// <summary>获取指定字段在指定内容类型下的来源优先链。</summary>
    public IReadOnlyList<string> GetFieldPriority(string field, VideoContentKind kind)
    {
        var entry = FieldPriorities.FirstOrDefault(f =>
            string.Equals(f.Field, field, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return [];

        if (entry.KindOverrides.TryGetValue(kind.ToString(), out var overrideSources) && overrideSources.Count > 0)
            return overrideSources;

        return entry.DefaultSources;
    }

    /// <summary>获取指定来源是否已启用。</summary>
    public bool IsSourceEnabled(string sourceId) =>
        SourceConfigs.TryGetValue(sourceId, out var cfg) && cfg.Enabled;

    // -----------------------------------------------------------------------
    // 默认值
    // -----------------------------------------------------------------------

    private static List<ContentRouteEntry> GetDefaultContentRoutes() =>
    [
        new() { Kind = VideoContentKind.Censored, Sources = ["dmm", "javdb", "javbus", "official"] },
        new() { Kind = VideoContentKind.Uncensored, Sources = ["javdb", "javbus", "avsox", "freejavbt"] },
        new() { Kind = VideoContentKind.Fc2, Sources = ["javdb", "fc2ppvdb", "fc2", "freejavbt"] },
        new() { Kind = VideoContentKind.Chinese, Sources = ["iqqtv", "javdb", "airav", "freejavbt"] },
        new() { Kind = VideoContentKind.Amateur, Sources = ["mgstage", "dmm", "javdb", "javbus"] },
        new() { Kind = VideoContentKind.Western, Sources = ["theporndb", "javdb", "freejavbt"] },
        new() { Kind = VideoContentKind.Hentai, Sources = ["getchu", "dmm", "javdb"] },
    ];

    private static List<FieldPriorityEntry> GetDefaultFieldPriorities() =>
    [
        new() { Field = "title", DefaultSources = ["dmm", "javdb"], KindOverrides = { ["chinese"] = ["iqqtv", "airav", "javdb"] } },
        new() { Field = "description", DefaultSources = ["airav", "dmm", "javdb"], KindOverrides = { ["censored"] = ["dmm"] } },
        new() { Field = "coverUrl", DefaultSources = ["dmm", "javbus", "official"] },
        new() { Field = "actors", DefaultSources = ["javbus", "javdb"] },
        new() { Field = "tags", DefaultSources = ["javbus", "javdb"] },
        new() { Field = "score", DefaultSources = ["javdb"] },
        new() { Field = "originalTitle", DefaultSources = ["dmm"] },
        new() { Field = "previewImageUrls", DefaultSources = ["javbus", "dmm", "official"] },
    ];

    private static Dictionary<string, VideoSourceConfig> GetDefaultSourceConfigs() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["javbus"] = new() { Enabled = true, BaseUrl = "https://www.javbus.com", RateLimitPerSecond = 1 },
            ["javdb"] = new() { Enabled = true, BaseUrl = "https://javdb.com" },
            ["airav"] = new() { Enabled = true, UseCurlFallback = true },
            ["dmm"] = new() { Enabled = false },
            ["iqqtv"] = new() { Enabled = false, Language = "zh_cn" },
            ["avsox"] = new() { Enabled = false },
            ["freejavbt"] = new() { Enabled = false },
            ["fc2ppvdb"] = new() { Enabled = false },
            ["fc2"] = new() { Enabled = false },
            ["mgstage"] = new() { Enabled = false },
            ["theporndb"] = new() { Enabled = false },
            ["getchu"] = new() { Enabled = false },
            ["official"] = new() { Enabled = false },
            ["prestige"] = new() { Enabled = false },
            ["r18dev"] = new() { Enabled = false },
        };
}

// ---------------------------------------------------------------------------
// M2: 配置校验
// ---------------------------------------------------------------------------

public sealed record ConfigValidationResult(bool IsValid, List<string> Warnings, List<string> Errors)
{
    public static ConfigValidationResult Ok() => new(true, [], []);
    public static ConfigValidationResult Fail(params string[] errors) => new(false, [], [.. errors]);
}

public static class VideoScrapeConfigValidator
{
    public static ConfigValidationResult Validate(
        VideoScrapeAdvancedSettings config,
        IVideoScrapeSourceRegistry registry)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var registeredIds = new HashSet<string>(
            registry.Sources.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);

        // 1. 路由中的来源必须存在
        foreach (var route in config.ContentRoutes)
        {
            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in route.Sources)
            {
                if (!registeredIds.Contains(src))
                    warnings.Add($"路由 {route.Kind}: 来源 '{src}' 未注册，已忽略");
                if (!distinct.Add(src))
                    warnings.Add($"路由 {route.Kind}: 来源 '{src}' 重复");
            }
        }

        // 2. 字段优先级中的来源必须在路由内
        foreach (var fp in config.FieldPriorities)
        {
            foreach (var src in fp.DefaultSources)
            {
                if (!registeredIds.Contains(src))
                    warnings.Add($"字段 {fp.Field} 优先级中的来源 '{src}' 未注册");
            }
        }

        return errors.Count > 0
            ? ConfigValidationResult.Fail([.. errors])
            : new ConfigValidationResult(true, warnings, []);
    }
}
