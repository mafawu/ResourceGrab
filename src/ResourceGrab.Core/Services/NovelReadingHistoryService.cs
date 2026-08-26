using System.Text.Json;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services;

public class NovelReadingHistoryService
{
    private readonly string _filePath;
    private Dictionary<string, NovelReadingProgress> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public NovelReadingHistoryService() : this(AppPaths.NovelHistoryPath) { }

    public NovelReadingHistoryService(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public IReadOnlyDictionary<string, NovelReadingProgress> Items
    {
        get { lock(_lock) return new Dictionary<string, NovelReadingProgress>(_items, StringComparer.OrdinalIgnoreCase); }
    }

    public NovelReadingProgress? Get(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        var key = Normalize(fullPath);
        lock(_lock) return _items.TryGetValue(key, out var v) ? v : null;
    }

    public void Save(string fullPath, int page, int pageCount, int charsPerPage, double fontSize, int totalChars, string title="")
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        var key = Normalize(fullPath);
        var prog = new NovelReadingProgress
        {
            Path = fullPath,
            Page = Math.Clamp(page, 1, Math.Max(1, pageCount)),
            PageCount = Math.Max(1, pageCount),
            CharsPerPage = charsPerPage,
            FontSize = fontSize,
            TotalChars = totalChars,
            UpdatedAt = DateTime.Now,
            Title = title
        };
        lock(_lock) _items[key] = prog;
        _ = Task.Run(SaveToFile);
    }

    public void Remove(string fullPath)
    {
        var key = Normalize(fullPath);
        lock(_lock) { if(_items.Remove(key)) _ = Task.Run(SaveToFile); }
    }

    public IReadOnlyList<NovelReadingProgress> GetRecent(int count = 20)
    {
        lock(_lock) return _items.Values.OrderByDescending(x=>x.UpdatedAt).Take(count).ToList();
    }

    private static string Normalize(string p) => p.Replace("/","\\").Trim().ToLowerInvariant();

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var file = JsonSerializer.Deserialize<NovelHistoryFile>(json, _opts);
            if (file?.Items != null)
                lock(_lock) _items = new Dictionary<string, NovelReadingProgress>(file.Items, StringComparer.OrdinalIgnoreCase);
        }
        catch { }
    }

    private void SaveToFile()
    {
        try
        {
            Dictionary<string, NovelReadingProgress> snapshot;
            lock(_lock) snapshot = new Dictionary<string, NovelReadingProgress>(_items, StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? ".");
            var file = new NovelHistoryFile { Items = snapshot };
            var json = JsonSerializer.Serialize(file, _opts);
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }
}
