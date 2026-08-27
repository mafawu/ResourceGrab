using System.Security.Cryptography;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M6: 资源类型
// ---------------------------------------------------------------------------

public enum VideoResourceKind
{
    Cover,
    Poster,
    PreviewImage,
    Trailer,
    ActorAvatar
}

// ---------------------------------------------------------------------------
// M6: 资源存储接口
// ---------------------------------------------------------------------------

public sealed class VideoResourceRecord
{
    public required string UrlHash { get; init; }
    public required string SourceUrl { get; init; }
    public required string LocalPath { get; init; }
    public required VideoResourceKind Kind { get; init; }
    public required string Sha256 { get; init; }
    public required long ByteSize { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface IVideoResourceStore
{
    /// <summary>获取或下载资源。若本地已有则直接返回路径。</summary>
    ValueTask<string?> GetOrDownloadAsync(string url, VideoResourceKind kind, CancellationToken ct);

    /// <summary>检查资源是否已存在。</summary>
    bool Exists(string url);

    /// <summary>获取资源记录。</summary>
    VideoResourceRecord? GetRecord(string url);

    /// <summary>获取所有资源记录。</summary>
    IReadOnlyList<VideoResourceRecord> GetAllRecords();

    /// <summary>清理无引用的资源文件。</summary>
    Task<int> CleanupAsync(Func<string, bool> isReferenced, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// M6: 磁盘资源存储实现
// ---------------------------------------------------------------------------

public sealed class DiskVideoResourceStore : IVideoResourceStore
{
    private readonly string _resourceDir;
    private readonly IVideoHttpClientFactory? _httpFactory;
    private readonly Dictionary<string, VideoResourceRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public DiskVideoResourceStore(string resourceDir, IVideoHttpClientFactory? httpFactory = null)
    {
        _resourceDir = resourceDir;
        _httpFactory = httpFactory;
        Directory.CreateDirectory(_resourceDir);
    }

    public bool Exists(string url) => _records.ContainsKey(ComputeUrlHash(url));

    public VideoResourceRecord? GetRecord(string url) =>
        _records.TryGetValue(ComputeUrlHash(url), out var record) ? record : null;

    public IReadOnlyList<VideoResourceRecord> GetAllRecords() => _records.Values.ToList();

    public async ValueTask<string?> GetOrDownloadAsync(string url, VideoResourceKind kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var urlHash = ComputeUrlHash(url);

        // 已有则直接返回
        if (_records.TryGetValue(urlHash, out var existing) && File.Exists(existing.LocalPath))
            return existing.LocalPath;

        if (_httpFactory is null) return null;

        var client = _httpFactory.Create("resource");
        var tempPath = Path.Combine(_resourceDir, $"{urlHash}.tmp");

        try
        {
            var bytes = await client.GetByteArrayAsync(url, ct);
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var ext = GuessExtension(url, bytes);
            var finalPath = Path.Combine(_resourceDir, $"{urlHash}{ext}");

            await File.WriteAllBytesAsync(finalPath, bytes, ct);

            var record = new VideoResourceRecord
            {
                UrlHash = urlHash,
                SourceUrl = url,
                LocalPath = finalPath,
                Kind = kind,
                Sha256 = sha256,
                ByteSize = bytes.Length
            };
            _records[urlHash] = record;

            // 清理临时文件
            if (File.Exists(tempPath)) File.Delete(tempPath);

            return finalPath;
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            return null;
        }
    }

    public async Task<int> CleanupAsync(Func<string, bool> isReferenced, CancellationToken ct = default)
    {
        var toRemove = _records.Values
            .Where(r => !isReferenced(r.LocalPath))
            .ToList();

        var removed = 0;
        foreach (var record in toRemove)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(record.LocalPath)) File.Delete(record.LocalPath);
                _records.Remove(record.UrlHash);
                removed++;
            }
            catch { }
        }
        return removed;
    }

    internal static string ComputeUrlHash(string url)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string GuessExtension(string url, byte[] bytes)
    {
        // 根据 URL 或文件头判断
        if (url.Contains(".webp", StringComparison.OrdinalIgnoreCase) ||
            (bytes.Length > 4 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46))
            return ".webp";
        if (url.Contains(".png", StringComparison.OrdinalIgnoreCase) ||
            (bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47))
            return ".png";
        return ".jpg";
    }
}
