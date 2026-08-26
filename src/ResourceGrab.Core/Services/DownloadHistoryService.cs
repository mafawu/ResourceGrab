using System.Text.Json;

namespace ResourceGrab.Core.Services;

/// <summary>下载历史记录：持久化到 AppDataDir/download-history.json，重启后恢复下载队列展示。</summary>
public class DownloadHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public DownloadHistoryService(string? filePath = null)
    {
        _filePath = filePath ?? AppPaths.DownloadHistoryPath;
    }

    public List<DownloadHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new List<DownloadHistoryEntry>();
            }
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<DownloadHistoryEntry>>(json, JsonOptions) ?? new();
        }
        catch
        {
            // 历史损坏不阻塞：视为空历史
            return new List<DownloadHistoryEntry>();
        }
    }

    public void Append(DownloadHistoryEntry entry)
    {
        try
        {
            var entries = Load();
            entries.Add(entry);
            // 最多保留 200 条历史
            if (entries.Count > 200)
            {
                entries.RemoveRange(0, entries.Count - 200);
            }
            Directory.CreateDirectory(AppPaths.AppDataDir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(entries, JsonOptions));
        }
        catch
        {
            // 历史保存失败不阻塞下载
        }
    }
}

/// <summary>一条下载历史记录。</summary>
public class DownloadHistoryEntry
{
    /// <summary>漫画标题。</summary>
    public string AlbumTitle { get; set; } = "";

    /// <summary>章节标题。</summary>
    public string ChapterTitle { get; set; } = "";

    /// <summary>状态：成功 / 失败。</summary>
    public string Status { get; set; } = "";

    /// <summary>完成时间。</summary>
    public DateTime CompletedAt { get; set; }

    /// <summary>图片总数（成功时）。</summary>
    public long ImageCount { get; set; }
}
