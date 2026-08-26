using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ResourceGrab.Core.Sources.Hitomi;

/// <summary>
/// hitomi 数据域 HTTP 客户端：文本 / 字节 / Range 分段请求，统一 UA 与简单重试。
/// 与 wnacg 客户端一样支持注入 handler 供测试使用。
/// </summary>
public class HitomiHttpClient
{
    private readonly HttpClient _client;

    public HitomiHttpClient()
        : this(CreateDefaultHandler())
    {
    }

    /// <summary>测试用构造：注入自定义 HttpMessageHandler。</summary>
    internal HitomiHttpClient(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    private static HttpMessageHandler CreateDefaultHandler() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    };

    /// <summary>数据域根地址，如 "https://ltn.gold-usergeneratedcontent.net"。</summary>
    public string BaseUrl => $"{HitomiConstants.Protocol}//{HitomiConstants.Domain}";

    /// <summary>GET 任意 URL，返回 UTF-8 文本（如 gg.js / galleryinfo）。</summary>
    public virtual async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        var bytes = await GetBytesAsync(url, ct);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>GET 任意 URL，返回原始字节；非 200 抛 <see cref="JmException"/>。</summary>
    public virtual async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", HitomiConstants.UserAgent);
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

    /// <summary>GET 任意 URL，非 200 返回 null（nozomi 列表缺失属正常情况，返回空列表即可）。</summary>
    public virtual async Task<byte[]?> GetBytesOrNullAsync(string url, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", HitomiConstants.UserAgent);
        using var httpResp = await _client.SendAsync(request, ct);
        if (httpResp.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }
        return await httpResp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Range 分段请求（B-tree 索引读取），返回 start 起 length 字节。</summary>
    public virtual async Task<byte[]> GetRangeAsync(string url, long start, long length, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", HitomiConstants.UserAgent);
        request.Headers.Range = new RangeHeaderValue(start, start + length - 1);
        using var httpResp = await _client.SendAsync(request, ct);
        if (httpResp.StatusCode != HttpStatusCode.PartialContent && httpResp.StatusCode != HttpStatusCode.OK)
        {
            var text = await httpResp.Content.ReadAsStringAsync(ct);
            throw new JmException($"Range 请求`{url}`失败，预料之外的状态码: {text}");
        }
        return await httpResp.Content.ReadAsByteArrayAsync(ct);
    }
}
