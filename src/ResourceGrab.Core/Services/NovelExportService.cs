using System.Text;
using ResourceGrab.Core.Sources.Novel;

namespace ResourceGrab.Core.Services;

/// <summary>
/// 整本导出为本地 txt：章节标题 + 正文顺序写入一个文件，
/// 产物直接落在用户指定目录，之后由现有的本地索引/阅读器接管。
/// </summary>
public sealed class NovelExportService(NovelChapterCache cache)
{
    /// <summary>导出进度：(已完成章数, 总章数, 当前章节标题)。</summary>
    public event Action<int, int, string>? Progress;

    public string DefaultDirectory => AppPaths.NovelExportDir;

    public async Task<string> ExportAsync(
        INovelSource source,
        NovelDetail detail,
        string? targetDirectory = null,
        CancellationToken ct = default)
    {
        var dir = string.IsNullOrWhiteSpace(targetDirectory) ? DefaultDirectory : targetDirectory;
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, SafeFileName(detail.Title) + ".txt");
        var chapters = detail.Chapters;
        var total = Math.Max(1, chapters.Count);

        var sb = new StringBuilder();
        sb.Append(detail.Title).Append('\n');
        if (!string.IsNullOrWhiteSpace(detail.Author)) sb.Append("作者：").Append(detail.Author).Append('\n');
        if (!string.IsNullOrWhiteSpace(detail.Category)) sb.Append("分类：").Append(detail.Category).Append('\n');
        if (!string.IsNullOrWhiteSpace(detail.WordCountText)) sb.Append("字数：").Append(detail.WordCountText).Append('\n');
        sb.Append("来源：").Append(source.Info.DisplayName).Append('\n');
        sb.Append("导出时间：").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append('\n');
        sb.Append("\n\n");

        for (var i = 0; i < chapters.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var chapter = chapters[i];
            Progress?.Invoke(i, total, chapter.Title);

            var text = await cache.TryReadAsync(source.Info.Id, chapter.BookId, chapter.Key, ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = await source.GetChapterTextAsync(chapter, ct);
                await cache.WriteAsync(source.Info.Id, chapter.BookId, chapter.Key, text, ct);
            }

            sb.Append(chapter.Title).Append('\n').Append('\n');
            sb.Append(text).Append('\n').Append('\n');
        }

        Progress?.Invoke(total, total, "");
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false), ct);
        return path;
    }

    /// <summary>只有章节列表、没有正文时给出可读文件名。</summary>
    public static string SafeFileName(string? name)
    {
        var s = string.IsNullOrWhiteSpace(name) ? "未命名" : name.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        s = s.Replace('/', '_').Replace('\\', '_');
        return s.Length > 80 ? s[..80] : s;
    }
}
