using System.Net;
using System.Text;
using ResourceGrab.Core.Sources.Novel;

namespace ResourceGrab.Core.Sources.Alicesw;

/// <summary>
/// 爱丽丝书屋 HTTP 客户端：站点没有签名/加密/JS 挑战，只要 UA 与正确的 Referer。
/// 域名按顺序失败切换，一次运行内记住可用域名，避免每次请求都先撞一次失效域名。
/// </summary>
public class AliceswHttpClient
{
    private readonly HttpClient _client;
    private readonly string[] _domains;
    private int _domainIndex;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AliceswHttpClient(string? domain = null, string? proxy = null)
        : this(domain, CreateDefaultHandler(proxy))
    {
    }

    internal AliceswHttpClient(string? domain, HttpMessageHandler handler)
    {
        _domains = string.IsNullOrWhiteSpace(domain)
            ? AliceswConstants.Domains
            : [domain, .. AliceswConstants.Domains.Where(d => !string.Equals(d, domain, StringComparison.OrdinalIgnoreCase))];
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>proxy 留空表示走系统代理；填了就用指定出口（站点在某些网络下需要）。</summary>
    private static HttpMessageHandler CreateDefaultHandler(string? proxy = null)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true,
        };
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }
        return handler;
    }

    public string Domain => _domains[Math.Clamp(_domainIndex, 0, _domains.Length - 1)];

    public string BaseUrl => "https://" + Domain;

    public virtual async Task<string> GetHtmlAsync(string pathOrUrl, CancellationToken ct = default)
    {
        var path = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(pathOrUrl).PathAndQuery
            : pathOrUrl;

        Exception? last = null;
        for (var attempt = 0; attempt < _domains.Length; attempt++)
        {
            var domain = await NextDomainAsync(attempt, ct);
            var url = "https://" + domain + path;
            try
            {
                return await GetOnceAsync(url, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                // 只在网络层/状态码级别换线；解析错误不换域名
                await MarkDomainFailedAsync(domain, ct);
            }
        }

        throw new JmException(
            $"书源连不上（已尝试 {string.Join("、", _domains)}）。站点可能换了地址，请到发布页 {AliceswConstants.PublishPage} 查看。",
            last ?? new Exception("未知网络错误"));
    }

    private async Task<string> GetOnceAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", AliceswConstants.UserAgent);
            request.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                if (attempt == 0)
                {
                    await Task.Delay(400, ct);
                    continue;
                }
                throw new JmException($"请求 `{url}` 失败：HTTP {(int)response.StatusCode}");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return Encoding.UTF8.GetString(bytes);
        }

        throw new JmException($"请求 `{url}` 失败");
    }

    private async Task<string> NextDomainAsync(int attempt, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return _domains[(_domainIndex + attempt) % _domains.Length];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MarkDomainFailedAsync(string domain, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var idx = Array.IndexOf(_domains, domain);
            if (idx >= 0 && idx == _domainIndex) _domainIndex = (idx + 1) % _domains.Length;
        }
        finally
        {
            _gate.Release();
        }
    }
}
