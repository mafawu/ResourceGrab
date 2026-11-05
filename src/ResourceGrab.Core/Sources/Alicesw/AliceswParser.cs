using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Sources.Novel;

namespace ResourceGrab.Core.Sources.Alicesw;

/// <summary>
/// 爱丽丝书屋 HTML 解析。选择器来自对站点五类页面的实测：
/// 搜索 <c>div.list-group-item</c>、书页 <c>.novel_title/.novel_info/.jianjie</c>、
/// 目录 <c>ul.mulu_list</c>、正文 <c>div.read-content</c>。
/// </summary>
public static class AliceswParser
{
    private static readonly HtmlParser Parser = new();

    private static readonly Regex NovelIdRegex = new(@"/novel/(\d+)\.html", RegexOptions.Compiled);
    private static readonly Regex BookHrefRegex = new(@"/book/([0-9A-Za-z]+)/([0-9A-Za-z]+)\.html", RegexOptions.Compiled);

    /// <summary>正文里需要剔除的行：站点推广、外链、导航残留。</summary>
    private static readonly string[] NoiseMarkers =
    [
        "http://", "https://", "www.", ".com", ".cc", ".xyz", ".tw",
        "爱丽丝书屋", "本站", "请收藏", "最新章节", "加入书架", "手机阅读",
        "章节错误", "求月票", "推荐票", "广告",
    ];

    // ---------------------------------------------------------------- 搜索

    public static NovelSearchResult ParseSearch(string html, int page)
    {
        var doc = Parser.ParseDocument(html);
        var items = new List<NovelSummary>();

        foreach (var node in doc.QuerySelectorAll("div.list-group-item"))
        {
            var link = node.QuerySelector("h5 a");
            var href = link?.GetAttribute("href") ?? "";
            var id = NovelIdRegex.Match(href) is { Success: true } m ? m.Groups[1].Value : "";
            if (id.Length == 0) continue;

            var title = CleanTitle(link?.TextContent);
            if (title.Length == 0) continue;

            var metaText = node.QuerySelector("p.mb-1")?.TextContent ?? "";
            var author = CleanText(node.QuerySelector("a[href*='f=author']")?.TextContent);

            var status = CleanText(node.QuerySelector("h5 small")?.TextContent).Trim('[', ']', ' ');

            var tags = node.QuerySelectorAll("a[href*='f=tag']")
                .Select(a => CleanText(a.TextContent).TrimStart('#'))
                .Where(t => t.Length > 0)
                .Distinct()
                .ToList();

            items.Add(new NovelSummary
            {
                Id = id,
                Title = title,
                Author = author,
                CoverUrl = "",
                Status = status,
                WordCountText = ExtractWordCount(metaText),
                Intro = CleanText(node.QuerySelector("p.content-txt")?.TextContent),
                Tags = tags,
                UpdatedText = ExtractUpdated(node.QuerySelector("p.timedesc")?.TextContent ?? ""),
            });
        }

        return new NovelSearchResult
        {
            Items = items,
            Page = page,
            TotalPages = ParseLastPage(doc, page),
        };
    }

    private static int ParseLastPage(IDocument doc, int fallback)
    {
        var max = fallback;
        foreach (var a in doc.QuerySelectorAll(".layui-laypage a[data-page]"))
        {
            if (int.TryParse(a.GetAttribute("data-page"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > max)
                max = n;
        }
        // 分类 / 排行页用的是 ?page=N 链接
        foreach (var a in doc.QuerySelectorAll("a[href*='page=']"))
        {
            var m = Regex.Match(a.GetAttribute("href") ?? "", @"[?&]page=(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var p) && p > max) max = p;
        }
        return max;
    }

    // ---------------------------------------------------------------- 分类 / 排行

    /// <summary>
    /// 分类列表与榜单共用一套表格行：li.one 序号 / li.sev 分类 / li.two 书名 /
    /// li.three 最新章节 / li.four 作者 / li.five 字数 / li.six 更新时间 / li.diyhot 人气。
    /// </summary>
    public static NovelSearchResult ParseList(string html, int page)
    {
        var doc = Parser.ParseDocument(html);
        var items = new List<NovelSummary>();

        foreach (var row in doc.QuerySelectorAll("div.rec_rullist ul"))
        {
            var titleLink = row.QuerySelector("li.two a");
            var href = titleLink?.GetAttribute("href") ?? "";
            var id = NovelIdRegex.Match(href) is { Success: true } m ? m.Groups[1].Value : "";
            if (id.Length == 0) continue;

            var title = CleanTitle(titleLink?.TextContent);
            if (title.Length == 0) continue;

            var category = CleanText(row.QuerySelector("li.sev a")?.TextContent);
            if (category.EndsWith("小说", StringComparison.Ordinal)) category = category[..^2];

            items.Add(new NovelSummary
            {
                Id = id,
                Title = title,
                Author = CleanText(row.QuerySelector("li.four")?.TextContent),
                Category = category,
                WordCountText = NormalizeWordCount(CleanText(row.QuerySelector("li.five")?.TextContent)),
                UpdatedText = CleanText(row.QuerySelector("li.six")?.TextContent),
                LatestChapterTitle = CleanText(row.QuerySelector("li.three a")?.TextContent),
                HitsText = CleanText(row.QuerySelector("li.diyhot")?.TextContent),
            });
        }

        return new NovelSearchResult
        {
            Items = items,
            Page = page,
            TotalPages = ParseLastPage(doc, page),
        };
    }

    /// <summary>
    /// 从站点的分类导航里取分类。只认两位以内的 /lists/{id}（书页也会用 /lists/{bookId} 表示"本书分类"，
    /// 那是五位数字，必须排除）。
    /// </summary>
    public static IReadOnlyList<NovelCategory> ParseCategories(string html)
    {
        var doc = Parser.ParseDocument(html);
        var list = new List<NovelCategory>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var a in doc.QuerySelectorAll("a[href^='/lists/']"))
        {
            var href = a.GetAttribute("href") ?? "";
            var m = Regex.Match(href, @"^/lists/(\d{1,2})\.html");
            if (!m.Success) continue;

            var id = m.Groups[1].Value;
            var name = CleanText(a.TextContent);
            if (name.Length == 0 || name.Length > 8) continue;
            if (name.EndsWith("小说", StringComparison.Ordinal)) name = name[..^2];
            if (!seen.Add(id)) continue;

            list.Add(new NovelCategory { Id = id, Name = name, Url = href });
        }

        return list.OrderBy(c => int.Parse(c.Id, CultureInfo.InvariantCulture)).ToList();
    }

    // ---------------------------------------------------------------- 书页

    public static NovelDetail ParseDetail(string html, string novelId)
    {
        var doc = Parser.ParseDocument(html);

        var title = CleanText(doc.QuerySelector(".novel_title")?.TextContent);
        if (title.Length == 0)
            title = CleanText(doc.QuerySelector(".mu_h1 h1")?.TextContent);

        var cover = doc.QuerySelector("img.lazyload_book_cover")?.GetAttribute("data-src")
                    ?? doc.QuerySelector("img.lazyload_book_cover")?.GetAttribute("src")
                    ?? "";

        var intro = ParseIntro(doc);

        var infoText = doc.QuerySelector(".novel_info")?.TextContent ?? "";

        var author = CleanText(doc.QuerySelector(".novel_info a[href*='f=author']")?.TextContent);
        var category = CleanText(doc.QuerySelector(".novel_info a[href*='/lists/']")?.TextContent);
        var status = MatchGroup(infoText, @"状\s*态：\s*(\S+)");
        var wordCount = NormalizeWordCount(MatchGroup(infoText, @"字\s*数：\s*([\d.]+\s*万?字?)"));
        var chapterCountText = MatchGroup(infoText, @"章\s*节：\s*(\d+)");
        var latest = CleanText(doc.QuerySelector(".novel_info a[href*='/book/']")?.TextContent);

        var tags = doc.QuerySelectorAll(".tags_list a")
            .Select(a => CleanText(a.TextContent).TrimStart('#'))
            .Where(t => t.Length > 0 && !t.StartsWith('注'))
            .Distinct()
            .ToList();

        return new NovelDetail
        {
            Id = novelId,
            Title = title,
            Author = author,
            CoverUrl = cover.Trim(),
            Intro = intro,
            Category = category,
            Status = status,
            WordCountText = wordCount,
            ChapterCount = int.TryParse(chapterCountText, out var cc) ? cc : 0,
            Tags = tags,
            LatestChapterTitle = latest,
            Chapters = Array.Empty<NovelChapter>(),
        };
    }

    // ---------------------------------------------------------------- 目录

    public static IReadOnlyList<NovelChapter> ParseChapters(string html)
    {
        var doc = Parser.ParseDocument(html);
        var list = new List<NovelChapter>();

        foreach (var a in doc.QuerySelectorAll("ul.mulu_list li a"))
        {
            var href = a.GetAttribute("href") ?? "";
            var m = BookHrefRegex.Match(href);
            if (!m.Success) continue;

            var title = CleanText(a.TextContent);
            if (title.Length == 0) continue;

            list.Add(new NovelChapter
            {
                Index = list.Count,
                Title = title,
                BookId = m.Groups[1].Value,
                Key = m.Groups[2].Value,
                Url = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : href,
            });
        }

        return list;
    }

    /// <summary>
    /// 解析正文。返回按段落拼好的纯文本：每段前置两个全角空格，
    /// 段间空一行——与本地 txt 的排版一致，阅读器不需要另做缩进。
    /// </summary>
    public static string ParseChapterText(string html)
    {
        var doc = Parser.ParseDocument(html);
        var box = doc.QuerySelector("div.read-content") ?? doc.QuerySelector(".read-content");
        if (box is null) return "";

        var paragraphs = new List<string>();
        foreach (var p in box.QuerySelectorAll("p"))
        {
            var text = CleanParagraph(p.TextContent);
            if (text.Length == 0) continue;
            if (IsNoise(text)) continue;
            if (paragraphs.Count > 0 && paragraphs[^1] == text) continue; // 相邻重复段
            paragraphs.Add(text);
        }

        // 部分书把正文放在直接文本节点里（没有 <p>），兜底取整块
        if (paragraphs.Count == 0)
        {
            foreach (var line in (box.TextContent ?? "").Split('\n'))
            {
                var text = CleanParagraph(line);
                if (text.Length > 0 && !IsNoise(text)) paragraphs.Add(text);
            }
        }

        var sb = new StringBuilder();
        foreach (var p in paragraphs)
        {
            sb.Append("　　").Append(p).Append('\n').Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ---------------------------------------------------------------- 工具

    private static bool IsNoise(string text)
    {
        if (text.Length <= 2) return false;
        foreach (var marker in NoiseMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string CleanParagraph(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = raw.Replace('\u00a0', ' ').Replace("\r", "").Replace("\n", " ");
        s = s.Trim().TrimStart('　').Trim();
        // 压缩行内连续空白，但保留中文标点
        s = Regex.Replace(s, @"[ \t]{2,}", " ");
        return s.Trim();
    }

    public static string CleanText(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = WebUtility.HtmlDecode(raw).Replace('\u00a0', ' ').Replace("\r", "").Replace("\n", " ");
        return Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    /// <summary>搜索结果标题带排名前缀（"1. 书名"），去掉。</summary>
    public static string CleanTitle(string? raw)
    {
        var s = CleanText(raw);
        return Regex.Replace(s, @"^\s*\d+\s*[.、]\s*", "");
    }

    /// <summary>书页的简介：优先 .jianjie 里的段落（可能有多个），再退到 meta description。</summary>
    private static string ParseIntro(IDocument doc)
    {
        var parts = doc.QuerySelectorAll("div.jianjie p")
            .Select(p => CleanParagraph(p.TextContent))
            .Where(t => t.Length > 0)
            .ToList();
        if (parts.Count > 0) return string.Join("\n", parts);

        var box = CleanParagraph(doc.QuerySelector(".box_intro")?.TextContent);
        if (box.Length > 0) return box;

        return CleanParagraph(doc.QuerySelector("meta[name=description]")?.GetAttribute("content"));
    }

    /// <summary>字数统一后缀：站点有的写 "12.12万"，有的写 "12.12万字"。</summary>
    private static string NormalizeWordCount(string raw)
    {
        var s = (raw ?? "").Replace(" ", "").Trim();
        if (s.Length == 0) return "";
        if (s.EndsWith('字')) return s;
        return s + "字";
    }

    private static string MatchGroup(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    /// <summary>从 "字数：12.12万　浏览：8750" 里取字数。</summary>
    private static string ExtractWordCount(string metaText)
    {
        var m = Regex.Match(metaText ?? "", @"字\s*数：\s*([\d.]+\s*万?字?)");
        return m.Success ? NormalizeWordCount(m.Groups[1].Value) : "";
    }

    /// <summary>从 "发布时间：… 更新时间：2025-06-08 08:56" 里取更新时间的日期部分。</summary>
    private static string ExtractUpdated(string text)
    {
        var m = Regex.Match(text ?? "", @"更新时间：\s*([\d-]+\s*[\d:]*)");
        if (!m.Success) return "";
        var v = m.Groups[1].Value.Trim();
        return v.Length >= 10 ? v[5..] : v;
    }
}
