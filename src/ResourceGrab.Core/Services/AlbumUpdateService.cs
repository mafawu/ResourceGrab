using ResourceGrab.Core.Http;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services;

/// <summary>更新检查结果：线上专辑与本地已下载章节的对比。</summary>
public class AlbumUpdateResult
{
    public long AlbumId { get; init; }
    public string AlbumTitle { get; init; } = "";

    /// <summary>本地专辑目录下实际已下载的章节数。</summary>
    public int LocalChapterCount { get; init; }

    /// <summary>线上专辑总章节数。</summary>
    public int RemoteChapterCount { get; init; }

    /// <summary>线上存在但本地尚未下载的章节。</summary>
    public List<ChapterInfo> NewChapters { get; init; } = new();

    public bool HasUpdates => NewChapters.Count > 0;
}

/// <summary>
/// 本地漫画更新检查服务：对比线上专辑章节与本地已下载章节目录，
/// 找出新增章节供用户手动触发增量下载（不做任何自动检查/自动下载）。
/// </summary>
public class AlbumUpdateService
{
    private readonly JmHttpClient _client;
    private readonly ConfigService _config;

    public AlbumUpdateService(JmHttpClient client, ConfigService config)
    {
        _client = client;
        _config = config;
    }

    /// <summary>
    /// 检查本地漫画是否有新章节。
    /// </summary>
    /// <param name="albumId">专辑 ID（本地 album.json 中的 id）。</param>
    /// <param name="albumDir">本地漫画目录（comic.Path，其子目录为各章节）。</param>
    public async Task<AlbumUpdateResult> CheckAsync(long albumId, string albumDir, CancellationToken ct = default)
    {
        var resp = await _client.GetAlbumAsync(albumId, ct);
        var album = AlbumBuilder.Build(resp, _config.Current.DownloadDir);

        var localChapters = ListLocalChapterDirs(albumDir);
        return new AlbumUpdateResult
        {
            AlbumId = album.Id,
            AlbumTitle = album.Name,
            LocalChapterCount = localChapters.Count,
            RemoteChapterCount = album.ChapterInfos.Count,
            NewChapters = ComputeNewChapters(album, localChapters),
        };
    }

    /// <summary>枚举本地专辑目录下的章节目录名（跳过未完成的临时下载目录）。</summary>
    public static List<string> ListLocalChapterDirs(string albumDir)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(albumDir) || !Directory.Exists(albumDir))
        {
            return result;
        }

        foreach (var dir in Directory.EnumerateDirectories(albumDir))
        {
            var name = Path.GetFileName(dir);
            if (!string.IsNullOrEmpty(name) && !name.StartsWith(".下载中-", StringComparison.Ordinal))
            {
                result.Add(name);
            }
        }
        return result;
    }

    /// <summary>
    /// 计算新增章节：线上章节标题不在本地章节目录名集合中的章节。
    /// 大小写不敏感匹配（目录名与 ChapterTitle 由同一逻辑生成，正常应完全一致）。
    /// </summary>
    public static List<ChapterInfo> ComputeNewChapters(
        Album album, IReadOnlyCollection<string> localChapterDirs)
    {
        var local = new HashSet<string>(
            localChapterDirs.Where(d => !string.IsNullOrWhiteSpace(d)),
            StringComparer.OrdinalIgnoreCase);

        return album.ChapterInfos
            .Where(c => !string.IsNullOrWhiteSpace(c.ChapterTitle) && !local.Contains(c.ChapterTitle))
            .ToList();
    }
}
