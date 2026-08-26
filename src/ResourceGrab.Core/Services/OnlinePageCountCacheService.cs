using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceGrab.Core.Sources;

namespace ResourceGrab.Core.Services;

/// <summary>
/// 在线漫画总页数缓存：只服务少于阈值的章节列表，
/// 按内容源 + 漫画 ID + 章节 ID 列表定位，章节变化后自动失效。
/// </summary>
public class OnlinePageCountCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int MaxEntries = 500;

    private static readonly object Lock = new();
    private readonly string _filePath;
    private Dictionary<string, OnlinePageCountCacheEntry> _entries;

    public OnlinePageCountCacheService(string filePath)
    {
        _filePath = filePath;
        _entries = Load();
    }

    /// <summary>章节列表指纹一致且未过期时返回缓存的总页数。</summary>
    public bool TryGet(
        string sourceId,
        string comicId,
        IReadOnlyList<Chapter> chapters,
        out long imageCount)
    {
        imageCount = 0;
        var key = BuildKey(sourceId, comicId);
        var fingerprint = BuildFingerprint(chapters);

        lock (Lock)
        {
            if (!_entries.TryGetValue(key, out var entry)
                || entry.ChapterFingerprint != fingerprint
                || !long.TryParse(entry.ImageCount, out imageCount)
                || imageCount < 0)
            {
                return false;
            }

            // 章节列表没变但远端可能重发内容；7 天后重新校准一次。
            return DateTime.TryParse(
                entry.UpdatedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var updatedAt)
                && DateTime.UtcNow - updatedAt < TimeSpan.FromDays(7);
        }
    }

    public void Set(string sourceId, string comicId, IReadOnlyList<Chapter> chapters, long imageCount)
    {
        if (imageCount < 0)
        {
            return;
        }

        var key = BuildKey(sourceId, comicId);
        var entry = new OnlinePageCountCacheEntry
        {
            ChapterFingerprint = BuildFingerprint(chapters),
            ImageCount = imageCount.ToString(CultureInfo.InvariantCulture),
            UpdatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };

        lock (Lock)
        {
            _entries[key] = entry;
            Trim();
            Save();
        }
    }

    public static string BuildKey(string sourceId, string comicId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceId}\n{comicId}")));

    public static string BuildFingerprint(IReadOnlyList<Chapter> chapters)
    {
        var payload = string.Join("\n", chapters.Select(c => c.Id));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private Dictionary<string, OnlinePageCountCacheEntry> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            var file = JsonSerializer.Deserialize<OnlinePageCountCacheFile>(
                File.ReadAllText(_filePath), JsonOptions);
            return file?.Entries is { } entries
                ? new Dictionary<string, OnlinePageCountCacheEntry>(entries, StringComparer.Ordinal)
                : [];
        }
        catch
        {
            return [];
        }
    }

    private void Trim()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (var key in _entries
            .OrderBy(pair => DateTime.TryParse(
                pair.Value.UpdatedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var updatedAt)
                ? updatedAt
                : DateTime.MinValue)
            .Select(pair => pair.Key)
            .Take(_entries.Count - MaxEntries))
        {
            _entries.Remove(key);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new OnlinePageCountCacheFile
            {
                Entries = _entries,
            }, JsonOptions));
            File.Move(temp, _filePath, true);
        }
        catch
        {
            // 缓存失败不影响详情页；下次进入时可重新统计。
        }
    }
}

public sealed class OnlinePageCountCacheFile
{
    [JsonPropertyName("entries")]
    public Dictionary<string, OnlinePageCountCacheEntry> Entries { get; set; } = new(StringComparer.Ordinal);
}

public sealed class OnlinePageCountCacheEntry
{
    [JsonPropertyName("chapterFingerprint")]
    public string ChapterFingerprint { get; set; } = "";

    [JsonPropertyName("imageCount")]
    public string ImageCount { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = "";
}
