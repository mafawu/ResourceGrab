using System.Text.Json;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.Core.Services;

// ---------------------------------------------------------------------------
// 在线视频预收藏：没入库也能 ★，入库（下载/重扫）时按番号自动关联为本地收藏。
// 入库后在线 stub 即使命完成会被移除，收藏关系由 VideoItem.IsFavorite 承载。
// ---------------------------------------------------------------------------

public sealed class OnlineVideoFavoriteEntry
{
    public string SourceId { get; set; } = "";
    public string VideoId { get; set; } = "";
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OnlineVideoFavoriteService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private Dictionary<string, OnlineVideoFavoriteEntry> _items = new(StringComparer.Ordinal);

    /// <summary>收藏变化，参数为 "sourceId/videoId" 键。</summary>
    public event Action<string>? Changed;

    public OnlineVideoFavoriteService(string filePath, ILogger? logger = null)
    {
        _filePath = filePath;
        _logger = logger;
        Load();
    }

    private static string Key(string sourceId, string videoId) => $"{sourceId}/{videoId}";

    public bool IsFavorite(string sourceId, string videoId)
    {
        if (string.IsNullOrEmpty(videoId)) return false;
        lock (_lock) return _items.ContainsKey(Key(sourceId, videoId));
    }

    /// <summary>切换收藏；返回切换后的状态。</summary>
    public bool Toggle(string sourceId, string videoId, string number, string title, string coverUrl)
    {
        if (string.IsNullOrEmpty(videoId)) return false;
        var key = Key(sourceId, videoId);
        bool now;
        lock (_lock)
        {
            if (_items.ContainsKey(key))
            {
                _items.Remove(key);
                now = false;
            }
            else
            {
                _items[key] = new OnlineVideoFavoriteEntry
                {
                    SourceId = sourceId, VideoId = videoId,
                    Number = number ?? "", Title = title ?? "", CoverUrl = coverUrl ?? "",
                };
                now = true;
            }
            SaveLocked();
        }
        Changed?.Invoke(key);
        return now;
    }

    public IReadOnlyList<OnlineVideoFavoriteEntry> GetAll()
    {
        lock (_lock)
            return _items.Values.OrderByDescending(e => e.AddedAt).ToList();
    }

    /// <summary>按番号找预收藏（入库关联用，大小写不敏感）。</summary>
    public IReadOnlyList<OnlineVideoFavoriteEntry> FindByNumber(string number)
    {
        if (string.IsNullOrWhiteSpace(number)) return [];
        lock (_lock)
            return _items.Values
                .Where(e => string.Equals(e.Number, number, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    /// <summary>移除该番号的全部预收藏 stub（已转为本地收藏后调用），返回移除数。</summary>
    public int RemoveByNumber(string number)
    {
        if (string.IsNullOrWhiteSpace(number)) return 0;
        List<string> keys;
        lock (_lock)
        {
            keys = _items
                .Where(kv => string.Equals(kv.Value.Number, number, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();
            if (keys.Count == 0) return 0;
            foreach (var key in keys) _items.Remove(key);
            SaveLocked();
        }
        foreach (var key in keys) Changed?.Invoke(key);
        return keys.Count;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var list = JsonSerializer.Deserialize<List<OnlineVideoFavoriteEntry>>(
                File.ReadAllText(_filePath));
            if (list is null) return;
            lock (_lock)
                _items = list.Where(e => !string.IsNullOrEmpty(e.VideoId))
                    .GroupBy(e => Key(e.SourceId, e.VideoId))
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.AddedAt).First());
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[OnlineFavorites] 加载失败: {ex.Message}");
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items.Values.ToList(), JsonOptions));
            File.Move(tmp, _filePath, true);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[OnlineFavorites] 保存失败: {ex.Message}");
        }
    }
}
