using System.Net;
using System.Text;

namespace ResourceGrab.Core.Sources.Baozimh;

public class BaozimhHttpClient
{
    private const string DefaultDomain = BaozimhConstants.DefaultDomain;
    private static readonly string UserAgent = BaozimhConstants.UserAgent;

    private readonly HttpClient _client;
    private readonly string _domain;

    public BaozimhHttpClient(string? domain = null)
        : this(domain, CreateDefaultHandler())
    {
    }

    internal BaozimhHttpClient(string? domain, HttpMessageHandler handler)
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
        AllowAutoRedirect = true,
    };

    public string Domain => _domain;
    public string BaseUrl => "https://" + _domain;

    public virtual async Task<string> GetHtmlAsync(string pathOrUrl, CancellationToken ct = default)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? pathOrUrl
            : BaseUrl + pathOrUrl;
        var bytes = await GetBytesAsync(url, ct);
        return Encoding.UTF8.GetString(bytes);
    }

    public virtual async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.TryAddWithoutValidation("Referer", BaozimhConstants.Referer);
                using var httpResp = await _client.SendAsync(request, ct);
                if (httpResp.StatusCode != HttpStatusCode.OK)
                {
                    var text = await httpResp.Content.ReadAsStringAsync(ct);
                    throw new JmException("请求`" + url + "`失败，预料之外的状态码: " + text);
                }
                return await httpResp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex) when (attempt < 2)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        throw lastError ?? new JmException("请求`" + url + "`失败");
    }
}
