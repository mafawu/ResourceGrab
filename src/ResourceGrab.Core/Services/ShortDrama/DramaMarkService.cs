using System.Text.Json;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.Core.Services.ShortDrama;

/// <summary>短剧的两种「我的」标记：追剧 / 收藏。两者独立，可同时存在。</summary>
public enum DramaMarkKind
{
    Follow,
    Favorite,
}

/// <summary>一条短剧标记（用于「追剧 / 收藏」页面在后续版本渲染）。</summary>
public sealed class DramaMarkEntry
{
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string EpisodeText { get; set; } = "";
    public string ScoreText { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 短剧追剧/收藏的本地持久化。结构与 <see cref="OnlineVideoFavoriteService"/> 一致：
/// 一个 JSON 文件、按 <c>kind/id</c> 建索引，写盘失败只记日志不影响播放。
/// </summary>
public sealed class DramaMarkService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private Dictionary<string, DramaMarkEntry> _items = new(StringComparer.Ordinal);

    public DramaMarkService(string filePath, ILogger? logger = null)
    {
        _filePath = filePath;
        _logger = logger;
        Load();
    }

    private static string Key(DramaMarkKind kind, string id) => $"{(int)kind}/{id}";

    public bool IsMarked(DramaMarkKind kind, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_lock) return _items.ContainsKey(Key(kind, id));
    }

    /// <summary>切换标记，返回切换后的状态。</summary>
    public bool Toggle(DramaMarkKind kind, string id, string title, string coverUrl,
        string channelName, string episodeText, string scoreText)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var key = Key(kind, id);
        bool now;
        lock (_lock)
        {
            if (_items.Remove(key))
            {
                now = false;
            }
            else
            {
                _items[key] = new DramaMarkEntry
                {
                    Kind = kind.ToString(),
                    Id = id,
                    Title = title ?? "",
                    CoverUrl = coverUrl ?? "",
                    ChannelName = channelName ?? "",
                    EpisodeText = episodeText ?? "",
                    ScoreText = scoreText ?? "",
                };
                now = true;
            }
            SaveLocked();
        }
        return now;
    }

    public IReadOnlyList<DramaMarkEntry> GetAll(DramaMarkKind kind)
    {
        var name = kind.ToString();
        lock (_lock)
            return _items.Values
                .Where(e => string.Equals(e.Kind, name, StringComparison.Ordinal))
                .OrderByDescending(e => e.AddedAt)
                .ToList();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<DramaMarkEntry>>(json, JsonOptions);
            if (list is null) return;
            lock (_lock)
            {
                _items = new Dictionary<string, DramaMarkEntry>(StringComparer.Ordinal);
                foreach (var entry in list)
                {
                    if (string.IsNullOrWhiteSpace(entry.Id)) continue;
                    var kind = Enum.TryParse<DramaMarkKind>(entry.Kind, out var parsed)
                        ? parsed
                        : DramaMarkKind.Favorite;
                    _items[Key(kind, entry.Id)] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[DramaMark] 读取标记失败: {ex.Message}");
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
            _logger?.Warn($"[DramaMark] 保存标记失败: {ex.Message}");
        }
    }
}
