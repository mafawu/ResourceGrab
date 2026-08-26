using System.Text.RegularExpressions;

namespace ResourceGrab.Core.Utils;

/// <summary>
/// 从漫画目录/文件名解析干净标题与结构化标签（借鉴 MangaReader metadata_parser.py，修复原版边界 bug）。
/// 纯函数、无状态：输入不含扩展名的文件名，输出 (干净标题, 标签集合)。
/// 标签统一为「类别:值」格式：平台/组/作者/会场/作品/汉化/其他/标题。
/// </summary>
public static class MangaFilenameParser
{
    // 平台前缀：(Fantia) / （Fantia）——限纯字母，避免把 (合集)、(C97) 当平台
    private static readonly Regex PlatformRegex = new(@"^[\(（]([A-Za-z]+)[\)）](.*)", RegexOptions.Compiled);

    // 组+作者：[团队 (作者)]，组名内不允许含方括号，避免跨括号吞内容
    private static readonly Regex GroupAuthorRegex = new(@"\[([^\[\]]+?) \(([^\[\]]+?)\)\]", RegexOptions.Compiled);

    // 单独作者 / 剩余方括号标签
    private static readonly Regex BracketRegex = new(@"\[(.*?)\]", RegexOptions.Compiled);

    // 展会：任意位置的 (C97) / (c97)，可被 [汉化组] 等夹住
    private static readonly Regex EventRegex = new(@"\(([Cc][0-9]+)\)", RegexOptions.Compiled);

    // 作品名：(作品名) —— 括号内不含数字，且括号不在方括号内
    private static readonly Regex SeriesRegex = new(@"[\(（]([^()（）\d]*?)[\)）](?![^[]*\])", RegexOptions.Compiled);

    private static readonly string[] HanhuaKeywords = { "中国翻訳", "中国翻译", "中國翻譯", "中國翻訳", "中文翻译" };

    /// <summary>单独作者回退时视为「非作者标签」的关键词：命中则不当作作者。</summary>
    private static readonly string[] NotAuthorKeywords =
    {
        "汉化", "漢化", "翻訳", "翻译", "翻譯",
        "無修正", "无修正", "無修", "全彩", "中文", "彩色", "繁中", "简中",
    };

    /// <summary>解析漫画文件名（不含扩展名），返回 (干净标题, 标签集合)。</summary>
    public static (string Title, HashSet<string> Tags) Parse(string fileBasename)
    {
        var tags = new HashSet<string>();
        var text = (fileBasename ?? "").Trim();

        // 1) 平台前缀：(Fantia) 标题
        var m = PlatformRegex.Match(text);
        if (m.Success)
        {
            tags.Add($"平台:{m.Groups[1].Value}");
            text = m.Groups[2].Value.Trim();
        }

        // 2) [团队 (作者)]，优先于单独 [作者]
        m = GroupAuthorRegex.Match(text);
        if (m.Success)
        {
            tags.Add($"组:{m.Groups[1].Value}");
            tags.Add($"作者:{m.Groups[2].Value}");
            text = RemoveAt(text, m.Index, m.Length);
        }
        else
        {
            m = BracketRegex.Match(text);
            if (m.Success && !NotAuthorKeywords.Any(k => m.Groups[1].Value.Contains(k)))
            {
                tags.Add($"作者:{m.Groups[1].Value}");
                text = RemoveAt(text, m.Index, m.Length);
            }
        }

        // 3) 展会：(C97)
        m = EventRegex.Match(text);
        if (m.Success)
        {
            tags.Add($"会场:{m.Groups[1].Value}");
            text = RemoveAt(text, m.Index, m.Length);
        }

        // 4) 作品名：(作品名)——只移除括号块本身，保留其后可能存在的标签（原版会截断其后所有内容）
        m = SeriesRegex.Match(text);
        if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
        {
            tags.Add($"作品:{m.Groups[1].Value}");
            text = RemoveAt(text, m.Index, m.Length);
        }

        // 5) 剩余方括号标签：循环剥离并归类
        while ((m = BracketRegex.Match(text)).Success)
        {
            var content = m.Groups[1].Value;
            if (HanhuaKeywords.Any(k => content.Contains(k)))
            {
                tags.Add("汉化:中国翻译");
            }
            else if (content.Contains("汉化") || content.Contains("漢化")
                     || content.Contains("翻訳") || content.Contains("翻译") || content.Contains("翻譯"))
            {
                tags.Add($"汉化:{content}");
            }
            else if (content.Contains("無修正") || content.Contains("无修正") || content.Contains("無修"))
            {
                tags.Add("其他:无修正");
            }
            else
            {
                tags.Add($"其他:{content}");
            }
            text = RemoveAt(text, m.Index, m.Length);
        }

        var cleanTitle = text.Trim();
        if (cleanTitle.Length > 0)
        {
            tags.Add($"标题:{cleanTitle}");
        }

        return (cleanTitle, tags);
    }

    private static string RemoveAt(string input, int index, int length)
        => input.Remove(index, length).Trim();
}
