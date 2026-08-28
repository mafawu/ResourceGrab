using System.Text.Json;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape;

namespace ResourceGrab.Core.Services;

/// <summary>
/// 读写 video-scrape-metadata.json 侧车文件。
/// 存储每个 VideoItem 的 FieldSources（字段溯源）和 Attempts（来源执行记录）。
/// </summary>
public sealed class ScrapeReportService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly object _sync = new();
    private VideoScrapeMetadataStore? _store;

    public ScrapeReportService(ILogger? logger = null)
    {
        _filePath = AppPaths.VideoScrapeMetadataPath;
        _logger = logger;
    }

    public ScrapeReportService(string filePath, ILogger? logger = null)
    {
        _filePath = filePath;
        _logger = logger;
    }

    private VideoScrapeMetadataStore EnsureLoaded()
    {
        if (_store is not null) return _store;
        if (File.Exists(_filePath))
        {
            try
            {
                var json = File.ReadAllText(_filePath);
                _store = JsonSerializer.Deserialize<VideoScrapeMetadataStore>(json) ?? new();
                return _store;
            }
            catch (Exception ex)
            {
                _logger?.Warn($"[ScrapeReport] 读取元数据文件失败: {ex.Message}");
            }
        }
        _store = new();
        return _store;
    }

    /// <summary>获取指定 VideoItem 的刮削报告。</summary>
    public VideoScrapeReport? GetReport(string videoItemId)
    {
        lock (_sync)
        {
            return GetReportCore(videoItemId);
        }
    }

    private VideoScrapeReport? GetReportCore(string videoItemId)
    {
        var store = EnsureLoaded();
        return store.Items.TryGetValue(videoItemId, out var report) ? report : null;
    }

    /// <summary>保存刮削聚合结果。</summary>
    public void SaveReport(string videoItemId, VideoScrapeAggregate aggregate)
    {
        lock (_sync)
        {
            var store = EnsureLoaded();
            var report = new VideoScrapeReport
            {
                FieldSources = aggregate.FieldSources,
                Attempts = aggregate.Attempts,
                LastAggregateAt = DateTimeOffset.UtcNow,
            };
            store.Items[videoItemId] = report;
            Persist(store);
        }
    }

    /// <summary>移除指定 VideoItem 的报告。</summary>
    public void RemoveReport(string videoItemId)
    {
        lock (_sync)
        {
            var store = EnsureLoaded();
            if (store.Items.Remove(videoItemId))
                Persist(store);
        }
    }

    /// <summary>获取所有报告的总条数。</summary>
    public int GetReportCount()
    {
        lock (_sync)
        {
            var store = EnsureLoaded();
            return store.Items.Count;
        }
    }

    /// <summary>获取缓存目录的总大小（字节）。返回 -1 表示目录不存在。</summary>
    public long GetCacheDirectorySize()
    {
        var cacheDir = AppPaths.VideoSourceCacheDir;
        if (!Directory.Exists(cacheDir)) return -1;
        try
        {
            return Directory.GetFiles(cacheDir, "*.json", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>获取快照缓存中各来源的条目数。</summary>
    public Dictionary<string, int> GetSnapshotSourceCounts()
    {
        var cacheDir = AppPaths.VideoSourceCacheDir;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(cacheDir)) return counts;
        try
        {
            foreach (var file in Directory.GetFiles(cacheDir, "*.json", SearchOption.AllDirectories))
            {
                var sourceId = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(sourceId)) continue;
                // 处理 xxx_lang 格式
                var underscoreIdx = sourceId.LastIndexOf('_');
                var baseId = underscoreIdx > 0 ? sourceId[..underscoreIdx] : sourceId;
                counts.TryGetValue(baseId, out var count);
                counts[baseId] = count + 1;
            }
        }
        catch { }
        return counts;
    }

    /// <summary>清除全部快照缓存。</summary>
    public async Task ClearAllSnapshotsAsync()
    {
        var cacheDir = AppPaths.VideoSourceCacheDir;
        if (!Directory.Exists(cacheDir)) return;
        try
        {
            await Task.Run(() => Directory.Delete(cacheDir, recursive: true));
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ScrapeReport] 清除快照缓存失败: {ex.Message}");
        }
    }


    // ---- 按文件路径存储的刮削元数据缓存（删除 video-library.json 后可恢复） ----

    private VideoScrapeMetadataStore? _pathStore;

    private VideoScrapeMetadataStore EnsurePathStoreLoaded()
    {
        if (_pathStore is not null) return _pathStore;
        var path = Path.Combine(Path.GetDirectoryName(_filePath) ?? "", "video-scrape-path-cache.json");
        if (File.Exists(path))
        {
            try
            {
                _pathStore = JsonSerializer.Deserialize<VideoScrapeMetadataStore>(File.ReadAllText(path)) ?? new();
                return _pathStore;
            }
            catch { }
        }
        _pathStore = new();
        return _pathStore;
    }

    /// <summary>按文件路径保存刮削元数据（VideoItem 的核心字段）。</summary>
    /// <summary>按番号保存刮削元数据。</summary>
    public void SaveMetadataByNumber(string number, VideoItem item)
    {
        if (string.IsNullOrEmpty(number)) return;
        lock (_sync)
        {
            var store = EnsurePathStoreLoaded();
            var report = new VideoScrapeReport
            {
                FieldSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["title"] = item.Title ?? "",
                    ["originalTitle"] = item.OriginalTitle ?? "",
                    ["description"] = item.Description ?? "",
                    ["actors"] = string.Join(",", item.Actors ?? []),
                    ["tags"] = string.Join(",", item.Tags ?? []),
                    ["series"] = item.Series ?? "",
                    ["studio"] = item.Studio ?? "",
                    ["score"] = item.Score.ToString(),
                    ["releaseDate"] = item.ReleaseDate?.ToString("yyyy-MM-dd") ?? "",
                    ["number"] = item.Number ?? "",
                    ["coverPath"] = item.CoverPath ?? "",
                    ["posterPath"] = item.PosterPath ?? "",
                },
                LastAggregateAt = DateTimeOffset.UtcNow,
            };
            store.Items[number] = report;
            PersistPathStore(store);
        }
    }

    /// <summary>按文件路径查找已缓存的刮削元数据，返回 null 表示无缓存。</summary>
    /// <summary>按番号查找已缓存的刮削元数据。</summary>
    public VideoScrapeReport? GetMetadataByNumber(string number)
    {
        lock (_sync)
        {
            var store = EnsurePathStoreLoaded();
            return store.Items.TryGetValue(number, out var report) ? report : null;
        }
    }

    private void PersistPathStore(VideoScrapeMetadataStore store)
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(_filePath) ?? "", "video-scrape-path-cache.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(store, JsonOpts));
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ScrapeReport] 保存路径缓存失败: {ex.Message}");
        }
    }

    private void Persist(VideoScrapeMetadataStore store)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(store, JsonOpts);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ScrapeReport] 保存元数据文件失败: {ex.Message}");
        }
    }
}

/// <summary>侧车文件的顶层结构。</summary>
public sealed class VideoScrapeMetadataStore
{
    public Dictionary<string, VideoScrapeReport> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>单个 VideoItem 的刮削报告。</summary>
public sealed class VideoScrapeReport
{
    public Dictionary<string, string> FieldSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<VideoSourceAttempt> Attempts { get; set; } = [];
    public DateTimeOffset LastAggregateAt { get; set; }
}
