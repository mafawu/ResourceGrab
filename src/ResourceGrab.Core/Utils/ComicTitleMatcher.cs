using System.Text.RegularExpressions;

namespace ResourceGrab.Core.Utils;

/// <summary>
/// 本地漫画标题与在线搜索候选的匹配打分器（纯函数、无状态）。
/// 归一化（剔除汉化/翻译标记、空白与标点）后做编辑距离相似度，作者一致加分；
/// 自动采纳要求最高分达标且与次高分有明显差距，否则视为歧义交由人工挑选。
/// </summary>
public static class ComicTitleMatcher
{
    /// <summary>自动采纳的最低相似度。</summary>
    public const double AutoMatchThreshold = 0.85;

    /// <summary>自动采纳要求最高分领先次高分的差距。</summary>
    public const double AutoMatchMargin = 0.1;

    /// <summary>汉化/翻译标记（中英括号都处理），归一化时剔除以免汉化组名干扰相似度。</summary>
    private static readonly Regex TranslationMarkRegex = new(
        @"[【\[][^【】\[\]]*(?:汉化|漢化|翻訳|翻譯|翻译|中国翻訳|中文)[^【】\[\]]*[】\]]"
        + @"|[（\(][^（）()]*?(?:汉化|漢化|翻訳|翻譯|翻译|中国|中文)[^（）()]*?[）\)]",
        RegexOptions.Compiled);

    /// <summary>空白与标点/符号（全半角统一剔除）。</summary>
    private static readonly Regex JunkCharsRegex = new(@"[\s\p{P}\p{S}]+", RegexOptions.Compiled);

    /// <summary>展会标记（(C97) 等，可在标题任意位置），禁漫候选标题常保留而本地解析已剥离。</summary>
    private static readonly Regex EventTagRegex = new(@"[（(]c[0-9]+[）)]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>标题归一化：NFKC 折叠全半角、剔除汉化/翻译标记、空白与标点，转小写。用于相似度比较。</summary>
    public static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "";
        }
        string text;
        try
        {
            // NFKC 把全角字母数字/半角片假名折叠为标准形，使全半角写法等价
            text = title.Normalize(System.Text.NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            text = title;
        }
        text = TranslationMarkRegex.Replace(text, "");
        text = EventTagRegex.Replace(text, "");
        return JunkCharsRegex.Replace(text, "").ToLowerInvariant();
    }

    /// <summary>
    /// 计算本地标题与候选标题的相似度得分（0~1）。
    /// 作者一致加 0.1（本地解析出的作者列表与候选作者字段任一互相包含即视为一致）。
    /// </summary>
    public static double Score(string localTitle, string candidateTitle,
        IEnumerable<string>? localAuthors = null, string? candidateAuthor = null)
    {
        var a = NormalizeTitle(localTitle);
        var b = NormalizeTitle(candidateTitle);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var score = a == b ? 1.0 : Similarity(a, b);
        // 一方完整包含另一方时视为高度相似（站点标题常带前缀/后缀修饰）
        if (score < 0.9 && a.Length >= 4 && b.Length >= 4
            && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)))
        {
            score = Math.Max(score, 0.9);
        }
        if (score < 1.0 && HasAuthorMatch(localAuthors, candidateAuthor))
        {
            score = Math.Min(1.0, score + 0.1);
        }
        return score;
    }

    /// <summary>按分降序的分数序列是否满足自动采纳条件（最高分达标且明显领先次高分）。</summary>
    public static bool AcceptsAutoMatch(IReadOnlyList<double> sortedScores)
    {
        if (sortedScores.Count == 0 || sortedScores[0] < AutoMatchThreshold)
        {
            return false;
        }
        return sortedScores.Count == 1 || sortedScores[0] - sortedScores[1] >= AutoMatchMargin;
    }

    /// <summary>
    /// 从原始目录名提取疑似禁漫专辑 id（5~7 位独立数字，可带 JM 前缀）。
    /// 仅为「疑似」：是否真为专辑 id 由搜索接口验证（数字 id 搜索命中唯一专辑时站点直接返回该专辑）。
    /// </summary>
    public static IReadOnlyList<string> ExtractJmAlbumIds(string folderName)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return result;
        }
        foreach (Match m in Regex.Matches(folderName, @"(?:^|[^0-9A-Za-z])(?:JM)?([0-9]{5,7})(?![0-9A-Za-z])", RegexOptions.IgnoreCase))
        {
            var id = m.Groups[1].Value;
            if (!result.Contains(id))
            {
                result.Add(id);
            }
        }
        return result;
    }

    private static bool HasAuthorMatch(IEnumerable<string>? localAuthors, string? candidateAuthor)
    {
        if (localAuthors is null || string.IsNullOrWhiteSpace(candidateAuthor))
        {
            return false;
        }
        var cand = candidateAuthor.Trim();
        return localAuthors.Any(a => !string.IsNullOrWhiteSpace(a)
            && (cand.Contains(a.Trim(), StringComparison.OrdinalIgnoreCase)
                || a.Trim().Contains(cand, StringComparison.OrdinalIgnoreCase)));
    }

    private static double Similarity(string a, string b)
        => 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);

    private static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
