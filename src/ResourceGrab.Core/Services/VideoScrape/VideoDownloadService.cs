using System.Diagnostics;
using System.Text.RegularExpressions;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Utils;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// 在线视频直存下载：ffmpeg 经 HLS 本地中继（App 层注入）或直连拉流，转封装为 mp4。
// HLS 用流复制（-c copy），不转码；MP4 直链同样复制封装。
// ---------------------------------------------------------------------------

/// <summary>下载请求（UI 层从在线详情组装）。
/// MasterUrl + VariantLabel 用于执行时重新解析：CDN 分片地址常带时效签名，
/// 点击选档到真正执行之间地址可能过期，重探一次拿新鲜地址。</summary>
public sealed record VideoDownloadRequest(
    string Number,
    string Title,
    string StreamUrl,
    string Referer = "",
    string? Proxy = null,
    string DurationText = "",
    string MasterUrl = "",
    string VariantLabel = "");

/// <summary>下载进度（Percent &lt; 0 表示总时长未知，仅上报已下载字节与速率；
/// EtaSeconds &lt; 0 表示无法估算剩余时间）。</summary>
public sealed record VideoDownloadProgress(
    double Percent, long DownloadedBytes, double SpeedBytesPerSec, double EtaSeconds = -1);

/// <summary>下载结果。</summary>
public sealed record VideoDownloadResult(bool Skipped, string OutputPath);

/// <summary>HLS 主 playlist 里的一档码率。</summary>
public sealed record VideoVariantInfo(
    string Label,
    string Uri,
    long BandwidthBps,
    long? EstimatedBytes);

public sealed class VideoDownloadService
{
    /// <summary>在线视频默认落盘目录（AppDataDir 下独立子目录，不污染漫画下载目录）。</summary>
    public static string DefaultDownloadDir => Path.Combine(AppPaths.AppDataDir, "视频下载");

    /// <summary>兼容旧调用点的默认目录（等同 DefaultDownloadDir）。</summary>
    public static string DownloadDir => DefaultDownloadDir;

    /// <summary>解析生效的下载目录：配置为空/非法时回退默认目录。</summary>
    public static string ResolveDownloadDir(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var full = Path.GetFullPath(configured.Trim());
                if (!string.IsNullOrEmpty(full)) return full;
            }
            catch { }
        }
        return DefaultDownloadDir;
    }

    /// <summary>输出文件名总长上限（含扩展名），超长截断标题部分。</summary>
    private const int MaxFileNameLength = 120;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly Func<Uri, string?, string?, string>? _hlsRelayRegister;
    private readonly Action<string?>? _hlsRelayUnregister;
    private readonly Func<string>? _downloadDirProvider;
    private readonly ILogger? _logger;

    /// <param name="hlsRelayRegister">HLS 本地中继注册（App 层 HlsLocalRelay.Instance.Register），null 则直连。</param>
    /// <param name="hlsRelayUnregister">中继注销，下载结束（成功/失败/取消）后调用。</param>
    /// <param name="downloadDirProvider">生效下载目录提供方（读配置，改设置即时生效）；null 则用默认目录。</param>
    public VideoDownloadService(
        Func<Uri, string?, string?, string>? hlsRelayRegister = null,
        Action<string?>? hlsRelayUnregister = null,
        ILogger? logger = null,
        Func<string>? downloadDirProvider = null)
    {
        _hlsRelayRegister = hlsRelayRegister;
        _hlsRelayUnregister = hlsRelayUnregister;
        _logger = logger;
        _downloadDirProvider = downloadDirProvider;
    }

    /// <summary>ffmpeg 是否可用（PATH 与程序目录查找）。</summary>
    public static string? FindFfmpeg() => VideoMetadataReader.FindTool("ffmpeg");

    /// <summary>
    /// 探测主 playlist 的清晰度档位（BANDWIDTH/RESOLUTION 逐档解析，逐档 URI 解析为绝对地址）。
    /// 直连被拦时经中继取（中继用 curl 代发，和播放链路一致）。无变体/取不到返回空列表。
    /// </summary>
    public async Task<IReadOnlyList<VideoVariantInfo>> ProbeVariantsAsync(
        string masterUrl, string referer, string? proxy, string durationText,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(masterUrl, UriKind.Absolute, out var masterUri)) return [];
        var (text, relayPrefix) = await FetchPlaylistAsync(masterUri, referer, proxy, ct);
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (relayPrefix is not null)
        {
            // 经中继取回的文本里分片地址是本地改写地址（且探测结束即注销，已死亡）：
            // 换回上游基址再解析，保证返回的永远是 CDN 真地址
            text = RewriteRelayPrefix(text, relayPrefix, new Uri(masterUri, ".").AbsoluteUri);
        }
        var durationSec = ParseDurationSeconds(durationText);
        var variants = ParseVariants(text, masterUri);
        return variants
            .Select(v => v with
            {
                EstimatedBytes = durationSec is > 0
                    ? (long?)(v.BandwidthBps / 8.0 * durationSec.Value)
                    : null,
            })
            .ToList();
    }

    /// <returns>playlist 文本；经中继时附带本地前缀（形如 http://127.0.0.1:端口/token/），供调用方换回上游基址。</returns>
    private async Task<(string? Text, string? RelayPrefix)> FetchPlaylistAsync(
        Uri masterUri, string referer, string? proxy, CancellationToken ct)
    {
        // 先直连（Referer/UA 透传）；被拦则走中继（curl 代发）
        var direct = await TryFetchPlaylistAsync(masterUri.AbsoluteUri, referer, ct);
        if (direct is not null) return (direct, null);
        if (_hlsRelayRegister is null) return (null, null);
        string? relayUrl = null;
        try
        {
            relayUrl = _hlsRelayRegister(masterUri,
                string.IsNullOrEmpty(referer) ? null : referer, proxy);
            var text = await TryFetchPlaylistAsync(relayUrl, referer, ct);
            if (text is null) return (null, null);
            // 本地前缀 = 中继地址去掉末尾文件名（Register 保证以 /token/文件名 结尾）
            var slash = relayUrl.LastIndexOf('/');
            var prefix = slash > 0 ? relayUrl[..(slash + 1)] : null;
            // 文件名本身也可能出现在文本里（如自引用），只替换“前缀+相对路径”形态：
            // 前缀以 /token/ 结尾，误伤概率可忽略
            return (text, prefix);
        }
        catch { return (null, null); }
        finally
        {
            if (relayUrl is not null)
            {
                try { _hlsRelayUnregister?.Invoke(relayUrl); } catch { }
            }
        }
    }

    private static async Task<string?> TryFetchPlaylistAsync(string url, string referer, CancellationToken ct)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            if (!string.IsNullOrEmpty(referer)) req.Headers.Referrer = new Uri(referer);
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync(ct);
            return text.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase) ? text : null;
        }
        catch { return null; }
    }

    /// <summary>把中继改写文本里的本地前缀换回上游基址（探测结束即注销，本地地址已死亡不可用）。</summary>
    internal static string RewriteRelayPrefix(string text, string relayPrefix, string upstreamBase) =>
        text.Replace(relayPrefix, upstreamBase, StringComparison.Ordinal);

    /// <summary>解析 #EXT-X-STREAM-INF（含 BANDWIDTH/RESOLUTION）与其后一行 URI。</summary>
    internal static IReadOnlyList<VideoVariantInfo> ParseVariants(string playlistText, Uri masterUri)
    {
        var result = new List<VideoVariantInfo>();
        string? pendingAttrs = null;
        foreach (var raw in playlistText.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                pendingAttrs = line;
                continue;
            }
            if (pendingAttrs is null || line.Length == 0 || line.StartsWith("#")) continue;
            var attrs = pendingAttrs;
            pendingAttrs = null;
            if (!Uri.TryCreate(masterUri, line, out var abs)) continue;
            var bw = Regex.Match(attrs, @"BANDWIDTH=(\d+)");
            var res = Regex.Match(attrs, @"RESOLUTION=\d+x(\d+)");
            var bps = bw.Success && long.TryParse(bw.Groups[1].Value, out var b) ? b : 0;
            var label = res.Success ? $"{res.Groups[1].Value}p" : bps > 0 ? $"{bps / 1000}kbps" : "默认";
            result.Add(new VideoVariantInfo(label, abs.AbsoluteUri, bps, null));
        }
        // 同一清晰度去重（保留首个），按码率降序方便菜单与默认选择
        return result.GroupBy(v => v.Label).Select(g => g.First())
            .OrderByDescending(v => v.BandwidthBps).ToList();
    }

    /// <summary>按档位名从主 playlist 重探新鲜分片地址；失败返回 null（调用方沿用旧地址）。</summary>
    private async Task<string?> RefreshVariantUrlAsync(VideoDownloadRequest request, CancellationToken ct)
    {
        try
        {
            var variants = await ProbeVariantsAsync(
                request.MasterUrl, request.Referer, request.Proxy, request.DurationText, ct);
            var hit = variants.FirstOrDefault(v =>
                string.Equals(v.Label, request.VariantLabel, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.Uri;
            _logger?.Warn($"[VideoDownload] 主 playlist 里找不到档位 {request.VariantLabel}，沿用旧地址");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[VideoDownload] 重探分片地址失败，沿用旧地址: {ex.Message}");
            return null;
        }
    }

    /// <summary>速率友好显示（看板/日志用）。</summary>
    public static string FormatBytes(double bytesPerSec) =>
        bytesPerSec < 1024 ? $"{bytesPerSec:F0} B/s"
        : bytesPerSec < 1024 * 1024 ? $"{bytesPerSec / 1024:F1} KB/s"
        : $"{bytesPerSec / 1024 / 1024:F2} MB/s";

    /// <summary>累计字节量友好显示。</summary>
    public static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:F1} KB"
        : bytes < 1024L * 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} MB"
        : $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";

    /// <summary>剩余秒数友好显示（3661 → "1:01:01"，65 → "1:05"）。</summary>
    public static string FormatEta(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{(int)t.Minutes:D2}:{(int)t.Seconds:D2}"
            : $"{(int)t.TotalMinutes}:{(int)t.Seconds:D2}";
    }

    /// <summary>按番号+标题生成落盘路径（目录自动创建）。</summary>
    public static string BuildOutputPath(string number, string title) =>
        BuildOutputPath(number, title, DefaultDownloadDir);

    /// <summary>按番号+标题生成落盘路径（目录自动创建）。</summary>
    public static string BuildOutputPath(string number, string title, string directory)
    {
        var dir = ResolveDownloadDir(directory);
        Directory.CreateDirectory(dir);
        var name = string.IsNullOrWhiteSpace(number) ? title : $"{number} {title}";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = Regex.Replace(name.Trim(), @"\s+", " ");
        if (name.Length > MaxFileNameLength) name = name[..MaxFileNameLength].TrimEnd();
        if (string.IsNullOrWhiteSpace(name)) name = $"video-{DateTime.Now:yyyyMMdd-HHmmss}";
        return Path.Combine(dir, name + ".mp4");
    }

    public static bool IsHls(string url) =>
        url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析展示用时长文本为秒（"mm:ss" / "hh:mm:ss" / "N 分钟"）；解析不出返回 null。</summary>
    public static double? ParseDurationSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        // 分钟部分不限制两位数：搜索卡片的时长常是 "120:00" 这类纯分钟格式
        var m = Regex.Match(text, @"^(?:(\d+):)?(\d+):([0-5]\d)$");
        if (m.Success)
        {
            var h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
            return h * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value);
        }
        m = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*分钟");
        if (m.Success && double.TryParse(m.Groups[1].Value, out var min)) return min * 60;
        return null;
    }

    /// <summary>执行下载。目标文件已存在且非空则直接跳过；取消时清掉临时文件。</summary>
    public async Task<VideoDownloadResult> DownloadAsync(
        VideoDownloadRequest request,
        IProgress<VideoDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.StreamUrl))
            throw new InvalidOperationException("该详情没有可下载的流地址");
        if (!Uri.TryCreate(request.StreamUrl, UriKind.Absolute, out var streamUri))
            throw new InvalidOperationException("流地址无效，无法下载");

        // 分片地址可能已过期：用主 playlist 按档位重探一次，拿到新鲜地址再下；
        // 重探失败则沿用旧地址（错误信息里会提示重新打开详情）
        var streamUrl = request.StreamUrl;
        if (!string.IsNullOrEmpty(request.VariantLabel)
            && Uri.TryCreate(request.MasterUrl, UriKind.Absolute, out _))
        {
            var fresh = await RefreshVariantUrlAsync(request, ct);
            if (fresh is not null && !string.Equals(fresh, streamUrl, StringComparison.Ordinal))
            {
                _logger?.Info($"[VideoDownload] 分片地址已刷新（{request.VariantLabel}）");
                streamUrl = fresh;
            }
        }

        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
            throw new InvalidOperationException("未找到 ffmpeg，请先安装 FFmpeg 后再下载（视频库缩略图也依赖它）");

        var configuredDir = _downloadDirProvider?.Invoke();
        var outputPath = BuildOutputPath(request.Number, request.Title,
            string.IsNullOrEmpty(configuredDir) ? DefaultDownloadDir : configuredDir);
        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
        {
            _logger?.Info($"[VideoDownload] 已存在，跳过: {outputPath}");
            return new VideoDownloadResult(Skipped: true, outputPath);
        }

        var tmpPath = outputPath + ".downloading";
        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }

        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out streamUri))
            throw new InvalidOperationException("流地址无效，无法下载");
        if (streamUri.Host is "127.0.0.1" or "localhost")
            throw new InvalidOperationException("探测返回了已失效的中继地址，请重试下载");
        // 后续统一用刷新后的地址（request 不可变，用 with 复制）
        request = request with { StreamUrl = streamUrl };

        // HLS 优先走本地中继（Referer/代理由中继注入，与播放器同链路）；
        // MP4 直链把 Referer/UA 经 -headers 传给 ffmpeg。
        string? relayUrl = null;
        string inputUrl = request.StreamUrl;
        if (IsHls(request.StreamUrl) && _hlsRelayRegister is not null)
        {
            relayUrl = _hlsRelayRegister(streamUri,
                string.IsNullOrEmpty(request.Referer) ? null : request.Referer,
                request.Proxy);
            inputUrl = relayUrl;
        }

        var totalSeconds = ParseDurationSeconds(request.DurationText);
        _logger?.Info($"[VideoDownload] 总时长来源：{(totalSeconds is > 0 ? $"{totalSeconds:F0}s（详情时长“{request.DurationText}”）" : "未知（等 ffmpeg 自报 Duration）")} {request.Number}");
        try
        {
            // HLS 高速路：ffmpeg 直连（Referer/UA 经 -headers 透传，HTTP keepalive 复用连接）。
            // 直连被拦（403）才降级中继——中继每分片一次 curl 子进程+新建连接，慢一个数量级。
            if (IsHls(request.StreamUrl) && relayUrl is not null)
            {
                try
                {
                    await RunFfmpegAsync(ffmpeg, request.StreamUrl, request, tmpPath, totalSeconds,
                        progress, directHeaders: true, route: "直连", ct);
                }
                catch (InvalidOperationException ex) when (IsAccessDenied(ex.Message))
                {
                    _logger?.Info($"[VideoDownload] 直连被拦，切中继: {request.Number}");
                    await RunFfmpegAsync(ffmpeg, relayUrl, request, tmpPath, totalSeconds,
                        progress, directHeaders: false, route: "中继", ct);
                }
            }
            else
            {
                // MP4 直链，或无中继可用的 HLS：一律直连并透传 Referer/UA
                await RunFfmpegAsync(ffmpeg, inputUrl, request, tmpPath, totalSeconds,
                    progress, directHeaders: true, route: "直连", ct);
            }
            File.Move(tmpPath, outputPath, overwrite: true);
            _logger?.Info($"[VideoDownload] 完成: {outputPath}");
            return new VideoDownloadResult(Skipped: false, outputPath);
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
        catch (Exception)
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
        finally
        {
            if (relayUrl is not null)
            {
                try { _hlsRelayUnregister?.Invoke(relayUrl); } catch { }
            }
        }
    }

    /// <summary>输出被站点拒绝（403 类）的特征：值得降级中继重试，其他错误直接抛。</summary>
    private static bool IsAccessDenied(string message) =>
        message.Contains("403", StringComparison.Ordinal)
        || message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
        || message.Contains("401", StringComparison.Ordinal);

    private async Task RunFfmpegAsync(
        string ffmpeg, string inputUrl, VideoDownloadRequest request,
        string tmpPath, double? totalSeconds,
        IProgress<VideoDownloadProgress>? progress, bool directHeaders, string route, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add("-progress"); psi.ArgumentList.Add("pipe:1");
        // 部分站点的分片伪装成图片扩展名（如 surrit 的 video0.jpeg 实为 TS 流）：
        // ffmpeg 9 默认白名单 + extension_picky 会拒收，必须显式放行（仅作用于本次输入）。
        psi.ArgumentList.Add("-allowed_extensions"); psi.ArgumentList.Add("ALL");
        psi.ArgumentList.Add("-allowed_segment_extensions"); psi.ArgumentList.Add("ALL");
        psi.ArgumentList.Add("-extension_picky"); psi.ArgumentList.Add("0");
        // 卡住的分片最多等 15 秒后重连，避免无限挂起；断线自动重连
        psi.ArgumentList.Add("-rw_timeout"); psi.ArgumentList.Add("15000000");
        psi.ArgumentList.Add("-reconnect"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_streamed"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_delay_max"); psi.ArgumentList.Add("5");
        if (directHeaders)
        {
            // 直连（HLS 高速路 / MP4 直链）：Referer/UA 直接传给 ffmpeg 的 HTTP 客户端
            var headers = "";
            if (!string.IsNullOrEmpty(request.Referer)) headers += $"Referer: {request.Referer}\r\n";
            headers += $"User-Agent: {UserAgent}\r\n";
            psi.ArgumentList.Add("-headers"); psi.ArgumentList.Add(headers);
            if (!string.IsNullOrEmpty(request.Proxy)) { psi.ArgumentList.Add("-http_proxy"); psi.ArgumentList.Add(request.Proxy); }
        }
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(inputUrl);
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-bsf:a"); psi.ArgumentList.Add("aac_adtstoasc");
        // 临时文件后缀是 .downloading（避免被视频库扫描半成品），扩展名指望不上，必须显式指定封装
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("mp4");
        psi.ArgumentList.Add(tmpPath);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        using var _ = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });
        var stderr = new List<string>();
        // ffmpeg 在输入头部会自报 "Duration: 01:23:45.67"：详情没时长时就用它做进度/ETA 分母
        double? ffmpegDuration = null;
        var durationRegex = new Regex(@"Duration:\s*(\d+):(\d+):([\d.]+)", RegexOptions.Compiled);
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr) stderr.Add(e.Data);
            if (ffmpegDuration is null)
            {
                var m = durationRegex.Match(e.Data);
                if (m.Success
                    && int.TryParse(m.Groups[1].Value, out var h)
                    && int.TryParse(m.Groups[2].Value, out var min)
                    && double.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var sec))
                {
                    var total = h * 3600 + min * 60 + sec;
                    if (total > 0) ffmpegDuration = total;
                }
            }
        };

        _logger?.Info($"[VideoDownload] 启动 ffmpeg（{route}）：输入={RedactUrl(inputUrl)} 输出={Path.GetFileName(tmpPath)}");
        proc.Start();
        proc.BeginErrorReadLine();

        // -progress 输出走 stdout：解析 out_time_ms/total_size，节流上报避免刷爆看板；
        // 速率按相邻两次上报的字节差/时间差估算
        var lastReport = DateTimeOffset.MinValue;
        var lastBytes = 0L;
        var lastTime = DateTimeOffset.UtcNow;
        // ETA 基准：首次上报时的墙钟与媒体时长，速率 = 媒体秒/墙钟秒
        var etaStartWall = DateTimeOffset.UtcNow;
        var etaStartMedia = 0.0;
        var etaStarted = false;
        long downloadedBytes = 0;
        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
        {
            if (line.StartsWith("total_size=", StringComparison.Ordinal)
                && long.TryParse(line["total_size=".Length..].Trim(), out var bytes))
            {
                downloadedBytes = bytes;
                continue;
            }
            if (!line.StartsWith("out_time_ms=", StringComparison.Ordinal)) continue;
            if (!long.TryParse(line["out_time_ms=".Length..].Trim(), out var ms)) continue;
            var now = DateTimeOffset.UtcNow;
            if (now - lastReport < TimeSpan.FromMilliseconds(1000)) continue;
            var mediaSec = ms / 10000.0;
            if (!etaStarted) { etaStarted = true; etaStartWall = now; etaStartMedia = mediaSec; }
            var dt = (now - lastTime).TotalSeconds;
            var speed = dt > 0 ? (downloadedBytes - lastBytes) / dt : 0;
            lastReport = now;
            lastTime = now;
            lastBytes = downloadedBytes;
            var effectiveTotal = totalSeconds is > 0 ? totalSeconds : ffmpegDuration;
            var percent = effectiveTotal is > 0 ? Math.Min(100, mediaSec / effectiveTotal.Value) : -1;
            // 剩余墙钟 ≈ 剩余媒体时长 / 媒体推进速率；速率过低时不估算
            double eta = -1;
            if (effectiveTotal is > 0)
            {
                var wallElapsed = (now - etaStartWall).TotalSeconds;
                var mediaElapsed = mediaSec - etaStartMedia;
                var rate = wallElapsed > 2 ? mediaElapsed / wallElapsed : 0;
                if (rate > 0.05) eta = (effectiveTotal.Value - mediaSec) / rate;
            }
            progress?.Report(new VideoDownloadProgress(percent, downloadedBytes, Math.Max(0, speed), eta));
        }
        await proc.WaitForExitAsync(ct);
        // 被取消时 ffmpeg 以非零码退出（kill），必须优先判取消，否则会被当成失败重试
        ct.ThrowIfCancellationRequested();

        if (proc.ExitCode != 0)
        {
            string all;
            lock (stderr) all = string.Join('\n', stderr.TakeLast(40));
            // 完整上下文进日志，面向用户的异常只带关键行 + 文件名（看板/Toast 可读）
            _logger?.Error($"[VideoDownload] ffmpeg exit={proc.ExitCode} 输出={tmpPath}\n{all}", null);
            var hint = HintFor(all);
            throw new InvalidOperationException(
                $"{hint}（输出：{Path.GetFileName(tmpPath)}，详见日志）");
        }
        try
        {
            var finalSize = new FileInfo(tmpPath).Length;
            progress?.Report(new VideoDownloadProgress(100, finalSize, 0));
        }
        catch
        {
            progress?.Report(new VideoDownloadProgress(100, 0, 0));
        }
    }

    /// <summary>URL 脱敏：公网地址只保留 host + 首段路径；
    /// 本地中继地址全量记录（含 token 与文件名），否则 404 定位不下去。</summary>
    private static string RedactUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "(无效地址)";
        if (u.Host is "127.0.0.1" or "localhost") return url;
        var path = u.AbsolutePath;
        var cut = path.IndexOf('/', 1);
        if (cut > 0) path = path[..cut] + "/…";
        return $"{u.Scheme}://{u.Host}{path}";
    }

    /// <summary>把 ffmpeg 尾部日志翻译成可操作的中文提示。</summary>
    private static string HintFor(string stderrTail)
    {
        if (stderrTail.Contains("403", StringComparison.Ordinal))
            return "下载被站点拒绝（403）：多为 Referer/代理问题，请检查视频刮削设置的代理后重试";
        if (stderrTail.Contains("404", StringComparison.Ordinal))
            return "流地址已失效（404）：请重新打开详情（刷新流地址）后再下载";
        if (stderrTail.Contains("Invalid data", StringComparison.OrdinalIgnoreCase))
            return "流数据无效：站点可能更换了播放格式，请重新打开详情后再试";
        var first = stderrTail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.IsNullOrEmpty(first) ? "ffmpeg 下载失败" : $"ffmpeg 下载失败：{first}";
    }
}
