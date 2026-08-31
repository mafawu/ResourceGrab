using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Tests.Sources;

/// <summary>
/// 测试用 IResilientFetcher：按 URL 字典返回预置 body（200）。
/// 未命中的 URL 返回 404；可配置整站 403 以测试 Blocked 路径。
/// 所有源实现测试共享，禁止在测试里访问真实网络。
/// </summary>
internal sealed class FakeFetcher : IResilientFetcher
{
    private readonly Dictionary<string, string> _pages;
    private readonly int? _forceStatus;

    /// <summary>记录按顺序收到的请求 URL，供断言请求次数/顺序。</summary>
    public List<string> Requested { get; } = [];

    public FakeFetcher(Dictionary<string, string> pages, int? forceStatus = null)
    {
        _pages = pages;
        _forceStatus = forceStatus;
    }

    public Task<FetchResult> GetAsync(string url, FetchOptions? options, CancellationToken ct)
    {
        Requested.Add(url);
        if (_forceStatus is { } status)
            return Task.FromResult(new FetchResult(status, "<html><title>Just a moment...</title></html>", FetchTier.HttpClient));
        return Task.FromResult(_pages.TryGetValue(url, out var body)
            ? new FetchResult(200, body, FetchTier.HttpClient)
            : new FetchResult(404, "not found", FetchTier.HttpClient));
    }

    /// <summary>POST 与 GET 同表驱动；预置字典的 key 直接用完整 URL。</summary>
    public Task<FetchResult> PostAsync(string url, string contentType, string body, FetchOptions? options, CancellationToken ct)
        => GetAsync(url, options, ct);
}
