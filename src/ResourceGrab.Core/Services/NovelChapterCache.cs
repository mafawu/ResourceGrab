using System.Text;

namespace ResourceGrab.Core.Services;

/// <summary>
/// 在线章节正文缓存：{config}\novel-cache\{sourceId}\{bookId}\{key}.txt。
/// 读过即缓存，断网可读；上限按条目数粗控（超出后按写入时间淘汰最旧的）。
/// </summary>
public sealed class NovelChapterCache
{
    private readonly string _root;
    private readonly int _maxEntries;

    public NovelChapterCache(string? root = null, int maxEntries = 4000)
    {
        _root = string.IsNullOrWhiteSpace(root) ? AppPaths.NovelChapterCacheDir : root;
        _maxEntries = Math.Max(200, maxEntries);
    }

    public string Root => _root;

    public string PathFor(string sourceId, string bookId, string key)
    {
        var safeSource = Safe(sourceId);
        var safeBook = Safe(bookId);
        var safeKey = Safe(key);
        return Path.Combine(_root, safeSource, safeBook, safeKey + ".txt");
    }

    public async Task<string?> TryReadAsync(string sourceId, string bookId, string key, CancellationToken ct = default)
    {
        var path = PathFor(sourceId, bookId, key);
        try
        {
            if (!File.Exists(path)) return null;
            var text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    public async Task WriteAsync(string sourceId, string bookId, string key, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var path = PathFor(sourceId, bookId, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct);
            await TrimAsync(ct);
        }
        catch
        {
            // 缓存失败不影响阅读
        }
    }

    public bool Exists(string sourceId, string bookId, string key) => File.Exists(PathFor(sourceId, bookId, key));

    public int CountCached(string sourceId, string bookId)
    {
        try
        {
            var dir = Path.Combine(_root, Safe(sourceId), Safe(bookId));
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.txt").Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private async Task TrimAsync(CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            var files = Directory.GetFiles(_root, "*.txt", SearchOption.AllDirectories);
            if (files.Length <= _maxEntries) return;

            var remove = files
                .Select(f => (File: f, Time: File.GetLastWriteTimeUtc(f)))
                .OrderBy(t => t.Time)
                .Take(files.Length - _maxEntries);

            await Task.Run(() =>
            {
                foreach (var (file, _) in remove)
                {
                    try { File.Delete(file); } catch { }
                }
            }, ct);
        }
        catch
        {
            // 清理失败下次再来
        }
    }

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(invalid.Contains(ch) || ch == '/' || ch == '\\' ? '_' : ch);
        return sb.Length == 0 ? "_" : sb.ToString();
    }
}
