using System.Net;
using System.Text;

namespace ResourceGrab.Core.Sources.Wnacg;

/// <summary>
/// wnacg 站点 HTML 客户端：所有请求带 Referer 防盗链头，简单重试。
/// 该站点无加密 API，全部为 HTML 页面。
/// </summary>
public class WnacgHttpClient
{
    private const string DefaultDomain = "www.wnacg.com";
    private static readonly string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

    private readonly HttpClient _client;
    private readonly string _domain;

    public WnacgHttpClient(string? domain = null)
        : this(domain, CreateDefaultHandler())
    {
    }

    /// <summary>测试用构造：注入自定义 HttpMessageHandler。</summary>
    internal WnacgHttpClient(string? domain, HttpMessageHandler handler)
    {
        _domain = string.IsNullOrWhiteSpace(domain) ? DefaultDomain : domain;
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    private static HttpMessageHandler CreateDefaultHandler() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    };

    public string Domain => _domain;

    /// <summary>站点根地址，如 "https://www.wnacg.com"。用于 Referer 与相对 URL 补全。</summary>
    public string BaseUrl => $"https://{_domain}";

    /// <summary>GET 指定路径（path 以 / 开头），返回 HTML 文本。</summary>
    public virtual async Task<string> GetHtmlAsync(string path, CancellationToken ct = default)
    {
        var bytes = await GetBytesAsync(BaseUrl + path, ct);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>GET 任意 URL（用于封面/图片），返回字节。</summary>
    public virtual async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
                using var httpResp = await _client.SendAsync(request, ct);
                if (httpResp.StatusCode != HttpStatusCode.OK)
                {
                    var text = await httpResp.Content.ReadAsStringAsync(ct);
                    throw new JmException($"请求`{url}`失败，预料之外的状态码: {text}");
                }
                return await httpResp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex) when (attempt < 2)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        throw lastError ?? new JmException($"请求`{url}`失败");
    }
}

