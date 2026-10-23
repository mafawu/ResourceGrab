using System.Net;
using System.Text.Json;

namespace ResourceGrab.Core.Sources.Mzt;

/// <summary>
/// 妹子图站点 HTTP 客户端：纯 JSON 接口，直连即可
/// （前置 Cloudflare 未开启挑战，实测列表/详情/标签均 200）。
/// <para>
/// 与本项目其他源客户端最大的不同：响应是 JSON 而非 HTML，故直接返回 <see cref="JsonDocument"/>。
/// 站点偶发响应偏慢甚至断连（大 JSON 探测期多次超时），因此内置重试 + 线性退避。
/// </para>
/// </summary>
public class MztHttpClient
{
    private readonly HttpClient _client;

    public MztHttpClient() : this(CreateDefaultHandler())
    {
    }

    /// <summary>测试用构造：注入自定义 HttpMessageHandler。</summary>
    internal MztHttpClient(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private static HttpMessageHandler CreateDefaultHandler() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    };

    /// <summary>GET 一个 JSON 接口，返回已解析的文档（调用方负责 Dispose）。</summary>
    public virtual async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct = default)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", MztConstants.UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

                using var httpResp = await _client.SendAsync(request, ct);
                if (httpResp.StatusCode != HttpStatusCode.OK)
                {
                    throw new JmException($"请求`{url}`失败，状态码 {(int)httpResp.StatusCode}");
                }

                await using var stream = await httpResp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < 2)
            {
                // 站点断连与超时都值得重试；调用方主动取消则不再重试
                lastError = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(800 * (attempt + 1)), ct);
            }
        }
        throw lastError ?? new JmException($"请求`{url}`失败");
    }
}
