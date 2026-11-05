using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ResourceGrab.Core.Sources.Novel;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace ResourceGrab.Core.Services;

/// <summary>
/// 本地小说封面刮削：拿书名去在线源搜，规范化书名全等命中才采纳，把详情页封面下载到本地。
/// 一次扫描几十本本地书是不可能逐本请求的，所以这里只做"单本查询"，由调用方（队列）限流。
/// </summary>
public sealed class NovelCoverService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly string[] NoiseWords =
    [
        "未删减", "无删减", "完整版", "完结版", "典藏版", "修订版", "加料版", "校对版", "精校版",
        "精校", "校对", "全本", "完结", "典藏", "合集", "全集", "TXT", "txt",
    ];

    private readonly INovelSource _source;
    private readonly string _dir;
    private readonly string _indexPath;
    private readonly object _lock = new();
    private readonly Dictionary<string, CoverEntry> _entries;
    /// <summary>已经查过但还没写进文件的键，避免同一本书并发重复请求。</summary>
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public NovelCoverService(INovelSource source, string? dir = null, string? indexPath = null)
    {
        _source = source;
        _dir = dir ?? AppPaths.NovelCoverDir;
        _indexPath = indexPath ?? AppPaths.NovelCoverIndexPath;
        _entries = Load();
    }

    /// <summary>只读缓存：命中过就直接返回路径，不发请求。</summary>
    public string? Peek(string title)
    {
        var key = NormalizeTitle(title);
        if (key.Length == 0) return null;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry)) return null;
            if (entry.Status != 1 || string.IsNullOrEmpty(entry.Cover)) return null;
            var path = Path.Combine(_dir, entry.Cover);
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>取封面：缓存 → 在线搜（书名全等）→ 下载到本地。查不到返回 null，并写负缓存。</summary>
    public async Task<string?> GetCoverAsync(string title, CancellationToken ct = default)
    {
        var key = NormalizeTitle(title);
        if (key.Length == 0) return null;

        if (Peek(title) is { } cached) return cached;

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.Status != 1 &&
                DateTime.TryParse(entry.CheckedAt, out var at) && (DateTime.Now - at).TotalDays < 7)
                return null; // 负缓存 7 天，别反复打站点
        }

        if (!_inFlight.TryAdd(key, 0)) return null;

        try
        {
            var search = await _source.SearchAsync(title, 1, NovelSearchField.Title, ct);
            var match = search.Items.FirstOrDefault(i => NormalizeTitle(i.Title) == key)
                        ?? search.Items.FirstOrDefault(i => NormalizeTitle(i.Title).StartsWith(key, StringComparison.Ordinal) && key.Length >= 3);
            if (match is null)
            {
                Record(key, 0, null);
                return null;
            }

            var detail = await _source.GetDetailAsync(match.Id, ct);
            var url = string.IsNullOrWhiteSpace(detail.CoverUrl) ? match.CoverUrl : detail.CoverUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                Record(key, 0, null);
                return null;
            }

            var saved = await DownloadAsync(url, key, ct);
            Record(key, saved is null ? 0 : 1, saved is null ? null : Path.GetFileName(saved));
            return saved;
        }
        catch
        {
            Record(key, 0, null);
            return null;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private async Task<string?> DownloadAsync(string url, string key, CancellationToken ct)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url, ct);
            if (bytes.Length < 512) return null; // 占位图 / 错误页

            Directory.CreateDirectory(_dir);
            var file = Path.Combine(_dir, Hash(key) + ".jpg");
            // 站点封面是 webp，WPF 没有内置 webp 解码器；统一转成 jpg 再落盘，
            // 顺带把尺寸压到卡片够用的宽度（省内存、省磁盘）。
            if (TrySaveAsJpeg(bytes, file)) return file;

            var raw = Path.Combine(_dir, Hash(key) + GuessExtension(url, bytes));
            await File.WriteAllBytesAsync(raw, bytes, ct);
            return raw;
        }
        catch
        {
            return null;
        }
    }

    private static bool TrySaveAsJpeg(byte[] bytes, string path)
    {
        try
        {
            using var image = Image.Load(bytes);
            if (image.Width > 480)
            {
                var height = (int)Math.Round(image.Height * (480.0 / image.Width));
                image.Mutate(x => x.Resize(480, Math.Max(1, height)));
            }
            image.SaveAsJpeg(path, new JpegEncoder { Quality = 82 });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Record(string key, int status, string? cover)
    {
        lock (_lock)
        {
            _entries[key] = new CoverEntry
            {
                Cover = cover,
                Status = status,
                CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            Save();
        }
    }

    private Dictionary<string, CoverEntry> Load()
    {
        try
        {
            if (!File.Exists(_indexPath)) return new(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(_indexPath);
            var file = JsonSerializer.Deserialize<CoverIndexFile>(json, JsonOpts);
            return file?.Items is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CoverEntry>(file.Items, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_indexPath) ?? ".");
            var file = new CoverIndexFile { Items = new Dictionary<string, CoverEntry>(_entries) };
            File.WriteAllText(_indexPath, JsonSerializer.Serialize(file, JsonOpts));
        }
        catch { }
    }

    /// <summary>书名规范化：《》书名号、作者标注、版本词、空白与标点全部抹掉。</summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var s = title.Trim();

        var cut = s.IndexOfAny(['（', '(', '【', '[']);
        if (cut > 1) s = s[..cut];

        foreach (var word in NoiseWords) s = s.Replace(word, "");
        // 版本词去掉后可能剩个孤零零的"版"（如"精校版"只匹配到"精校"时的兜底）
        s = s.TrimEnd('版', '全', '本');

        s = Regex.Replace(s, @"^(?:\d+[.、_\-])+", "");          // 前导 "001. "
        s = Regex.Replace(s, @"《|》|「|」|】|【", "");
        s = Regex.Replace(s, @"作者[：:]\s*[^\s]+", "");
        s = Regex.Replace(s, @"[\s\-_·:：,，.。!！?？~～|/\\]+", "");
        return s.ToLowerInvariant();
    }

    private static string Hash(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string GuessExtension(string url, byte[] bytes)
    {
        var ext = Path.GetExtension(new Uri(url, UriKind.Absolute).AbsolutePath).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif") return ext;

        // 魔数兜底
        if (bytes.Length > 3)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) return ".jpg";
            if (bytes[0] == 0x89 && bytes[1] == 0x50) return ".png";
            if (bytes[0] == 0x47 && bytes[1] == 0x49) return ".gif";
        }
        return ".jpg";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.alicesw.com/");
        return client;
    }

    public sealed class CoverEntry
    {
        /// <summary>1 = 命中，0 = 未命中。</summary>
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("cover")] public string? Cover { get; set; }
        [JsonPropertyName("checkedAt")] public string CheckedAt { get; set; } = "";
    }

    private sealed class CoverIndexFile
    {
        [JsonPropertyName("items")] public Dictionary<string, CoverEntry> Items { get; set; } = new();
    }
}
