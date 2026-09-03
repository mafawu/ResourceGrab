using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.App.Common;

/// <summary>
/// 本地 HLS 中继服务器。
/// <para>
/// 解决 LibVLC 3.x 的已知缺陷：HLS adaptive 模块发起的子播放列表 / 分片请求不会继承 media 级的
/// <c>:http-referrer</c>，导致强制校验 Referer 的 CDN（如 surrit.com）对子请求返回 403，播放报
/// "Failed to create demuxer"。本服务把 m3u8 链改写为指向 127.0.0.1，由本服务代发请求并注入
/// Referer / UA（用 curl 子进程取流，绕开 .NET SChannel 的 TLS 指纹拦截），VLC 只与本机通信。
/// </para>
/// <para>
/// 默认用 .NET HttpListener（对 VLC 的 HTTP 客户端最友好）；若因 Windows URL ACL 无法绑定，
/// 自动尝试 netsh add urlacl，仍失败则回退到改进版 TcpListener（Server 头 + Flush 优雅关闭）。
/// </para>
/// </summary>
public sealed class HlsLocalRelay : IDisposable
{
    private static readonly Lazy<HlsLocalRelay> Lazy = new(() => new HlsLocalRelay());
    public static HlsLocalRelay Instance => Lazy.Value;

    private readonly int _port;
    private readonly HttpListener? _listener;
    private readonly TcpListener? _tcpListener;
    private readonly Thread? _tcpThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, RelayTarget> _targets = new();
    private readonly ILogger? _logger = App.Services.GetService<ILogger>();

    private sealed record RelayTarget(string Token, string Host, Uri BaseUri, string? Referer, string? ProxyUrl);

    private HlsLocalRelay()
    {
        _port = FindFreePort();
        if (TryStartHttpListener(_port, out var listener))
        {
            _listener = listener;
            listener.Start();
            _ = Task.Run(AcceptHttpLoop);
            _logger?.Info($"[HlsLocalRelay] 已启动(HTTPListener) 本地 HLS 中继 http://127.0.0.1:{_port}/");
        }
        else
        {
            _tcpListener = new TcpListener(IPAddress.Loopback, _port);
            _tcpListener.Start();
            _tcpThread = new Thread(AcceptTcpLoop) { IsBackground = true, Name = "HlsLocalRelay" };
            _tcpThread.Start();
            _logger?.Info($"[HlsLocalRelay] 已启动(回退TcpListener) 本地 HLS 中继 http://127.0.0.1:{_port}/");
        }
    }

    public string Register(Uri streamUrl, string? referer, string? proxyUrl)
    {
        var token = Guid.NewGuid().ToString("N");
        var baseUri = new Uri(streamUrl, ".");
        _targets[token] = new RelayTarget(token, streamUrl.Host, baseUri, referer, proxyUrl);
        return $"http://127.0.0.1:{_port}/{token}/playlist.m3u8";
    }

    public void Unregister(string? localUrl)
    {
        var token = ExtractToken(localUrl);
        if (token is not null) _targets.TryRemove(token, out _);
    }

    private static string? ExtractToken(string? localUrl)
    {
        if (string.IsNullOrEmpty(localUrl)) return null;
        var m = Regex.Match(localUrl, @"^https?://127\.0\.0\.1:\d+/([^/]+)/");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static int FindFreePort()
    {
        var tmp = new TcpListener(IPAddress.Loopback, 0);
        tmp.Start();
        var port = ((IPEndPoint)tmp.LocalEndpoint).Port;
        tmp.Stop();
        return port;
    }

    // ---- HttpListener 模式 ----

    private bool TryStartHttpListener(int port, out HttpListener? listener)
    {
        listener = null;
        try
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{port}/");
            l.Start();
            listener = l;
            return true;
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            _logger?.Warn($"[HlsLocalRelay] HttpListener 需要 URL ACL（AccessDenied），尝试 netsh add urlacl: {ex.Message}");
            if (AddUrlAcl(port))
            {
                try
                {
                    var l2 = new HttpListener();
                    l2.Prefixes.Add($"http://127.0.0.1:{port}/");
                    l2.Start();
                    listener = l2;
                    return true;
                }
                catch (Exception ex2) { _logger?.Warn($"[HlsLocalRelay] 添加 ACL 后仍失败: {ex2.Message}"); }
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[HlsLocalRelay] HttpListener 启动失败: {ex.Message}");
            return false;
        }
    }

    private bool AddUrlAcl(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"http add urlacl url=http://127.0.0.1:{port}/ user=Everyone",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Exception ex) { _logger?.Warn($"[HlsLocalRelay] netsh add urlacl 失败: {ex.Message}"); return false; }
    }

    private async Task AcceptHttpLoop()
    {
        while (!_cts.IsCancellationRequested && _listener != null)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => ServeHttp(ctx));
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                _logger?.Warn($"[HlsLocalRelay] GetContext 异常: {ex.Message}");
            }
        }
    }

    private async Task ServeHttp(HttpListenerContext ctx)
    {
        try
        {
            var localPath = ctx.Request.Url?.LocalPath ?? "/";
            var m = Regex.Match(localPath, @"^/([^/]+)/(.*)$");
            if (!m.Success || !_targets.TryGetValue(m.Groups[1].Value, out var target))
            {
                ctx.Response.StatusCode = 404; ctx.Response.Close(); return;
            }
            var rest = m.Groups[2].Value;
            if (string.IsNullOrEmpty(rest)) rest = "playlist.m3u8";
            var (body, ct, status) = await Prepare(target, rest);
            if (status < 200 || status >= 300) { ctx.Response.StatusCode = 502; ctx.Response.Close(); return; }
            ctx.Response.ContentType = ct;
            ctx.Response.ContentLength64 = body.Length;
            using var os = ctx.Response.OutputStream;
            await os.WriteAsync(body, 0, body.Length);
            await os.FlushAsync();
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[HlsLocalRelay] ServeHttp 异常: {ex.Message}");
            try { ctx.Response.Abort(); } catch { }
        }
    }

    // ---- 回退 TcpListener 模式（改进版：Server 头 + Flush 优雅关闭） ----

    private void AcceptTcpLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = _tcpListener!.AcceptTcpClient(); }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger?.Warn($"[HlsLocalRelay] Accept 异常: {ex.Message}"); continue; }
            _ = Task.Run(() => HandleTcpClient(client));
        }
    }

    private async Task HandleTcpClient(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var requestLine = await ReadLineAsync(stream);
                if (requestLine is null) return;
                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteStatus(stream, 405, "Method Not Allowed"); return;
                }
                var localPath = parts[1];
                var m = Regex.Match(localPath, @"^/([^/]+)/(.*)$");
                if (!m.Success || !_targets.TryGetValue(m.Groups[1].Value, out var target))
                {
                    await WriteStatus(stream, 404, "Not Found"); return;
                }
                var rest = m.Groups[2].Value;
                if (string.IsNullOrEmpty(rest)) rest = "playlist.m3u8";
                var (body, ct, status) = await Prepare(target, rest);
                if (status < 200 || status >= 300) { await WriteStatus(stream, 502, "Bad Gateway"); return; }
                await WriteResponse(stream, status, ct, body);
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[HlsLocalRelay] 处理连接异常: {ex.Message}");
        }
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream)
    {
        var buf = new byte[1];
        var ms = new MemoryStream();
        while (true)
        {
            var n = await stream.ReadAsync(buf, 0, 1);
            if (n == 0) return null;
            if (buf[0] == '\n') break;
            if (buf[0] == '\r') continue;
            ms.WriteByte(buf[0]);
            if (ms.Length > 8192) break;
        }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static async Task WriteStatus(NetworkStream stream, int code, string reason)
    {
        var body = Encoding.ASCII.GetBytes(reason + "\n");
        await WriteResponse(stream, code, "text/plain", body);
    }

    private static async Task WriteResponse(NetworkStream stream, int code, string contentType, byte[] body)
    {
        var header = $"HTTP/1.1 {code} {ReasonPhrase(code)}\r\n" +
                     "Server: HlsLocalRelay\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Connection: close\r\n\r\n";
        var h = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(h, 0, h.Length);
        await stream.WriteAsync(body, 0, body.Length);
        await stream.FlushAsync();
    }

    // ---- 共用：取流 + 重写 ----

    private async Task<(byte[] Body, string Ct, int Status)> Prepare(RelayTarget target, string rest)
    {
        var absTarget = new Uri(target.BaseUri, rest);
        var (s, body, _) = await FetchAsync(target, absTarget);
        if (s < 200 || s >= 300)
        {
            if (!string.IsNullOrEmpty(target.ProxyUrl))
            {
                var r = await FetchAsync(target, absTarget, target.ProxyUrl);
                if (r.Status >= 200 && r.Status < 300) { s = r.Status; body = r.Body; }
                else return (Array.Empty<byte>(), "application/octet-stream", s);
            }
            else return (Array.Empty<byte>(), "application/octet-stream", s);
        }
        var ct = InferContentType(absTarget);
        if (absTarget.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || ct.Contains("mpegurl"))
        {
            var text = Encoding.UTF8.GetString(body);
            body = Encoding.UTF8.GetBytes(RewriteM3u8(text, target, absTarget));
            ct = "application/vnd.apple.mpegurl";
        }
        return (body, ct, s);
    }

    private const string RelayUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private async Task<(int Status, byte[] Body, string ContentType)> FetchAsync(RelayTarget target, Uri absTarget, string? proxyUrl = null)
    {
        var body = await CurlFetch(target, absTarget, proxyUrl);
        if (body is { Length: > 0 }) return (200, body, InferContentType(absTarget));
        return (502, Array.Empty<byte>(), "application/octet-stream");
    }

    private async Task<byte[]?> CurlFetch(RelayTarget target, Uri absTarget, string? proxyUrl)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "curl",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add("--noproxy"); psi.ArgumentList.Add("*");
        psi.ArgumentList.Add("-A"); psi.ArgumentList.Add(RelayUserAgent);
        if (!string.IsNullOrEmpty(target.Referer)) { psi.ArgumentList.Add("-e"); psi.ArgumentList.Add(target.Referer); }
        if (!string.IsNullOrEmpty(proxyUrl)) { psi.ArgumentList.Add("-x"); psi.ArgumentList.Add(proxyUrl); }
        psi.ArgumentList.Add(absTarget.AbsoluteUri);
        try
        {
            using var p = Process.Start(psi)!;
            using var ms = new MemoryStream();
            await p.StandardOutput.BaseStream.CopyToAsync(ms);
            await p.WaitForExitAsync();
            _logger?.Info($"[HlsLocalRelay] curl {absTarget} -> exit={p.ExitCode} len={ms.Length} {(proxyUrl is null ? "直连" : "代理")}");
            return p.ExitCode == 0 ? ms.ToArray() : null;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[HlsLocalRelay] curl 启动失败 {absTarget}: {ex.Message}");
            return null;
        }
    }

    private static string InferContentType(Uri absTarget) =>
        absTarget.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            ? "application/vnd.apple.mpegurl"
            : "application/octet-stream";

    private string RewriteM3u8(string text, RelayTarget target, Uri absTargetForRelative)
    {
        var baseForRelative = new Uri(absTargetForRelative, ".");
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw;
            if (line.StartsWith("#"))
            {
                line = Regex.Replace(line, @"URI=""([^""]+)""",
                    mm => $"URI=\"{ToLocal(target, baseForRelative, mm.Groups[1].Value)}\"");
                sb.AppendLine(line);
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine(line);
            }
            else
            {
                sb.AppendLine(ToLocal(target, baseForRelative, line.Trim()));
            }
        }
        return sb.ToString();
    }

    private string ToLocal(RelayTarget target, Uri baseForRelative, string uri)
    {
        Uri abs;
        try { abs = new Uri(baseForRelative, uri); }
        catch { return uri; }
        if (string.Equals(abs.Host, target.Host, StringComparison.OrdinalIgnoreCase))
            return $"http://127.0.0.1:{_port}/{target.Token}{abs.PathAndQuery}";
        return uri;
    }

    private static string ReasonPhrase(int code) => code switch
    {
        200 => "OK",
        404 => "Not Found",
        405 => "Method Not Allowed",
        502 => "Bad Gateway",
        _ => "Status",
    };

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Close(); } catch { }
        try { _tcpListener?.Stop(); } catch { }
    }
}
