using System.IO;
using System.Net.Http;
using ResourceGrab.Core;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.App.Common;

/// <summary>
/// 相关推荐封面磁盘缓存：在线封面 URL 只下载一次，落盘在 config/related-covers/，
/// 之后推荐页直接读本地文件，不再联网（配合 manga-relations-cache.json 做到二次打开零网络）。
/// </summary>
public sealed class RelatedCoverCache
{
    private static readonly string[] KnownExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" };

    private static readonly HttpClient HttpClient = CreateClient();

    private readonly ILogger? _logger;

    public RelatedCoverCache(ILogger? logger = null) => _logger = logger;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", JmConstants.UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("referer", "https://18comic.vip/");
        return client;
    }

    /// <summary>返回已缓存的本地封面路径；未缓存返回 null。</summary>
    public string? TryGet(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var safe = Sanitize(id);
        foreach (var ext in KnownExtensions)
        {
            var path = Path.Combine(AppPaths.RelatedCoversDir, safe + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>确保封面已缓存；已存在直接返回本地路径，否则下载一次落盘（失败返回 null）。</summary>
    public async Task<string?> EnsureAsync(string id, string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url)) return null;
        if (TryGet(id) is { } cached) return cached;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0) return null;

            var ext = ResolveExtension(url, resp.Content.Headers.ContentType?.MediaType);
            Directory.CreateDirectory(AppPaths.RelatedCoversDir);
            var path = Path.Combine(AppPaths.RelatedCoversDir, Sanitize(id) + ext);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, path, true);
            return path;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[RelatedCover] 下载封面失败 {id}: {ex.Message}");
            return null;
        }
    }

    private static string ResolveExtension(string url, string? mediaType)
    {
        var mapped = mediaType?.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            _ => null,
        };
        if (mapped is not null) return mapped;

        var urlExt = Path.GetExtension(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url)
            .ToLowerInvariant();
        return KnownExtensions.Contains(urlExt) ? urlExt : ".jpg";
    }

    private static string Sanitize(string id)
    {
        var chars = id.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }
}
