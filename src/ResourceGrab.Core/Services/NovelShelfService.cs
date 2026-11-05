using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Services;

/// <summary>书架里的一本在线书。只存展示与进度所需的最小字段，正文仍在缓存目录。</summary>
public sealed class NovelShelfItem
{
    [JsonPropertyName("sourceId")] public string SourceId { get; set; } = "";
    [JsonPropertyName("novelId")] public string NovelId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("coverUrl")] public string CoverUrl { get; set; } = "";
    [JsonPropertyName("wordCount")] public string WordCountText { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("addedAt")] public DateTime AddedAt { get; set; } = DateTime.Now;
    [JsonPropertyName("lastChapterIndex")] public int LastChapterIndex { get; set; }
    [JsonPropertyName("lastChapterTitle")] public string LastChapterTitle { get; set; } = "";
    [JsonPropertyName("lastReadAt")] public DateTime? LastReadAt { get; set; }
    [JsonPropertyName("chapterCount")] public int ChapterCount { get; set; }

    [JsonIgnore] public string Key => SourceId + ":" + NovelId;

    [JsonIgnore]
    public string ProgressText => ChapterCount > 0 && LastChapterIndex > 0
        ? $"读到 {LastChapterIndex} / {ChapterCount} 章"
        : LastChapterIndex > 0 ? $"读到第 {LastChapterIndex} 章" : "未开始";
}

internal sealed class NovelShelfFile
{
    [JsonPropertyName("items")] public List<NovelShelfItem> Items { get; set; } = new();
}

/// <summary>
/// 在线书架：收藏、移除、记录读到哪一章。落盘为 config\novel-shelf.json，
/// 与本地索引互不写入——两者的"来源"必须一直可回答。
/// </summary>
public sealed class NovelShelfService
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly List<NovelShelfItem> _items = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public NovelShelfService() : this(AppPaths.NovelShelfPath) { }

    public NovelShelfService(string path)
    {
        _path = path;
        Load();
    }

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    public IReadOnlyList<NovelShelfItem> Items
    {
        get
        {
            lock (_lock)
            {
                return _items
                    .OrderByDescending(i => i.LastReadAt ?? i.AddedAt)
                    .ToList();
            }
        }
    }

    public bool Contains(string sourceId, string novelId)
    {
        lock (_lock) return Find(sourceId, novelId) is not null;
    }

    public NovelShelfItem? Get(string sourceId, string novelId)
    {
        lock (_lock) return Find(sourceId, novelId);
    }

    public void Add(NovelShelfItem item)
    {
        if (string.IsNullOrWhiteSpace(item.NovelId)) return;
        lock (_lock)
        {
            var existing = Find(item.SourceId, item.NovelId);
            if (existing is not null)
            {
                // 重复加入：只刷新元信息，保留进度
                existing.Title = item.Title;
                existing.Author = item.Author;
                existing.CoverUrl = item.CoverUrl;
                existing.WordCountText = item.WordCountText;
                existing.Status = item.Status;
                existing.ChapterCount = item.ChapterCount;
            }
            else
            {
                _items.Add(item);
            }
        }
        Save();
    }

    public bool Remove(string sourceId, string novelId)
    {
        lock (_lock)
        {
            var existing = Find(sourceId, novelId);
            if (existing is null) return false;
            _items.Remove(existing);
        }
        Save();
        return true;
    }

    /// <summary>阅读时更新"读到第几章"。</summary>
    public void RecordProgress(string sourceId, string novelId, int chapterIndex, string chapterTitle, int chapterCount = 0)
    {
        lock (_lock)
        {
            var item = Find(sourceId, novelId);
            if (item is null) return;
            item.LastChapterIndex = chapterIndex;
            item.LastChapterTitle = chapterTitle;
            item.LastReadAt = DateTime.Now;
            if (chapterCount > 0) item.ChapterCount = chapterCount;
        }
        Save();
    }

    private NovelShelfItem? Find(string sourceId, string novelId) =>
        _items.FirstOrDefault(i =>
            string.Equals(i.SourceId, sourceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.NovelId, novelId, StringComparison.OrdinalIgnoreCase));

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var file = JsonSerializer.Deserialize<NovelShelfFile>(json, Options);
            if (file?.Items is null) return;
            lock (_lock)
            {
                _items.Clear();
                _items.AddRange(file.Items);
            }
        }
        catch
        {
            // 书架损坏不影响阅读，按空处理
        }
    }

    private void Save()
    {
        try
        {
            NovelShelfFile file;
            lock (_lock) file = new NovelShelfFile { Items = _items.ToList() };
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            File.WriteAllText(_path, JsonSerializer.Serialize(file, Options), new UTF8Encoding(false));
        }
        catch
        {
            // 写失败下次再存
        }
    }
}
