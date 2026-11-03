using System.Text.Json;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.Core.Services.ShortDrama;

/// <summary>一部短剧的观看进度：上次看到第几集 + 已看完的集号。</summary>
public sealed class DramaWatchProgressEntry
{
    public string Id { get; set; } = "";
    public int LastEpisode { get; set; } = 1;
    public List<int> WatchedEpisodes { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 短剧观看进度持久化（config/short-drama-progress.json）。
/// 只记录「上次看到第几集 + 已看完哪些集」，不记播放秒数：短剧单集短，
/// 按集续播比按秒续播更符合使用习惯，也避免签名地址失效后出现"续到一半"的尴尬。
/// </summary>
public sealed class DramaWatchProgressService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private Dictionary<string, DramaWatchProgressEntry> _items = new(StringComparer.Ordinal);

    public DramaWatchProgressService(string filePath, ILogger? logger = null)
    {
        _filePath = filePath;
        _logger = logger;
        Load();
    }

    /// <summary>取一部剧的进度副本；没有则返回 null。</summary>
    public DramaWatchProgressEntry? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_lock)
        {
            if (!_items.TryGetValue(id, out var entry)) return null;
            return new DramaWatchProgressEntry
            {
                Id = entry.Id,
                LastEpisode = entry.LastEpisode,
                WatchedEpisodes = [.. entry.WatchedEpisodes],
                UpdatedAt = entry.UpdatedAt,
            };
        }
    }

    /// <summary>覆盖保存一部剧的进度（幂等）。</summary>
    public void Save(string id, int lastEpisode, IEnumerable<int> watchedEpisodes)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var watched = watchedEpisodes.Where(n => n > 0).Distinct().OrderBy(n => n).ToList();
        lock (_lock)
        {
            _items[id] = new DramaWatchProgressEntry
            {
                Id = id,
                LastEpisode = Math.Max(1, lastEpisode),
                WatchedEpisodes = watched,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            SaveLocked();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<DramaWatchProgressEntry>>(json, JsonOptions);
            if (list is null) return;
            lock (_lock)
            {
                _items = new Dictionary<string, DramaWatchProgressEntry>(StringComparer.Ordinal);
                foreach (var entry in list)
                {
                    if (string.IsNullOrWhiteSpace(entry.Id)) continue;
                    _items[entry.Id] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[DramaProgress] 读取进度失败: {ex.Message}");
        }
    }

    private void SaveLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_items.Values.ToList(), JsonOptions));
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[DramaProgress] 保存进度失败: {ex.Message}");
        }
    }
}
