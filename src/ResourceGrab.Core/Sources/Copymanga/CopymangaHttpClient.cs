using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ResourceGrab.Core.Sources.Copymanga;

/// <summary>
/// 拷贝漫画 API 客户端（对齐 copymanga-downloader / lanyeeee）：
///  - 域名 api.copy202601.com
///  - 请求头 User-Agent=COPY/3.0.0、version、platform=1、webp=1、region=1
///  - 章节图片接口需要 Authorization: Token {token}（参考项目通过账号池登录获取）
/// </summary>
public class CopymangaHttpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _client;
    private readonly string _domain;
    private volatile string _token = "";

    public CopymangaHttpClient(string? domain = null)
        : this(domain, CreateDefaultHandler())
    {
    }

    /// <summary>测试用构造：注入自定义 HttpMessageHandler。</summary>
    internal CopymangaHttpClient(string? domain, HttpMessageHandler handler)
    {
        _domain = string.IsNullOrWhiteSpace(domain) ? CopymangaConstants.ApiDomain : domain!;
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

    /// <summary>API 根地址。</summary>
    public string BaseUrl => $"https://{_domain}";

    /// <summary>当前登录 token（空表示未登录）。</summary>
    public string Token => _token;

    /// <summary>设置登录 token（登录成功后调用）。</summary>
    public void SetToken(string token) => _token = token;

    /// <summary>POST 登录接口（表单），返回登录结果（含 token）。</summary>
    public virtual async Task<CopyLoginResult> LoginAsync(
        string username, string encodedPassword, int salt, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/v3/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = encodedPassword,
                ["salt"] = salt.ToString(),
            }),
        };
        ApplyHeaders(request);
        using var httpResp = await _client.SendAsync(request, ct);
        var body = await httpResp.Content.ReadAsStringAsync(ct);
        if (httpResp.StatusCode != HttpStatusCode.OK)
        {
            throw new JmException($"登录失败，状态码 {(int)httpResp.StatusCode}: {body}");
        }
        var api = JsonSerializer.Deserialize<CopyApiResponse<CopyLoginResult>>(body, JsonOptions)
                  ?? throw new JmException("登录失败：响应为空");
        if (api.Code != 200)
        {
            throw new JmException($"登录失败：{api.Message}");
        }
        return api.Results ?? throw new JmException("登录失败：结果为空");
    }

    /// <summary>GET 指定 API 路径（path 以 / 开头），反序列化并校验 code==200。</summary>
    public virtual async Task<T> GetAsync<T>(string path, bool requireAuth = false, CancellationToken ct = default)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
                ApplyHeaders(request);
                if (requireAuth && _token.Length > 0)
                {
                    request.Headers.TryAddWithoutValidation("Authorization", $"Token {_token}");
                }
                using var httpResp = await _client.SendAsync(request, ct);
                if (httpResp.StatusCode != HttpStatusCode.OK)
                {
                    throw new JmException($"请求`{BaseUrl + path}`失败，状态码 {(int)httpResp.StatusCode}");
                }

                var api = await httpResp.Content.ReadFromJsonAsync<CopyApiResponse<T>>(JsonOptions, ct)
                          ?? throw new JmException($"请求`{BaseUrl + path}`返回空响应");
                if (api.Code != 200)
                {
                    throw new JmException($"接口`{BaseUrl + path}`返回错误码 {api.Code}: {api.Message}");
                }
                return api.Results ?? throw new JmException($"接口`{BaseUrl + path}`结果为空");
            }
            catch (Exception ex) when (attempt < 2)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        throw lastError ?? new JmException($"请求`{BaseUrl + path}`失败");
    }

    /// <summary>GET 任意 URL（图片/封面），返回字节。</summary>
    public virtual async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", CopymangaConstants.UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var httpResp = await _client.SendAsync(request, ct);
                if (httpResp.StatusCode != HttpStatusCode.OK)
                {
                    throw new JmException($"请求`{url}`失败，状态码 {(int)httpResp.StatusCode}");
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

    private void ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", CopymangaConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("version", CopymangaConstants.ApiVersion);
        request.Headers.TryAddWithoutValidation("platform", "1");
        request.Headers.TryAddWithoutValidation("webp", "1");
        request.Headers.TryAddWithoutValidation("region", "1");
    }
}
