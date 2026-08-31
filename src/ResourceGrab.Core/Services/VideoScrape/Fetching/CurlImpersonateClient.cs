using System.Diagnostics;
using System.Net;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.Core.Services.VideoScrape.Fetching;

// ---------------------------------------------------------------------------
// 阶段0: curl-impersonate 子进程客户端（TLS 指纹层 L2）。
// .NET 没有 curl_cffi 等价物，用打了浏览器指纹补丁的 curl 二进制代替：
// 它的 JA3/TLS 握手模拟 Chrome，能通过 Cloudflare 的被动指纹检测
// （主动 JS 挑战仍需要 L3 浏览器渲染，暂未实现）。
// Windows 构建可执行文件（如 curl_chrome116.exe）由用户放置在
// {AppDataDir}/curl-impersonate/ 或应用目录 assets/curl-impersonate/ 下；
// 未探测到则 Available=false，调用方透明跳过 L2，应用必须能在无 L2 时正常工作。
// ---------------------------------------------------------------------------

public sealed class CurlImpersonateClient
{
    private readonly string? _exePath;
    private readonly int _timeoutSeconds;
    private readonly ILogger? _logger;

    public CurlImpersonateClient(int timeoutSeconds = 30, ILogger? logger = null)
    {
        _timeoutSeconds = Math.Clamp(timeoutSeconds, 5, 120);
        _logger = logger;
        _exePath = ProbeExecutable();
        if (_exePath is null)
            _logger?.Info("[Fetcher] 未找到 curl-impersonate，TLS 指纹层（L2）不可用");
        else
            _logger?.Info($"[Fetcher] curl-impersonate 就绪: {_exePath}");
    }

    public bool Available => _exePath is not null;

    private static string? ProbeExecutable()
    {
        var dirs = new[]
        {
            Path.Combine(AppPaths.AppDataDir, "curl-impersonate"),
            Path.Combine(AppContext.BaseDirectory, "assets", "curl-impersonate"),
        };
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("curl_chrome", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("curl-impersonate-chrome", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("curl_firefox", StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }
        return null;
    }

    /// <summary>
    /// 发起 GET。不用 --fail：要拿到 403 挑战页内容供 BlockDetector 判定；
    /// 状态码通过 -w 尾标（"\n&gt;&gt;&gt;{http_code}&lt;&lt;&lt;"）从输出末尾解析。
    /// 注意：不要传 User-Agent —— impersonate 构建自带与 TLS 指纹匹配的浏览器 UA。
    /// </summary>
    public Task<FetchResult> GetAsync(string url, string? cookieHeader = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null, string? proxy = null,
        CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, url, null, null, cookieHeader, extraHeaders, proxy, ct);

    /// <summary>
    /// POST 变体：同 GetAsync，附加 -X POST、Content-Type 与 --data。
    /// </summary>
    public Task<FetchResult> PostAsync(string url, string contentType, string body, string? cookieHeader = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null, string? proxy = null,
        CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, url, contentType, body, cookieHeader, extraHeaders, proxy, ct);

    private async Task<FetchResult> SendAsync(HttpMethod method, string url, string? contentType, string? body,
        string? cookieHeader, IReadOnlyDictionary<string, string>? extraHeaders, string? proxy, CancellationToken ct)
    {
        if (_exePath is null)
            throw new HttpRequestException("curl-impersonate 不可用（未找到可执行文件）");

        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-sS");
        psi.ArgumentList.Add("-L");
        psi.ArgumentList.Add("--compressed");
        psi.ArgumentList.Add("--max-time");
        psi.ArgumentList.Add(_timeoutSeconds.ToString());
        if (method == HttpMethod.Post)
        {
            psi.ArgumentList.Add("-X");
            psi.ArgumentList.Add("POST");
            if (!string.IsNullOrEmpty(contentType))
            {
                psi.ArgumentList.Add("-H");
                psi.ArgumentList.Add($"Content-Type: {contentType}");
            }
            if (!string.IsNullOrEmpty(body))
            {
                psi.ArgumentList.Add("--data");
                psi.ArgumentList.Add(body);
            }
        }
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            psi.ArgumentList.Add("-x");
            psi.ArgumentList.Add(proxy);
        }
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add($"Cookie: {cookieHeader}");
        }
        if (extraHeaders is not null)
        {
            foreach (var header in extraHeaders)
            {
                psi.ArgumentList.Add("-H");
                psi.ArgumentList.Add($"{header.Key}: {header.Value}");
            }
        }
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add("\n>>>{http_code}<<<");
        psi.ArgumentList.Add(url);

        using var process = Process.Start(psi)!;
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = await outputTask;
        var errors = await errorTask;

        const string open = ">>>";
        const string close = "<<<";
        var sep = output.LastIndexOf(open, StringComparison.Ordinal);
        if (sep >= 0)
        {
            var tail = output[(sep + open.Length)..];
            var end = tail.IndexOf(close, StringComparison.Ordinal);
            if (end >= 0 && int.TryParse(tail[..end].Trim(), out var status))
            {
                var responseBody = output[..sep].TrimStart('\r', '\n');
                return new FetchResult(status, responseBody, FetchTier.CurlImpersonate);
            }
        }

        if (process.ExitCode != 0)
            throw new HttpRequestException(
                $"curl-impersonate 请求失败 ({process.ExitCode}): {errors[..Math.Min(errors.Length, 200)]}");
        throw new HttpRequestException("curl-impersonate 响应无法解析状态码");
    }
}
