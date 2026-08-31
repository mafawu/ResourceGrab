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

    // —— 阶段0 新增：反爬与代理的 per-source 配置（全部可选，缺省回退全局） ——

    /// <summary>备用镜像域名/基础地址列表（主域名被拦时按序轮换）。</summary>
    [JsonPropertyName("mirrorUrls")] public List<string> MirrorUrls { get; set; } = [];

    /// <summary>该源专属代理，空则回退全局 VideoScraping.Proxy（不同源可走不同出口）。</summary>
    [JsonPropertyName("proxyOverride")] public string ProxyOverride { get; set; } = "";

    /// <summary>true 时优先走 curl-impersonate（Chrome TLS 指纹层 L2）。</summary>
    [JsonPropertyName("preferImpersonate")] public bool PreferImpersonate { get; set; }

    /// <summary>该源专属请求头（Referer/Cookie 等站点约定）。</summary>
    [JsonPropertyName("extraHeaders")] public Dictionary<string, string> ExtraHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 源性能分级（波次调度）：1=快且稳（首批 2-3 个），2=中等，3=慢或受限。
    /// 首批命中即停，全部未命中才继续下一批。
    /// </summary>
    [JsonPropertyName("tier")] public int Tier { get; set; } = 2;
}

// ---------------------------------------------------------------------------
// M2: 扩展后的配置 — 嵌入 VideoScrapeSettings
// ---------------------------------------------------------------------------

public sealed class VideoScrapeAdvancedSettings
{
    /// <summary>刮削引擎："graph"（图调度多源择优）| "legacy"（老 VideoScrapeService 三源管道）。配置非法时回退 legacy。</summary>
    [JsonPropertyName("engine")] public string Engine { get; set; } = "graph";

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
        // missav 在每条路由首位（Tier 1 首批必含）：覆盖全部内容类型、快、免登录
        new() { Kind = VideoContentKind.Censored, Sources = ["missav", "javbus", "javdb", "dmm", "official", "javlibrary", "jav321", "dahlia", "faleno", "xcity", "giga"] },
        new() { Kind = VideoContentKind.Uncensored, Sources = ["missav", "javbus", "javdb", "avsox", "freejavbt", "kin8"] },
        new() { Kind = VideoContentKind.Fc2, Sources = ["missav", "javdb", "fc2ppvdb", "fc2", "freejavbt", "fc2club"] },
        new() { Kind = VideoContentKind.Chinese, Sources = ["missav", "iqqtv", "javdb", "airav", "freejavbt"] },
        new() { Kind = VideoContentKind.Amateur, Sources = ["missav", "mgstage", "dmm", "javdb", "javbus"] },
        new() { Kind = VideoContentKind.Western, Sources = ["missav", "theporndb", "javdb", "freejavbt"] },
        new() { Kind = VideoContentKind.Hentai, Sources = ["missav", "getchu", "dmm", "javdb"] },
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
            // Tier 分级依据实测访问性能：1=快且稳（首批 2-3 个并发），2=中等，3=慢或地理受限
            ["javbus"] = new() { Enabled = true, BaseUrl = "https://www.javbus.com", RateLimitPerSecond = 1, Tier = 1 },
            ["javdb"] = new() { Enabled = true, BaseUrl = "https://javdb.com", Tier = 1 },
            ["airav"] = new() { Enabled = true, UseCurlFallback = true, Tier = 2 },
            ["dmm"] = new() { Enabled = true, BaseUrl = "https://www.dmm.co.jp", Tier = 3 },
            ["iqqtv"] = new() { Enabled = true, Language = "zh_cn", Tier = 2 },
            ["avsox"] = new() { Enabled = true, Tier = 2 },
            ["freejavbt"] = new() { Enabled = true, Tier = 2 },
            ["fc2ppvdb"] = new() { Enabled = true, Tier = 2 },
            ["fc2"] = new() { Enabled = true, Tier = 3 },
            ["fc2club"] = new() { Enabled = true, Tier = 3 },
            ["mgstage"] = new() { Enabled = true, Tier = 3 },
            ["theporndb"] = new() { Enabled = false, Tier = 2 },   // 需用户配置 apiKey 后启用（Western 内容链首）
            ["getchu"] = new() { Enabled = true, Tier = 3 },
            ["official"] = new() { Enabled = false, Tier = 3 },    // 未实现（厂牌官网路由规则库待建）
            ["prestige"] = new() { Enabled = true, Tier = 3 },     // 日本 IP 限定：需用户配 proxyOverride 指向日本出口
            ["r18dev"] = new() { Enabled = false, Tier = 3 },      // 未实现（离线 PostgreSQL 镜像查询，后续专项）
            // —— 阶段2 新实现并默认启用的源 ——
            ["javlibrary"] = new() { Enabled = true, BaseUrl = "https://www.javlibrary.com/cn", Tier = 2 },
            ["jav321"] = new() { Enabled = true, BaseUrl = "https://www.jav321.com", Tier = 2 },
            ["kin8"] = new() { Enabled = true, BaseUrl = "https://www.kin8tengoku.com", Tier = 3 },
            ["xcity"] = new() { Enabled = true, Tier = 3 },
            ["giga"] = new() { Enabled = true, Tier = 3 },
            ["dahlia"] = new() { Enabled = true, Tier = 2 },
            ["faleno"] = new() { Enabled = true, Tier = 2 },
            // —— MissAV 元数据源（Tier 1 首批必含，用户指定）——
            ["missav"] = new() { Enabled = true, BaseUrl = "https://missav.ws", Tier = 1 },
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
