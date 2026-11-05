using System.Text;
using System.Text.RegularExpressions;

namespace ResourceGrab.Core.Services;

/// <summary>本地文本按章节标题切分后的一段。</summary>
public sealed record NovelTextSpan(string Title, int Offset, int Length);

/// <summary>
/// 本地 txt 的章节切分：用阅读设置里的 <c>chapterPattern</c> 找章节行，
/// 命中 ≥2 个才认为"能分出章节"，否则当作整本无章节处理（不硬编）。
/// </summary>
public static class NovelTextIndexer
{
    public const string DefaultPattern = @"^\s*(第[0-9零一二三四五六七八九十百千万]+[章节卷回部]|Chapter\s+\d+).*$";

    public static IReadOnlyList<NovelTextSpan> Split(string text, string? chapterPattern)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<NovelTextSpan>();

        var pattern = string.IsNullOrWhiteSpace(chapterPattern) ? DefaultPattern : chapterPattern;
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Multiline | RegexOptions.Compiled);
        }
        catch (ArgumentException)
        {
            regex = new Regex(DefaultPattern, RegexOptions.Multiline | RegexOptions.Compiled);
        }

        var starts = new List<(int Index, string Title)>();
        foreach (Match m in regex.Matches(text))
        {
            var line = m.Value.Trim();
            if (line.Length == 0) continue;
            // 只认行首的标题行：避免正文里"第一章节"这类词误判
            if (m.Index > 0)
            {
                var prev = text[m.Index - 1];
                if (prev != '\n' && prev != '\r') continue;
            }
            starts.Add((m.Index, TrimTitle(line)));
        }

        if (starts.Count < 2) return Array.Empty<NovelTextSpan>();

        var spans = new List<NovelTextSpan>(starts.Count);
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i].Index;
            var end = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
            var len = Math.Max(0, end - start);
            spans.Add(new NovelTextSpan(starts[i].Title, start, len));
        }
        return spans;
    }

    /// <summary>读取文件并按章切分（自动编码探测与 <see cref="Encoding"/> 兼容）。</summary>
    public static async Task<(string Text, IReadOnlyList<NovelTextSpan> Spans)> ReadAndSplitAsync(
        string path, string? chapterPattern, CancellationToken ct = default)
    {
        var text = await ReadTextAsync(path, ct);
        return (text, Split(text, chapterPattern));
    }

    public static async Task<string> ReadTextAsync(string path, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        return Decode(bytes);
    }

    /// <summary>与 NovelReaderView 的解码顺序保持一致（BOM → 严格 UTF-8 → GB18030）。</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            var strict = Encoding.GetEncoding("utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var s = strict.GetString(bytes);
            if (!s.Contains('\uFFFD')) return s;
        }
        catch { }

        try
        {
            var s = Encoding.UTF8.GetString(bytes);
            if (!s.Contains('\uFFFD')) return s;
        }
        catch { }

        try { return Encoding.GetEncoding(936).GetString(bytes); } catch { }
        try { return Encoding.GetEncoding("GB18030").GetString(bytes); } catch { }
        return Encoding.UTF8.GetString(bytes);
    }

    private static string TrimTitle(string line)
    {
        var s = Regex.Replace(line, @"\s+", " ").Trim();
        return s.Length > 80 ? s[..80] : s;
    }
}
