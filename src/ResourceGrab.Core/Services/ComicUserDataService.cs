using System.Text.Json;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services;

/// <summary>
/// 漫画用户数据服务：按漫画路径存储阅读进度、评分、备注等用户操作数据。
/// 独立于 local-library-cache.json，重新扫描不会丢失用户数据。
/// </summary>
public class ComicUserDataService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Lock = new();

    private readonly string _filePath;
    private Dictionary<string, ComicUserData> _data;

    public ComicUserDataService(string filePath)
    {
        _filePath = filePath;
        _data = Load();
    }

    /// <summary>获取指定漫画的用户数据（不存在则返回默认值）。</summary>
    public ComicUserData Get(string comicPath)
    {
        lock (Lock)
        {
            return _data.TryGetValue(comicPath, out var d) ? d : new ComicUserData();
        }
    }

    /// <summary>更新用户数据并持久化。</summary>
    public void Update(string comicPath, Action<ComicUserData> mutate)
    {
        lock (Lock)
        {
            if (!_data.TryGetValue(comicPath, out var entry))
            {
                entry = new ComicUserData();
                _data[comicPath] = entry;
            }
            mutate(entry);
            Save();
        }
    }

    /// <summary>记录一次阅读：更新阅读次数、时间戳和进度。</summary>
    public void RecordReading(string comicPath, int chapterIndex, int imageIndex, int totalImages)
    {
        Update(comicPath, d =>
        {
            var now = DateTime.UtcNow.ToString("o");
            d.FirstReadAt ??= now;
            d.LastReadAt = now;
            d.ReadCount++;
            if (totalImages > 0 && chapterIndex >= 0 && imageIndex >= 0)
            {
                // 简化：进度 = 已读图片数 / 总图片数（跨章节时用当前章节位置近似）
                var progress = Math.Clamp(100.0 * (chapterIndex + 1) * imageIndex / totalImages, 0, 100);
                if (progress > d.ReadProgressPercent) d.ReadProgressPercent = progress;
            }
        });
    }

    /// <summary>设置评分。</summary>
    public void SetRating(string comicPath, int rating)
    {
        Update(comicPath, d => d.Rating = Math.Clamp(rating, 0, 5));
    }

    /// <summary>设置备注。</summary>
    public void SetNotes(string comicPath, string notes)
    {
        Update(comicPath, d => d.Notes = notes ?? "");
    }

    /// <summary>批量填充 LocalComic 的用户数据字段。</summary>
    public void Populate(IEnumerable<LocalComic> comics)
    {
        foreach (var comic in comics)
        {
            var d = Get(comic.Path);
            comic.ReadImageCount = d.ReadImageCount;
            comic.TotalImageCount = d.TotalImageCount;
            comic.FirstReadAt = d.FirstReadAt;
            comic.LastReadAt = d.LastReadAt;
            comic.ReadCount = d.ReadCount;
            comic.Rating = d.Rating;
            comic.Notes = d.Notes;
        }
    }

    private Dictionary<string, ComicUserData> Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, ComicUserData>>(File.ReadAllText(_filePath));
                if (data is not null) return data;
            }
        }
        catch { }
        return new Dictionary<string, ComicUserData>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_data, JsonOptions));
            File.Move(temp, _filePath, true);
        }
        catch { }
    }
}