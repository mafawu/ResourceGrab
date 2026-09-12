using System.Net.Http;
using System.Text.Json;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Services.VideoScrape;

namespace ResourceGrab.Core.Services;

// ---------------------------------------------------------------------------
// 演员收藏：名字键（大小写不敏感）+ 落盘 json。档案补全（头像/简介）由 UI 层
// 触发 VideoActorMerger 后经 UpdateEnrichment 写回，本服务只管存取与变更通知。
// ---------------------------------------------------------------------------

public sealed class ActorFavoriteEntry
{
    public string Name { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? AvatarPath { get; set; }
    public string? Bio { get; set; }
}

public sealed class ActorFavoriteService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private Dictionary<string, ActorFavoriteEntry> _items =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>收藏变化（UI 刷新星标/列表用），参数为演员名。</summary>
    public event Action<string>? Changed;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly VideoActorMerger? _merger;

    public ActorFavoriteService(string filePath, ILogger? logger = null, VideoActorMerger? merger = null)
    {
        _filePath = filePath;
        _logger = logger;
        _merger = merger;
        Load();
    }

    public bool IsFavorite(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_lock) return _items.ContainsKey(name.Trim());
    }

    /// <summary>切换收藏；返回切换后的状态。取消收藏保留档案（头像/简介）以便再次收藏秒回。</summary>
    public bool Toggle(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        bool now;
        lock (_lock)
        {
            if (_items.ContainsKey(name))
            {
                _items.Remove(name);
                now = false;
            }
            else
            {
                _items[name] = new ActorFavoriteEntry { Name = name };
                now = true;
            }
            SaveLocked();
        }
        Changed?.Invoke(name);
        return now;
    }

    /// <summary>写回档案补全结果（头像落盘路径/简介行）；非收藏也允许预存。</summary>
    public void UpdateEnrichment(string name, string? avatarPath, string? bio)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        lock (_lock)
        {
            if (!_items.TryGetValue(name, out var entry))
            {
                entry = new ActorFavoriteEntry { Name = name };
                _items[name] = entry;
            }
            if (!string.IsNullOrEmpty(avatarPath)) entry.AvatarPath = avatarPath;
            if (!string.IsNullOrEmpty(bio)) entry.Bio = bio;
            SaveLocked();
        }
        Changed?.Invoke(name);
    }

    public IReadOnlyList<ActorFavoriteEntry> GetAll()
    {
        lock (_lock)
            return _items.Values.OrderByDescending(e => e.AddedAt).ToList();
    }

    public ActorFavoriteEntry? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_lock)
            return _items.TryGetValue(name.Trim(), out var e) ? e : null;
    }

    /// <summary>
    /// 收藏即刮削：经 VideoActorMerger 补全档案，头像落盘到 avatars 目录（与 GFriends 复用同一命名），
    /// 简介拼成一行写回记录。失败静默（仅日志），收藏本身不受影响。
    /// </summary>
    /// <returns>是否补到了新信息。</returns>
    public async Task<bool> EnrichAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || _merger is null) return false;
        name = name.Trim();
        VideoActorMetadata? meta;
        try
        {
            meta = await _merger.MergeAsync(name, ct);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ActorFavorites] 补全失败 {name}: {ex.Message}");
            return false;
        }
        if (meta is null) return false;

        string? avatarPath = null;
        var firstImage = meta.ImageUrls.FirstOrDefault();
        if (!string.IsNullOrEmpty(firstImage))
        {
            if (!firstImage.StartsWith("http", StringComparison.OrdinalIgnoreCase) && File.Exists(firstImage))
            {
                avatarPath = firstImage; // GFriends 本地落盘路径，直接复用
            }
            else if (firstImage.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                avatarPath = await SaveAvatarAsync(name, firstImage, ct);
            }
        }

        var info = new List<string>();
        if (meta.Birthday is not null) info.Add($"生日 {meta.Birthday}");
        if (meta.Birthplace is not null) info.Add($"出生地 {meta.Birthplace}");
        if (meta.Height is not null) info.Add($"{meta.Height}cm");
        if (meta.Aliases.Count > 0) info.Add("别名 " + string.Join(" / ", meta.Aliases.Take(6)));
        var bio = info.Count > 0 ? string.Join(" · ", info) : null;

        var before = Get(name);
        if (avatarPath is null && bio is null) return false;
        if (before?.AvatarPath == avatarPath && before?.Bio == bio) return false;
        UpdateEnrichment(name, avatarPath ?? before?.AvatarPath, bio ?? before?.Bio);
        _logger?.Info($"[ActorFavorites] 已补全 {name}");
        return true;
    }

    private async Task<string?> SaveAvatarAsync(string name, string url, CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(AppPaths.AppDataDir, "avatars");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, VideoScrape.Sources.GFriendsSource.AvatarFileName(name) + ".jpg");
            if (File.Exists(path)) return path;
            var bytes = await _http.GetByteArrayAsync(url, ct);
            if (bytes.Length == 0) return null;
            await File.WriteAllBytesAsync(path, bytes, ct);
            return path;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ActorFavorites] 头像保存失败 {name}: {ex.Message}");
            return null;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var list = JsonSerializer.Deserialize<List<ActorFavoriteEntry>>(
                File.ReadAllText(_filePath));
            if (list is null) return;
            lock (_lock)
                _items = list.Where(e => !string.IsNullOrWhiteSpace(e.Name))
                    .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.AddedAt).First(),
                        StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ActorFavorites] 加载失败: {ex.Message}");
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
            _logger?.Warn($"[ActorFavorites] 保存失败: {ex.Message}");
        }
    }
}
