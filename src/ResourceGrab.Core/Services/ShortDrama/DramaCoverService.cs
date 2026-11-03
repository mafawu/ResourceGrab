using System.Collections.Concurrent;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Sources.ShortDrama;

namespace ResourceGrab.Core.Services.ShortDrama;

/// <summary>
/// 短剧封面缓存：下载 CDN 的**加密字节** → 本地解密 → 存成真正的 jpg。
/// <para>
/// 为什么要落盘：封面地址带短签名（每次渲染都变），而且每张都要走一次「下载 + 解密」，
/// 不缓存的话网格每滚一次就重下一遍。缓存键用**剧集 id**（不是签名 URL），所以永远命中。
/// </para>
/// </summary>
public sealed class DramaCoverService
{
    private const int Concurrency = 4;
    private const int MaxConcurrentDownloads = 6;

    private readonly HttpClient _http;
    private readonly ILogger? _logger;

    private readonly SemaphoreSlim _gate = new(Concurrency, Concurrency);
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();

    public DramaCoverService(HttpClient http, ILogger? logger = null, string? cacheDir = null)
    {
        _http = http;
        _logger = logger;
        CacheDir = string.IsNullOrWhiteSpace(cacheDir) ? DefaultCacheDir : cacheDir;
        Directory.CreateDirectory(CacheDir);
    }

    /// <summary>默认缓存目录（config\drama-covers）。</summary>
    public static string DefaultCacheDir => AppPaths.ShortDramaCoverDir;

    /// <summary>实际缓存目录（测试可覆盖）。</summary>
    public string CacheDir { get; }

    /// <summary>同步查缓存：列表页建卡片时用它，命中就不用发请求（零成本）。</summary>
    public string? TryGetCached(string? dramaId) => FindFile(dramaId);

    /// <summary>
    /// 确保本地有这部戏的封面。已缓存直接返回路径；
    /// 并发调用同一部戏只会跑一次请求；失败返回 null（调用方继续用渐变兜底）。
    /// </summary>
    public Task<string?> EnsureAsync(string dramaId, string? coverUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dramaId)) return Task.FromResult<string?>(null);
        if (FindFile(dramaId) is { } cached) return Task.FromResult<string?>(cached);
        if (string.IsNullOrWhiteSpace(coverUrl)) return Task.FromResult<string?>(null);

        return _inflight.GetOrAdd(dramaId, id =>
            Task.Run(() => DownloadAndDecryptAsync(id, coverUrl, ct), CancellationToken.None));
    }

    private async Task<string?> DownloadAndDecryptAsync(string dramaId, string coverUrl, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                byte[]? plain;
                using (var request = new HttpRequestMessage(HttpMethod.Get, coverUrl))
                {
                    // 客户端已有默认 UA 时不能再加一遍（重复头会被 CDN 打进"地址发布页"）
                    if (!_http.DefaultRequestHeaders.UserAgent.Any())
                        request.Headers.TryAddWithoutValidation("User-Agent", XiguaDramaConstants.UserAgent);
                    request.Headers.TryAddWithoutValidation("Referer", XiguaDramaConstants.DefaultBaseUrl + "/");

                    using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var raw = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    if (raw.Length < 512) return null;

                    plain = DramaCoverCipher.TryDecrypt(raw);
                    if (plain is null)
                    {
                        _logger?.Warn($"[DramaCover] {dramaId} 解密失败（{raw.Length} 字节），继续用兜底色");
                        return null;
                    }
                }

                Directory.CreateDirectory(CacheDir);
                var target = PathFor(dramaId, plain);
                var temp = target + ".tmp";
                await File.WriteAllBytesAsync(temp, plain, ct).ConfigureAwait(false);
                File.Move(temp, target, overwrite: true);
                _logger?.Info($"[DramaCover] {dramaId} 封面已解密并缓存（{plain.Length / 1024} KB）");
                return target;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[DramaCover] {dramaId} 封面处理失败：{ex.Message}");
            return null;
        }
        finally
        {
            _inflight.TryRemove(dramaId, out _);
        }
    }

    /// <summary>缓存文件名按图片真实类型给扩展名，WPF 解码更稳（也方便排查）。</summary>
    private string PathFor(string dramaId, byte[] plain)
    {
        var ext = plain.Length >= 3 && plain[0] == 0xFF && plain[1] == 0xD8 ? ".jpg"
            : plain.Length >= 8 && plain[0] == 0x89 && plain[1] == 0x50 ? ".png"
            : plain.Length >= 12 && plain[8] == 0x57 && plain[9] == 0x45 ? ".webp"
            : ".img";
        return Path.Combine(CacheDir, SafeName(dramaId) + ext);
    }

    private string? FindFile(string? dramaId)
    {
        if (string.IsNullOrWhiteSpace(dramaId)) return null;
        var baseName = Path.Combine(CacheDir, SafeName(dramaId));
        foreach (var ext in new[] { ".jpg", ".png", ".webp", ".img" })
        {
            var candidate = baseName + ext;
            if (File.Exists(candidate) && new FileInfo(candidate).Length > 1024) return candidate;
        }
        return null;
    }

    private static string SafeName(string dramaId)
    {
        var safe = new string(dramaId.Where(char.IsLetterOrDigit).ToArray());
        return safe.Length == 0 ? "unknown" : safe;
    }
}
