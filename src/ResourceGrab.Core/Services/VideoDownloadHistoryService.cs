using System.Text.Json;

namespace ResourceGrab.Core.Services;

/// <summary>
/// 视频下载完成记录：持久化到 AppDataDir/video-download-history.json。
/// 与 <see cref="DownloadHistoryService"/>（漫画章节）互补，供下载页的「已完成」在重启后仍然可见。
/// 视频任务队列本身是纯内存的，历史只能在这里落盘。
/// </summary>
public class VideoDownloadHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public VideoDownloadHistoryService(string? filePath = null)
    {
        _filePath = filePath ?? AppPaths.VideoDownloadHistoryPath;
    }

    public List<VideoDownloadHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new List<VideoDownloadHistoryEntry>();
            }
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<VideoDownloadHistoryEntry>>(json, JsonOptions) ?? new();
        }
        catch
        {
            // 历史损坏不阻塞下载页
            return new List<VideoDownloadHistoryEntry>();
        }
    }

    /// <summary>追加一条完成记录；同一输出路径的旧记录会被替换，避免重复下载后出现两行。</summary>
    public void Append(VideoDownloadHistoryEntry entry)
    {
        try
        {
            var entries = Load();
            entries.RemoveAll(e => string.Equals(e.OutputPath, entry.OutputPath, StringComparison.OrdinalIgnoreCase));
            entries.Add(entry);
            if (entries.Count > 200)
            {
                entries.RemoveRange(0, entries.Count - 200);
            }
            Directory.CreateDirectory(AppPaths.AppDataDir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(entries, JsonOptions));
        }
        catch
        {
            // 保存失败不影响下载
        }
    }

    /// <summary>清空历史（只清记录，不动磁盘文件）。</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        catch
        {
            // 忽略
        }
    }
}

/// <summary>一条视频下载完成记录。</summary>
public class VideoDownloadHistoryEntry
{
    /// <summary>番号（可能为空）。</summary>
    public string Number { get; set; } = "";

    /// <summary>标题。</summary>
    public string Title { get; set; } = "";

    /// <summary>落盘路径（「打开文件 / 打开目录」的依据）。</summary>
    public string OutputPath { get; set; } = "";

    /// <summary>文件大小（字节）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>完成时间（本地时间）。</summary>
    public DateTime CompletedAt { get; set; }
}
