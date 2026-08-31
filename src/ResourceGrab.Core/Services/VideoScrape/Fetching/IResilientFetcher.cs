namespace ResourceGrab.Core.Services.VideoScrape.Fetching;

// ---------------------------------------------------------------------------
// 阶段0: 多级抓取栈的统一抽象。
// 设计参考 amane（github.com/sqzw-x/amane）crawlers/http.py 的双客户端思路：
// L1 = .NET HttpClient（常规），L2 = curl-impersonate（浏览器 TLS 指纹），
// L3 = 浏览器渲染 / FlareSolverr（预留，暂未实现）。
// ---------------------------------------------------------------------------

public enum FetchTier
{
    /// <summary>.NET HttpClient（常规通道）。</summary>
    HttpClient = 0,
    /// <summary>curl-impersonate 子进程（Chrome TLS 指纹，过 Cloudflare 被动检测）。</summary>
    CurlImpersonate = 1,
    /// <summary>浏览器渲染兜底（预留）。</summary>
    FlareSolverr = 2,
}

public sealed record FetchOptions
{
    /// <summary>调用方源 ID：per-source 的客户端/代理/限速隔离键；未知来源可留空。</summary>
    public string? SourceId { get; init; }

    public string? Referer { get; init; }

    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }

    /// <summary>允许降级的最高层级（默认允许到 L2 curl-impersonate）。</summary>
    public FetchTier MaxTier { get; init; } = FetchTier.CurlImpersonate;
}

public sealed record FetchResult(int StatusCode, string Body, FetchTier Tier);

public interface IResilientFetcher
{
    /// <summary>
    /// 按层级顺序抓取：命中 BlockDetector 即向下一级降级重试；
    /// 非拦截类 HTTP 错误（404 等）立即抛出；网络层失败逐级尝试后抛最后一个异常。
    /// </summary>
    Task<FetchResult> GetAsync(string url, FetchOptions? options, CancellationToken ct);

    /// <summary>POST 变体（DMM GraphQL 等 API 端点用），降级语义与 GetAsync 一致。</summary>
    Task<FetchResult> PostAsync(string url, string contentType, string body, FetchOptions? options, CancellationToken ct);
}
