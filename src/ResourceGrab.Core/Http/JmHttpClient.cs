using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Utils;
using Polly;
using Polly.Retry;

namespace ResourceGrab.Core.Http;

/// <summary>
/// 禁漫 API 客户端（对应原 Rust 实现 jm_client.rs）。
/// 负责请求签名（token/tokenparam）、AES-256-ECB 响应解密、Cookie 会话与重试。
/// 单例使用，所有请求共享同一个 CookieContainer；登录后固定使用建立会话的域名（AVS Cookie 仅对该域名有效），会话失效时用保存的凭据自动重新登录并重试。
/// </summary>
public class JmHttpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _client;
    private readonly ResiliencePipeline _pipeline;
    private readonly ConfigService _configService;
    private readonly ApiDomainPool _domainPool;

    /// <summary>登录成功时建立会话（AVS Cookie）的域名；认证类请求固定使用该域名，避免轮换到其他域名导致会话失效。</summary>
    private string? _sessionDomain;

    private enum ApiPath
    {
        Login,
        UserProfile,
        Search,
        Album,
        Chapter,
        ScrambleId,
        Favorite,
        WeeklyInfo,
        Weekly,
        Forum,
    }

    private static string ApiPathStr(ApiPath path) => path switch
    {
        // 获取用户信息也是用 /login：带 AVS cookie 请求即可，不需要账号密码
        ApiPath.Login or ApiPath.UserProfile => "/login",
        ApiPath.Search => "/search",
        ApiPath.Album => "/album",
        ApiPath.Chapter => "/chapter",
        ApiPath.ScrambleId => "/chapter_view_template",
        ApiPath.Favorite => "/favorite",
        ApiPath.WeeklyInfo => "/week",
        ApiPath.Weekly => "/week/filter",
        ApiPath.Forum => "/forum",
        _ => throw new ArgumentOutOfRangeException(nameof(path)),
    };

    public JmHttpClient(ConfigService configService)
    : this(configService, CreateDefaultHandler())
{
}

    /// <summary>测试用构造：注入自定义 HttpMessageHandler 以便模拟域名故障与响应。</summary>
    internal JmHttpClient(ConfigService configService, HttpMessageHandler handler)
{
    _configService = configService;
    _domainPool = new ApiDomainPool(configService);
    _client = new HttpClient(handler)
    {
        Timeout = TimeSpan.FromSeconds(15),
        };

        // 与原版一致：指数退避 + 抖动，重试间隔约 1 秒，总重试时长约 3 秒
        var retryOptions = new RetryStrategyOptions
        {
            // 超时会由域名轮换处理；这里若重试 OperationCanceledException，
            // 单个请求会被放大成“重试 x 域名轮换”的长等待。
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>(),
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Constant,
            UseJitter = true,
            MaxDelay = TimeSpan.FromSeconds(2),
        };
        _pipeline = new ResiliencePipelineBuilder().AddRetry(retryOptions).Build();
    }

    private static HttpMessageHandler CreateDefaultHandler() => new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    };

    /// <summary>发起带签名的请求（对应 jm_request）。</summary>
    private Task<HttpResponseMessage> JmRequestAsync(
        HttpMethod method,
        ApiPath path,
        Dictionary<string, object>? query,
        Dictionary<string, object>? form,
        long ts,
        string operation,
        CancellationToken ct = default)
        => JmRequestAsync(method, ApiPathStr(path), query, form, ts, operation, ct, apiPath: path);

    /// <summary>发起带签名的请求（支持任意 API 路径，如带分类路径的 /search/photos/doujin）。</summary>
    private async Task<HttpResponseMessage> JmRequestAsync(
        HttpMethod method,
        string path,
        Dictionary<string, object>? query,
        Dictionary<string, object>? form,
        long ts,
        string operation,
        CancellationToken ct = default,
        ApiPath? apiPath = null)
    {
        var tokenparam = $"{ts},{JmConstants.AppVersion}";
        var secret = path == ApiPathStr(ApiPath.ScrambleId) ? JmConstants.AppTokenSecret2 : JmConstants.AppTokenSecret;
        var token = Md5Util.Hex($"{ts}{secret}");

        return await SendWithDomainRotationAsync(operation, apiPath, async domain =>
        {
            var url = $"https://{domain}{path}";
            if (query is not null && query.Count > 0)
            {
                var qs = string.Join("&", query.Select(kv =>
                    $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value?.ToString() ?? "")}"));
                url += "?" + qs;
            }

            return await _pipeline.ExecuteAsync(async ct2 =>
            {
                using var request = new HttpRequestMessage(method, url);
                request.Headers.TryAddWithoutValidation("token", token);
                request.Headers.TryAddWithoutValidation("tokenparam", tokenparam);
                request.Headers.TryAddWithoutValidation("user-agent", JmConstants.UserAgent);
    
                if (form is not null && form.Count > 0)
                {
                    request.Content = new FormUrlEncodedContent(
                        form.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value?.ToString() ?? "")));
                }
    
                return await _client.SendAsync(request, ct2);
            }, ct);
        }, ct);
    }

    /// <summary>
    /// 带域名轮换的请求：逐个尝试候选域名。网络失败 / 域名级错误（非 JSON 错误页的非 200 响应）
    /// 时切换到下一个域名并临时冷却失效域名；业务错误（如登录失败返回 JSON 错误体）直接抛出，不切换域名。
    /// 认证类请求（收藏 / 用户信息）固定优先使用登录时建立会话的域名——AVS Cookie 与域名绑定，
    /// 轮换到其他域名会丢失会话；若目标域名返回"未登录"，则用配置中保存的凭据自动重新登录并重试一次。
    /// </summary>
    private async Task<HttpResponseMessage> SendWithDomainRotationAsync(
        string operation, ApiPath? apiPath, Func<string, Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        var isAuthApi = apiPath is ApiPath.Favorite or ApiPath.UserProfile;
        var isCredentialLogin = apiPath == ApiPath.Login;
        var domains = _domainPool.GetDomains();
        if (domains.Count == 0)
        {
            throw new JmException($"{operation}失败：未配置可用的接口域名");
        }

        // 认证类请求固定优先使用建立会话的域名（AVS Cookie 仅对该域名有效）
        var sessionDomain = isAuthApi && _sessionDomain is not null ? FindDomain(domains, _sessionDomain) : null;

        Exception? lastError = null;
        var tried = new List<string>(domains.Count);
        var reloginAttempted = false;
        for (var i = 0; i < domains.Count; i++)
        {
            var domain = i == 0 && sessionDomain is not null ? sessionDomain : _domainPool.Next();
            tried.Add(domain);
            HttpResponseMessage? response = null;
            try
            {
                response = await send(domain);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    _domainPool.MarkSuccess(domain);
                    if (isCredentialLogin || isAuthApi)
                    {
                        _sessionDomain = domain;
                    }
                    return response;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                if (TryExtractBusinessError(body, out var businessMessage))
                {
                    if (isAuthApi && !reloginAttempted && IsSessionExpiredError(response, businessMessage))
                    {
                        reloginAttempted = true;
                        response.Dispose();
                        response = null;
                        var loginError = await TryReloginAsync(domain, ct);
                        if (loginError is null)
                        {
                            i--; // 重新登录成功：重试当前域名
                            continue;
                        }
                        throw loginError;
                    }
                    response.Dispose();
                    throw new JmException($"{operation}失败: {businessMessage}");
                }

                var statusCode = (int)response.StatusCode;
                response.Dispose();
                response = null;
                _domainPool.MarkFailed(domain);
                lastError = new JmException($"域名 {domain} 返回状态码 {statusCode}: {body}");
            }
            catch (Exception ex) when (IsDomainLevelFailure(ex))
            {
                response?.Dispose();
                _domainPool.MarkFailed(domain);
                lastError = ex;
            }
        }

        throw new JmException(
            $"{operation}失败：所有接口域名均不可用（已尝试: {string.Join(", ", tried)}），请检查网络或在设置中更换域名",
            lastError!);
    }

    /// <summary>在域名列表中查找指定域名（忽略大小写），找不到返回 null。</summary>
    private static string? FindDomain(IReadOnlyList<string> domains, string target)
    {
        foreach (var domain in domains)
        {
            if (string.Equals(domain, target, StringComparison.OrdinalIgnoreCase))
            {
                return domain;
            }
        }
        return null;
    }

    /// <summary>是否属于"会话失效（未登录）"错误：HTTP 401 或服务端提示登录。</summary>
    private static bool IsSessionExpiredError(HttpResponseMessage response, string message)
        => response.StatusCode == HttpStatusCode.Unauthorized
           || message.Contains("登入", StringComparison.Ordinal)
           || message.Contains("登錄", StringComparison.Ordinal)
           || message.Contains("登录", StringComparison.Ordinal)
           || message.Contains("login", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 用配置中保存的凭据在指定域名重新登录（AVS Cookie 与域名绑定，需在目标域名重建会话）。
    /// 成功返回 null 并更新会话域名；失败返回异常。
    /// </summary>
    private async Task<Exception?> TryReloginAsync(string domain, CancellationToken ct)
    {
        var config = _configService.Current;
        if (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(config.Password))
        {
            return new JmException("未保存登录凭据，无法自动重新登录，请重新登录后再试");
        }

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = Md5Util.Hex($"{ts}{JmConstants.AppTokenSecret}");
        var tokenparam = $"{ts},{JmConstants.AppVersion}";

        try
        {
            using var response = await _pipeline.ExecuteAsync(async ct2 =>
            {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{domain}/login");
            request.Headers.TryAddWithoutValidation("token", token);
            request.Headers.TryAddWithoutValidation("tokenparam", tokenparam);
            request.Headers.TryAddWithoutValidation("user-agent", JmConstants.UserAgent);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = config.Username,
                ["password"] = config.Password,
            });
    
                return await _client.SendAsync(request, ct2);
            }, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.OK && TryReadCode(body, out var code) && code == 200)
            {
                _sessionDomain = domain;
                return null;
            }
            if (TryExtractBusinessError(body, out var message))
            {
                return new JmException($"自动重新登录失败: {message}");
            }
            return new JmException($"自动重新登录失败，状态码 {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return new JmException("自动重新登录失败: 网络异常", ex);
        }
    }

    /// <summary>从响应体中读取 code 字段（用于区分 code=200 成功与业务失败）。</summary>
    private static bool TryReadCode(string body, out long code)
    {
        code = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var codeEl))
            {
                code = codeEl.GetInt64();
                return true;
            }
        }
        catch (JsonException)
        {
            // 非 JSON
        }
        return false;
    }
    /// <summary>是否属于域名级失败（网络异常 / 超时），需要切换到下一个域名。</summary>
    private static bool IsDomainLevelFailure(Exception ex)
        => ex is HttpRequestException
           or OperationCanceledException { CancellationToken.IsCancellationRequested: false };

    /// <summary>尝试从非 200 的 JSON 响应体中提取业务错误（如登录失败返回 HTTP 401 + {"code":401,"errorMsg":"..."}）。</summary>
    private static bool TryExtractBusinessError(string body, out string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var codeEl))
            {
                var code = codeEl.GetInt64();
                var errMsg = "";
                if (doc.RootElement.TryGetProperty("errorMsg", out var msgEl) ||
                    doc.RootElement.TryGetProperty("error_msg", out msgEl))
                {
                    errMsg = msgEl.GetString() ?? "";
                }
                message = string.IsNullOrWhiteSpace(errMsg) ? $"code={code}" : errMsg;
                return true;
            }
        }
        catch (JsonException)
        {
            // 不是 JSON 错误体
        }
        message = "";
        return false;
    }
    /// <summary>解密 API 响应中的 data 字段（对应 decrypt_data）。</summary>
    public static string DecryptData(long ts, string data)
    {
        var encrypted = Convert.FromBase64String(data);
        var key = Encoding.UTF8.GetBytes(Md5Util.Hex($"{ts}{JmConstants.AppDataSecret}"));
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
        // 手动去除 PKCS#7 填充（与原版逻辑一致）
        var paddingLength = decrypted[^1];
        var withoutPadding = decrypted[..^paddingLength];
        return Encoding.UTF8.GetString(withoutPadding);
    }

    private static async Task<string> ReadAndVerifyAsync(
        HttpResponseMessage httpResp, string operation, CancellationToken ct)
    {
        var body = await httpResp.Content.ReadAsStringAsync(ct);
        if (httpResp.StatusCode != HttpStatusCode.OK)
        {
            // 非 200 时尝试提取服务端返回的业务错误（如登录失败返回 HTTP 401 + {"code":401,"errorMsg":"..."}）。
            // 正常路径下响应已由域名轮换保证为 200，此处保留防御性处理。
            if (TryExtractBusinessError(body, out var errMsg))
            {
                throw new JmException($"{operation}失败: {errMsg}");
            }
            throw new JmException($"{operation}失败，预料之外的状态码({(int)httpResp.StatusCode}): {body}");
        }
        return body;
    }

    private static JmResp ParseJmResp(string body, string operation)
    {
        JmResp jmResp;
        try
        {
            jmResp = JsonSerializer.Deserialize<JmResp>(body, JsonOptions)
                     ?? throw new JmException($"将body解析为JmResp失败: {body}");
        }
        catch (JsonException ex)
        {
            throw new JmException($"将body解析为JmResp失败: {body}", ex);
        }
        if (jmResp.Code != 200)
        {
            throw new JmException($"{operation}失败，预料之外的code: {body}");
        }
        if (jmResp.Data.ValueKind != JsonValueKind.String)
        {
            throw new JmException($"{operation}失败，data字段不是字符串: {body}");
        }
        return jmResp;
    }

    private async Task<T> JmGetJsonAsync<T>(
        ApiPath path, Dictionary<string, object>? query, string operation, CancellationToken ct = default)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var httpResp = await JmRequestAsync(HttpMethod.Get, path, query, null, ts, operation, ct);
        var body = await ReadAndVerifyAsync(httpResp, operation, ct);
        var jmResp = ParseJmResp(body, operation);
        var decrypted = DecryptData(ts, jmResp.Data.GetString()!);
        try
        {
            return JsonSerializer.Deserialize<T>(decrypted, JsonOptions)
                   ?? throw new JmException($"将解密后的data字段解析为{typeof(T).Name}失败: {decrypted}");
        }
        catch (JsonException ex)
        {
            throw new JmException($"将解密后的data字段解析为{typeof(T).Name}失败: {decrypted}", ex);
        }
    }

    private async Task<T> JmPostJsonAsync<T>(
        ApiPath path, Dictionary<string, object>? query, Dictionary<string, object>? form,
        string operation, CancellationToken ct = default)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var httpResp = await JmRequestAsync(HttpMethod.Post, path, query, form, ts, operation, ct);
        var body = await ReadAndVerifyAsync(httpResp, operation, ct);
        var jmResp = ParseJmResp(body, operation);
        var decrypted = DecryptData(ts, jmResp.Data.GetString()!);
        try
        {
            return JsonSerializer.Deserialize<T>(decrypted, JsonOptions)
                   ?? throw new JmException($"将解密后的data字段解析为{typeof(T).Name}失败: {decrypted}");
        }
        catch (JsonException ex)
        {
            throw new JmException($"将解密后的data字段解析为{typeof(T).Name}失败: {decrypted}", ex);
        }
    }

    // ====================== 业务方法 ======================

    /// <summary>使用账号密码登录。</summary>
    public Task<UserProfileRespData> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var form = new Dictionary<string, object> { ["username"] = username, ["password"] = password };
        return JmPostJsonAsync<UserProfileRespData>(ApiPath.Login, null, form, "使用账号密码登录", ct);
    }

    /// <summary>获取用户信息（复用 /login + AVS cookie）。</summary>
    public Task<UserProfileRespData> GetUserProfileAsync(CancellationToken ct = default)
    {
        return JmPostJsonAsync<UserProfileRespData>(ApiPath.UserProfile, null, null, "获取用户信息", ct);
    }

    /// <summary>搜索漫画。</summary>
    public Task<SearchResp> SearchAsync(string keyword, long page, SearchSort sort, CancellationToken ct = default)
    {
        var query = new Dictionary<string, object>
        {
            ["main_tag"] = 0,
            ["search_query"] = keyword,
            ["page"] = page,
            ["o"] = sort.ToQueryString(),
        };
        return SearchCoreAsync(ApiPathStr(ApiPath.Search), query, ct);
    }

    /// <summary>网页搜索接口：支持分类路径（如 "doujin"、"doujin/sub/CG"）与 天/周/月周期（o=mv_w 等）。</summary>
    public Task<SearchResp> SearchPhotosAsync(
        string keyword, long page, SearchSort sort, RankPeriod period = RankPeriod.All,
        string? categoryPath = null, CancellationToken ct = default)
    {
        var query = new Dictionary<string, object>
        {
            ["main_tag"] = 0,
            ["search_query"] = keyword,
            ["page"] = page,
            ["o"] = sort.Combine(period),
        };
        var path = string.IsNullOrWhiteSpace(categoryPath)
            ? "/search/photos"
            : $"/search/photos/{categoryPath}";
        return SearchCoreAsync(path, query, ct);
    }

    /// <summary>分类 / 排行过滤接口：分类 slug（single/short/hanman/doujin/meiman/another）+ 周期。</summary>
    public Task<SearchResp> CategoriesFilterAsync(
        long page, SearchSort sort, RankPeriod period = RankPeriod.All,
        string? categorySlug = null, CancellationToken ct = default)
    {
        var query = new Dictionary<string, object>
        {
            ["page"] = page,
            ["order"] = "",
            ["c"] = categorySlug ?? "",
            ["o"] = sort.Combine(period),
        };
        return SearchCoreAsync("/categories/filter", query, ct);
    }

    private async Task<SearchResp> SearchCoreAsync(string path, Dictionary<string, object> query, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var httpResp = await JmRequestAsync(HttpMethod.Get, path, query, null, ts, "搜索", ct);
        var body = await ReadAndVerifyAsync(httpResp, "搜索", ct);
        var jmResp = ParseJmResp(body, "搜索");
        var decrypted = DecryptData(ts, jmResp.Data.GetString()!);

        // 先尝试解析为重定向（搜索命中唯一漫画）
        try
        {
            var redirect = JsonSerializer.Deserialize<RedirectRespData>(decrypted, JsonOptions);
            if (redirect is not null && !string.IsNullOrEmpty(redirect.RedirectAid))
            {
                var aid = long.Parse(redirect.RedirectAid);
                var album = await GetAlbumAsync(aid, ct);
                return new SearchResp { AlbumRespData = album };
            }
        }
        catch (JsonException)
        {
            // 不是重定向结构，继续尝试搜索列表
        }

        try
        {
            var searchRespData = JsonSerializer.Deserialize<SearchRespData>(decrypted, JsonOptions);
            if (searchRespData is not null)
            {
                return new SearchResp { SearchRespData = searchRespData };
            }
        }
        catch (JsonException)
        {
            // 继续
        }

        throw new JmException($"将解密后的数据解析为SearchRespData或RedirectRespData失败: {decrypted}");
    }

    /// <summary>获取漫画（专辑）详情。</summary>
    public Task<AlbumRespData> GetAlbumAsync(long aid, CancellationToken ct = default)
    {
        var query = new Dictionary<string, object> { ["id"] = aid };
        return JmGetJsonAsync<AlbumRespData>(ApiPath.Album, query, "获取漫画", ct);
    }

    /// <summary>获取章节详情（含图片列表）。</summary>
    public Task<ChapterRespData> GetChapterAsync(long id, CancellationToken ct = default)
    {
        var query = new Dictionary<string, object> { ["id"] = id };
        return JmGetJsonAsync<ChapterRespData>(ApiPath.Chapter, query, "获取章节", ct);
    }

    /// <summary>获取 scramble_id（用于计算图片分块数）。</summary>
    public async Task<long> GetScrambleIdAsync(long id, CancellationToken ct = default)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var query = new Dictionary<string, object>
        {
            ["id"] = id,
            ["v"] = ts,
            ["mode"] = "vertical",
            ["page"] = 0,
            ["app_img_shunt"] = 1,
            ["express"] = "off",
        };
        using var httpResp = await JmRequestAsync(HttpMethod.Get, ApiPath.ScrambleId, query, null, ts, "获取scramble_id", ct);
        var body = await ReadAndVerifyAsync(httpResp, "获取scramble_id", ct);
        // 从 HTML 中提取 scramble_id，失败则使用默认值（与原版一致）
        const string marker = "var scramble_id = ";
        var idx = body.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var rest = body[(idx + marker.Length)..];
            var end = rest.IndexOf(';');
            if (end > 0 && long.TryParse(rest[..end], out var scrambleId))
            {
                return scrambleId;
            }
        }
        return 220_980;
    }

    /// <summary>获取收藏夹内容。</summary>
    public Task<FavoriteRespData> GetFavoriteFolderAsync(long folderId, long page, FavoriteSort sort, CancellationToken ct = default)
    {
        var query = new Dictionary<string, object>
        {
            ["page"] = page,
            ["o"] = sort.ToQueryString(),
            ["folder_id"] = folderId,
        };
        return JmGetJsonAsync<FavoriteRespData>(ApiPath.Favorite, query, "获取收藏夹", ct);
    }

    /// <summary>获取每周必看信息（分类 + 类型）。</summary>
    public Task<GetWeeklyInfoRespData> GetWeeklyInfoAsync(CancellationToken ct = default)
        => JmGetJsonAsync<GetWeeklyInfoRespData>(ApiPath.WeeklyInfo, null, "获取每周必看信息", ct);

    /// <summary>获取每周必看漫画列表。</summary>
    public Task<GetWeeklyRespData> GetWeeklyAsync(string categoryId, string typeId, CancellationToken ct = default)
    {
        var query = new Dictionary<string, object>
        {
            ["id"] = categoryId,
            ["type"] = typeId,
        };
        return JmGetJsonAsync<GetWeeklyRespData>(ApiPath.Weekly, query, "获取每周必看", ct);
    }

    /// <summary>获取本子评论分页（mode=all，total 为全部主评论数）。</summary>
    public Task<ForumRespData> GetAlbumCommentsAsync(long aid, long page, CancellationToken ct = default)
    {
        var query = new Dictionary<string, object>
        {
            ["mode"] = "all",
            ["page"] = page,
            ["aid"] = aid,
        };
        return JmGetJsonAsync<ForumRespData>(ApiPath.Forum, query, "获取评论", ct);
    }

    /// <summary>获取全站评论分页（不带本子限定）。</summary>
    public Task<ForumRespData> GetForumCommentsAsync(long page, CancellationToken ct = default)
    {
        var query = new Dictionary<string, object>
        {
            ["mode"] = "all",
            ["page"] = page,
        };
        return JmGetJsonAsync<ForumRespData>(ApiPath.Forum, query, "获取全站评论", ct);
    }

    /// <summary>收藏 / 取消收藏 漫画。</summary>
    public Task<ToggleFavoriteResp> ToggleFavoriteAlbumAsync(long aid, CancellationToken ct = default)
    {
        var form = new Dictionary<string, object> { ["aid"] = aid };
        return JmPostJsonAsync<ToggleFavoriteResp>(ApiPath.Favorite, null, form, "收藏/取消收藏", ct);
    }

    /// <summary>同步收藏夹（技巧：对同一漫画 toggle 两次，返回相反的 type 即成功）。</summary>
    public async Task SyncFavoriteFolderAsync(CancellationToken ct = default)
    {
        const long aid = 468_984;
        var task1 = ToggleFavoriteAlbumAsync(aid, ct);
        var task2 = ToggleFavoriteAlbumAsync(aid, ct);
        var (resp1, resp2) = (await task1, await task2);
        if (resp1.ToggleType == resp2.ToggleType)
        {
            throw new JmException($"同步收藏夹失败，两个请求都是`{resp1.ToggleType}`操作，请重试");
        }
    }

    /// <summary>图片下载用 HttpClient（无 Cookie，超时较长，简单重试）。</summary>
    public HttpClient CreateImageClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", JmConstants.UserAgent);
        return client;
    }
}



