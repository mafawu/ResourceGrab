using System.Collections.Concurrent;
using System.Net;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape.Fetching;

// ---------------------------------------------------------------------------
// 阶段0: 多级抓取栈实现。
// L1 HttpClient（带 per-source 代理/超时）→ L2 curl-impersonate（Chrome TLS 指纹）。
// 响应命中 BlockDetector 即向下一级降级；非拦截类 HTTP 错误（404 等）不换层直接抛出；
// 网络层失败逐级尝试后抛最后一个异常。
// per-source 隔离：同一 SourceId 共享一个 HttpClient、一个并发信号量与一套 host 限速车道；
// Cookie 经 DomainCookieJar 带上/回写，cf_clearance 类放行 Cookie 全站复用。
// ---------------------------------------------------------------------------

public sealed class MultiTierFetcher : IResilientFetcher
{
    private sealed class SourceLane
    {
        public required HttpClient Client { get; init; }
        public required SemaphoreSlim Gate { get; init; }
        public required int IntervalMs { get; init; }
        public string? Proxy { get; init; }
        public readonly ConcurrentDictionary<string, DateTime> NextByHost = new();
    }

    private readonly IVideoHttpClientFactory _httpFactory;
    private readonly VideoScrapeAdvancedSettings _advanced;
    private readonly VideoScrapeSettings _settings;
    private readonly CurlImpersonateClient _l2;
    private readonly DomainCookieJar _cookies;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, SourceLane> _lanes = new(StringComparer.OrdinalIgnoreCase);

    public MultiTierFetcher(
        IVideoHttpClientFactory httpFactory,
        VideoScrapeAdvancedSettings advanced,
        VideoScrapeSettings settings,
        CurlImpersonateClient l2,
        DomainCookieJar cookies,
        ILogger? logger = null)
    {
        _httpFactory = httpFactory;
        _advanced = advanced;
        _settings = settings;
        _l2 = l2;
        _cookies = cookies;
        _logger = logger;
    }

    public async Task<FetchResult> GetAsync(string url, FetchOptions? options, CancellationToken ct)
    {
        options ??= new FetchOptions();
        var sourceId = string.IsNullOrEmpty(options.SourceId) ? "default" : options.SourceId;
        var lane = _lanes.GetOrAdd(sourceId, id => CreateLane(id));
        Exception? last = null;

        for (var tier = FetchTier.HttpClient; tier <= options.MaxTier; tier++)
        {
            // L2 未就绪（用户没放 curl-impersonate 二进制）则透明跳过
            if (tier == FetchTier.CurlImpersonate && !_l2.Available) continue;

            try
            {
                var result = tier switch
                {
                    FetchTier.HttpClient => await GetWithHttpClientAsync(lane, url, options, ct),
                    FetchTier.CurlImpersonate => await GetWithCurlAsync(lane, url, options, ct),
                    _ => throw new HttpRequestException($"层级 {tier} 未实现"),
                };

                if (BlockDetector.IsBlocked(result.StatusCode, result.Body))
                {
                    last = new HttpRequestException($"被反爬拦截 (HTTP {result.StatusCode})", null,
                        (HttpStatusCode)result.StatusCode);
                    _logger?.Warn($"[Fetcher] {sourceId} L{(int)tier + 1} 被拦截(HTTP {result.StatusCode})，尝试下一层: {url}");
                    continue;
                }

                if (result.StatusCode is < 200 or >= 400)
                {
                    // 非拦截类 HTTP 错误换层没有意义（429 已被 BlockDetector 覆盖），直接抛出
                    throw new HttpRequestException($"HTTP {result.StatusCode} {url}", null,
                        (HttpStatusCode)result.StatusCode);
                }
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex)
            {
                last = ex;
                _logger?.Warn($"[Fetcher] {sourceId} L{(int)tier + 1} 失败: {ex.Message}");
            }
            catch (Exception ex)
            {
                last = ex;
                _logger?.Warn($"[Fetcher] {sourceId} L{(int)tier + 1} 异常: {ex.Message}");
            }
        }

        throw last ?? new HttpRequestException($"抓取失败: {url}");
    }

    public async Task<FetchResult> PostAsync(string url, string contentType, string body, FetchOptions? options, CancellationToken ct)
    {
        options ??= new FetchOptions();
        var sourceId = string.IsNullOrEmpty(options.SourceId) ? "default" : options.SourceId;
        var lane = _lanes.GetOrAdd(sourceId, id => CreateLane(id));
        Exception? last = null;

        for (var tier = FetchTier.HttpClient; tier <= options.MaxTier; tier++)
        {
            if (tier == FetchTier.CurlImpersonate && !_l2.Available) continue;

            try
            {
                await lane.Gate.WaitAsync(ct);
                FetchResult result;
                try
                {
                    await DelayAsync(lane, url, ct);
                    var uri = new Uri(url);
                    result = tier switch
                    {
                        FetchTier.HttpClient => await PostWithHttpClientAsync(lane, uri, url, contentType, body, options, ct),
                        FetchTier.CurlImpersonate => await _l2.PostAsync(url, contentType, body,
                            _cookies.GetCookieHeader(uri), options.ExtraHeaders, lane.Proxy, ct),
                        _ => throw new HttpRequestException($"层级 {tier} 未实现"),
                    };
                }
                finally { lane.Gate.Release(); }

                if (BlockDetector.IsBlocked(result.StatusCode, result.Body))
                {
                    last = new HttpRequestException($"被反爬拦截 (HTTP {result.StatusCode})", null,
                        (HttpStatusCode)result.StatusCode);
                    _logger?.Warn($"[Fetcher] {sourceId} POST L{(int)tier + 1} 被拦截(HTTP {result.StatusCode})，尝试下一层: {url}");
                    continue;
                }
                if (result.StatusCode is < 200 or >= 400)
                    throw new HttpRequestException($"HTTP {result.StatusCode} {url}", null,
                        (HttpStatusCode)result.StatusCode);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex)
            {
                last = ex;
                _logger?.Warn($"[Fetcher] {sourceId} POST L{(int)tier + 1} 失败: {ex.Message}");
            }
            catch (Exception ex)
            {
                last = ex;
                _logger?.Warn($"[Fetcher] {sourceId} POST L{(int)tier + 1} 异常: {ex.Message}");
            }
        }

        throw last ?? new HttpRequestException($"POST 失败: {url}");
    }

    private async Task<FetchResult> PostWithHttpClientAsync(SourceLane lane, Uri uri, string url,
        string contentType, string body, FetchOptions options, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, contentType);
        var cookie = _cookies.GetCookieHeader(uri);
        if (!string.IsNullOrEmpty(cookie))
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (options.ExtraHeaders is not null)
            foreach (var header in options.ExtraHeaders)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        using var response = await lane.Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            _cookies.UpdateFromResponse(uri, setCookies);
        return new FetchResult((int)response.StatusCode, responseBody, FetchTier.HttpClient);
    }

    private async Task<FetchResult> GetWithHttpClientAsync(SourceLane lane, string url, FetchOptions options, CancellationToken ct)
    {
        await lane.Gate.WaitAsync(ct);
        try
        {
            await DelayAsync(lane, url, ct);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (!string.IsNullOrEmpty(options.Referer) && Uri.TryCreate(options.Referer, UriKind.Absolute, out var referer))
                        request.Headers.Referrer = referer;

                    var uri = new Uri(url);
                    var cookie = _cookies.GetCookieHeader(uri);
                    if (!string.IsNullOrEmpty(cookie))
                        request.Headers.TryAddWithoutValidation("Cookie", cookie);
                    if (options.ExtraHeaders is not null)
                        foreach (var header in options.ExtraHeaders)
                            request.Headers.TryAddWithoutValidation(header.Key, header.Value);

                    using var response = await lane.Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
                    var body = await response.Content.ReadAsStringAsync(ct);
                    if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                        _cookies.UpdateFromResponse(uri, setCookies);
                    return new FetchResult((int)response.StatusCode, body, FetchTier.HttpClient);
                }
                catch (Exception ex) when (attempt < 2 && ex is not OperationCanceledException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                }
            }
        }
        finally { lane.Gate.Release(); }
    }

    private async Task<FetchResult> GetWithCurlAsync(SourceLane lane, string url, FetchOptions options, CancellationToken ct)
    {
        await lane.Gate.WaitAsync(ct);
        try
        {
            await DelayAsync(lane, url, ct);
            return await _l2.GetAsync(
                url,
                _cookies.GetCookieHeader(new Uri(url)),
                options.ExtraHeaders,
                lane.Proxy,
                ct);
        }
        finally { lane.Gate.Release(); }
    }

    private SourceLane CreateLane(string sourceId)
    {
        var cfg = _advanced.SourceConfigs.TryGetValue(sourceId, out var sourceConfig) ? sourceConfig : null;
        var intervalMs = cfg is { RateLimitPerSecond: > 0 }
            ? Math.Clamp((int)(1000 / cfg.RateLimitPerSecond), 0, 10_000)
            : Math.Clamp(_settings.RequestIntervalMs, 0, 10_000);
        var proxy = !string.IsNullOrWhiteSpace(cfg?.ProxyOverride) ? cfg!.ProxyOverride
            : string.IsNullOrWhiteSpace(_settings.Proxy) ? null : _settings.Proxy;

        return new SourceLane
        {
            Client = _httpFactory.Create(sourceId),
            Gate = new SemaphoreSlim(Math.Clamp(_settings.Concurrency, 1, 8), Math.Clamp(_settings.Concurrency, 1, 8)),
            IntervalMs = intervalMs,
            Proxy = proxy,
        };
    }

    private static string GetHost(string url)
    {
        try { return new Uri(url).Host; }
        catch { return ""; }
    }

    private async Task DelayAsync(SourceLane lane, string url, CancellationToken ct)
    {
        if (lane.IntervalMs <= 0) return;
        var host = GetHost(url);
        var now = DateTime.UtcNow;
        var next = lane.NextByHost.AddOrUpdate(host,
            now.AddMilliseconds(lane.IntervalMs),
            (_, prev) => prev > now ? prev.AddMilliseconds(lane.IntervalMs) : now.AddMilliseconds(lane.IntervalMs));
        var wait = next - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    }
}
