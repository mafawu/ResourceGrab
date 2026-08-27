using System.Text.Json;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M4: 磁盘 JSON 快照缓存
// 路径: config/video-source-cache/<number>/<sourceId>[_<language>].json
// ---------------------------------------------------------------------------

public sealed class DiskJsonSnapshotCache : IVideoSourceSnapshotCache
{
    private readonly string _cacheDir;
    private readonly ILogger? _logger;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private const string SchemaVersion = "v1";
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

    public DiskJsonSnapshotCache(string cacheDir, ILogger? logger = null)
    {
        _cacheDir = cacheDir;
        _logger = logger;
        Directory.CreateDirectory(_cacheDir);
    }

    public ValueTask<VideoSourceSnapshot?> GetAsync(string number, string sourceId, string? language = null)
    {
        var path = GetFilePath(number, sourceId, language);
        if (!File.Exists(path))
            return ValueTask.FromResult<VideoSourceSnapshot?>(null);

        try
        {
            var json = File.ReadAllText(path);
            var snapshot = JsonSerializer.Deserialize<VideoSourceSnapshot>(json, JsonOpts);
            if (snapshot is null) return ValueTask.FromResult<VideoSourceSnapshot?>(null);

            // schema 版本不匹配则视为无效
            if (snapshot.SchemaVersion != SchemaVersion)
            {
                _logger?.Warn($"[Cache] {number}/{sourceId} schema 不匹配: {snapshot.SchemaVersion} != {SchemaVersion}");
                return ValueTask.FromResult<VideoSourceSnapshot?>(null);
            }

            return ValueTask.FromResult<VideoSourceSnapshot?>(snapshot);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[Cache] {number}/{sourceId} 读取失败: {ex.Message}");
            return ValueTask.FromResult<VideoSourceSnapshot?>(null);
        }
    }

    public ValueTask SetAsync(VideoSourceSnapshot snapshot)
    {
        var dir = GetNumberDir(snapshot.Number);
        Directory.CreateDirectory(dir);
        var path = GetFilePath(snapshot.Number, snapshot.SourceId, snapshot.Language);

        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOpts);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[Cache] {snapshot.Number}/{snapshot.SourceId} 写入失败: {ex.Message}");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> HasAsync(string number, string sourceId, string? language = null)
    {
        var path = GetFilePath(number, sourceId, language);
        return ValueTask.FromResult(File.Exists(path));
    }

    public ValueTask ClearAsync(string number)
    {
        var dir = GetNumberDir(number);
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { _logger?.Warn($"[Cache] {number} 清除失败: {ex.Message}"); }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAllAsync()
    {
        if (Directory.Exists(_cacheDir))
        {
            try { Directory.Delete(_cacheDir, recursive: true); Directory.CreateDirectory(_cacheDir); }
            catch (Exception ex) { _logger?.Warn($"[Cache] 全部清除失败: {ex.Message}"); }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<string>> GetCachedSourceIdsAsync(string number)
    {
        var dir = GetNumberDir(number);
        if (!Directory.Exists(dir))
            return ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var ids = Directory.GetFiles(dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Cast<string>()
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<string>>(ids);
    }

    private string GetNumberDir(string number) =>
        Path.Combine(_cacheDir, number.ToLowerInvariant().Replace(' ', '_'));

    private string GetFilePath(string number, string sourceId, string? language)
    {
        var name = string.IsNullOrEmpty(language)
            ? sourceId
            : $"{sourceId}_{language}";
        return Path.Combine(GetNumberDir(number), $"{name}.json");
    }
}
