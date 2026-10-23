using System.Net;
using System.Text;

namespace ResourceGrab.Core.Sources.LoveCutes;

/// <summary>
/// lovecutes 站点 HTTP 客户端：纯 HTML + JSON-LD，直连即可
/// （前置 Cloudflare 未开启挑战，实测详情页 200）。
/// <para>
/// 站点会偶发直接断开连接（探测期约 1/5 请求返回 curl 000，无响应体），
/// 因此内置重试 + 线性退避；这也是与本项目其他漫画源客户端最大的不同。
/// </para>
/// </summary>
public class LoveCutesHttpClient
{
    private readonly HttpClient _client;
    private readonly string _domain;

    public LoveCutesHttpClient(string? domain = null)
        : this(domain, CreateDefaultHandler())
    {
    }

    /// <summary>测试用构造：注入自定义 HttpMessageHandler。</summary>
    internal LoveCutesHttpClient(string? domain, HttpMessageHandler handler)
    {
        _domain = string.IsNullOrWhiteSpace(domain) ? LoveCutesConstants.Domain : domain;
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private static HttpMessageHandler CreateDefaultHandler() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    };

    public string Domain => _domain;

    /// <summary>站点根地址，如 "https://www.lovecutes.com"。用于 Referer 与相对 URL 补全。</summary>
    public string BaseUrl => $"https://{_domain}";

    /// <summary>GET 指定路径（path 以 / 开头），返回 HTML 文本。</summary>
    public virtual async Task<string> GetHtmlAsync(string path, CancellationToken ct = default)
    {
        var bytes = await GetBytesAsync(BaseUrl + path, ct);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>GET 任意 URL（列表页封面/图片），返回字节。</summary>
    public virtual async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", LoveCutesConstants.UserAgent);
                request.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
                using var httpResp = await _client.SendAsync(request, ct);
                if (httpResp.StatusCode != HttpStatusCode.OK)
                {
                    throw new JmException($"请求`{url}`失败，状态码 {(int)httpResp.StatusCode}");
                }
                return await httpResp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < 2)
            {
                // 站点断连与超时都值得重试；调用方主动取消则不再重试
                lastError = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(700 * (attempt + 1)), ct);
            }
        }
        throw lastError ?? new JmException($"请求`{url}`失败");
    }
}
