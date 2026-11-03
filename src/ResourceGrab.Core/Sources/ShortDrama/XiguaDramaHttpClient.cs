using System.Net;
using System.Text;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.Core.Sources.ShortDrama;

/// <summary>短剧站点不可用（所有线路都失败）。<see cref="TriedHosts"/> 供 UI 展示"试过哪些线"。</summary>
public sealed class ShortDramaUnavailableException(string message, IReadOnlyList<string> triedHosts)
    : Exception(message)
{
    public IReadOnlyList<string> TriedHosts { get; } = triedHosts;
}

/// <summary>
/// 短剧站点 HTTP 客户端：负责「换线」与「节流」两件事，业务语义一律交给调用方。
/// <para>
/// 换线顺序：上次成功的线路 → 用户配置的 baseUrl → /go-home/ 自发现的线路 → 内置备用列表。
/// 站点域名是"随机子域 + 官方发布页"形态，写死任何一个域名都活不过几周。
/// </para>
/// </summary>
public sealed class XiguaDramaHttpClient
{
    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private readonly string? _configuredBaseUrl;

    /// <summary>请求节流：串行化 + 最小间隔，避免自动翻页把站点打疼。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTime _lastRequestUtc = DateTime.MinValue;
    private string? _lastGoodHost;
    private IReadOnlyList<string> _discoveredHosts = [];
    private DateTime _lastDiscoveryUtc = DateTime.MinValue;

    /// <param name="http">外部构造的 HttpClient（UA/超时/代理由调用方决定）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="configuredBaseUrl">用户在设置里填写的线路；为空用默认主站。</param>
    public XiguaDramaHttpClient(HttpClient http, ILogger? logger = null, string? configuredBaseUrl = null)
    {
        _http = http;
        _logger = logger;
        _configuredBaseUrl = Normalize(configuredBaseUrl);
    }

    /// <summary>当前生效的基址（成功过的那条；还没成功过则为配置值/默认值）。</summary>
    public string CurrentBaseUrl =>
        _lastGoodHost
        ?? _configuredBaseUrl
        ?? XiguaDramaConstants.DefaultBaseUrl;

    /// <summary>最近一次请求实际用过的线路（UI 的线路卡显示它）。</summary>
    public string? LastGoodHost => _lastGoodHost;

    /// <summary>取一个站内路径的 HTML（path 以 / 开头）。失败会自动换线。</summary>
    public async Task<string> GetStringAsync(string path, CancellationToken ct = default)
    {
        var tried = new List<string>();
        foreach (var host in CandidateHosts())
        {
            ct.ThrowIfCancellationRequested();
            tried.Add(host);
            try
            {
                await ThrottleAsync(ct).ConfigureAwait(false);
                var html = await GetOnceAsync(host, path, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(html))
                {
                    if (!string.Equals(_lastGoodHost, host, StringComparison.OrdinalIgnoreCase))
                        _logger?.Info($"[XiguaDrama] 线路切换为 {host}");
                    _lastGoodHost = host;
                    return html;
                }
                _logger?.Warn($"[XiguaDrama] {host}{path} 返回空内容，尝试下一条线路");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn($"[XiguaDrama] {host}{path} 失败：{ex.Message}");
            }
        }

        // 全部失败：最后再试一次官方发布页自发现（可能是子域整体轮换了）
        if (DateTime.UtcNow - _lastDiscoveryUtc > TimeSpan.FromMinutes(5))
        {
            var refreshed = await DiscoverHostsAsync(ct).ConfigureAwait(false);
            foreach (var host in refreshed)
            {
                if (tried.Contains(host, StringComparer.OrdinalIgnoreCase)) continue;
                tried.Add(host);
                try
                {
                    await ThrottleAsync(ct).ConfigureAwait(false);
                    var html = await GetOnceAsync(host, path, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(html))
                    {
                        _lastGoodHost = host;
                        _logger?.Info($"[XiguaDrama] 经发布页自发现换线到 {host}");
                        return html;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger?.Warn($"[XiguaDrama] 自发现线路 {host} 失败：{ex.Message}"); }
            }
        }

        throw new ShortDramaUnavailableException(
            $"短剧站点所有线路均不可用（已尝试 {tried.Count} 条）。可在设置里换一条线路，或点线路卡刷新。",
            tried);
    }

    /// <summary>读取官方发布页（/go-home/）里的线路列表；失败返回空列表（不抛）。</summary>
    public async Task<IReadOnlyList<string>> DiscoverHostsAsync(CancellationToken ct = default)
    {
        _lastDiscoveryUtc = DateTime.UtcNow;
        foreach (var host in new[] { XiguaDramaConstants.DefaultBaseUrl, _configuredBaseUrl, _lastGoodHost }
                     .Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h!).Distinct())
        {
            try
            {
                await ThrottleAsync(ct).ConfigureAwait(false);
                var html = await GetOnceAsync(host, XiguaDramaConstants.GoHomePath, ct).ConfigureAwait(false);
                var hosts = XiguaDramaParser.ParseHosts(html);
                if (hosts.Count > 0)
                {
                    _discoveredHosts = hosts;
                    _logger?.Info($"[XiguaDrama] 发布页发现 {hosts.Count} 条线路：{string.Join(", ", hosts)}");
                    return hosts;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger?.Warn($"[XiguaDrama] 发布页读取失败（{host}）：{ex.Message}"); }
        }
        return [];
    }

    private IEnumerable<string> CandidateHosts()
    {
        var list = new List<string>();
        void Add(string? host)
        {
            var h = Normalize(host);
            if (h is null) return;
            if (!list.Contains(h, StringComparer.OrdinalIgnoreCase)) list.Add(h);
        }

        Add(_lastGoodHost);
        Add(_configuredBaseUrl);
        foreach (var h in _discoveredHosts) Add(h);
        foreach (var h in XiguaDramaConstants.FallbackHosts) Add(h);
        return list;
    }

    /// <summary>
    /// 单次请求。**单次尝试自带 20 秒上限**：站点偶发卡住时，宁可快速换下一条线路，
    /// 也不要让一个 40 秒的 HttpClient 超时把整次导航拖死。
    /// </summary>
    private async Task<string> GetOnceAsync(string host, string path, CancellationToken ct)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(TimeSpan.FromSeconds(AttemptTimeoutSeconds));
        var attemptToken = attemptCts.Token;

        using var request = new HttpRequestMessage(HttpMethod.Get, host.TrimEnd('/') + path);
        // ⚠️ 只在客户端没有默认值时才逐请求补头：同一个头出现两次（DefaultRequestHeaders + 请求头）
        // 会被站点 CDN 判成异常客户端，直接把整站请求打到"地址发布页"（返回 645 字节的 publish.js 页面）。
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            request.Headers.TryAddWithoutValidation("User-Agent", XiguaDramaConstants.UserAgent);
        if (!_http.DefaultRequestHeaders.Contains("Accept"))
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        if (!_http.DefaultRequestHeaders.Contains("Accept-Language"))
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, attemptToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(attemptToken).ConfigureAwait(false);
        return DecodeBody(bytes, response.Content.Headers.ContentType?.CharSet);
    }

    /// <summary>单次尝试的超时（秒）。</summary>
    private const int AttemptTimeoutSeconds = 20;

    private static string DecodeBody(byte[] bytes, string? charset)
    {
        if (bytes.Length == 0) return "";
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes); }
            catch { /* 未知 charset 退回 UTF-8 */ }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>最小请求间隔节流（串行化保证并发调用也不会突破间隔）。</summary>
    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequestUtc;
            var wait = XiguaDramaConstants.RequestIntervalMs - (int)elapsed.TotalMilliseconds;
            if (wait > 0) await Task.Delay(wait, ct).ConfigureAwait(false);
            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim().TrimEnd('/');
        if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) value = "https://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
    }
}
