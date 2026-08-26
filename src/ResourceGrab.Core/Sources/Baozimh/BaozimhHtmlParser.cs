using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace ResourceGrab.Core.Sources.Baozimh;

public static class BaozimhHtmlParser
{
    private static readonly HtmlParser Parser = new();

    public sealed class SearchParseResult
    {
        public List<ComicSummary> Items { get; init; } = new();
        public long TotalPages { get; init; } = 1;
    }

    public sealed class DetailParseResult
    {
        public string Title { get; init; } = "";
        public string Cover { get; init; } = "";
        public string Intro { get; init; } = "";
        public List<(string href, string title)> Chapters { get; init; } = new();
    }

    public static SearchParseResult ParseSearchResults(string html, string baseUrl)
    {
        var doc = Parser.ParseDocument(html);
        var items = new List<ComicSummary>();
        foreach (var card in doc.QuerySelectorAll(".comics-card"))
        {
            var poster = card.QuerySelector("a.comics-card__poster");
            var href = poster?.GetAttribute("href") ?? "";
            if (href.Length == 0) continue;
            var id = href.Split("/", StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            if (id.Length == 0) continue;

            var title = card.QuerySelector("h3")?.TextContent?.Trim() ?? "";
            if (title.Length == 0)
                title = poster?.GetAttribute("title")?.Trim() ?? "";
            if (title.Length == 0) continue;

            var img = card.QuerySelector("amp-img");
            var cover = img?.GetAttribute("src") ?? img?.GetAttribute("data-src") ?? "";
            // html 实体 &amp; 已由 AngleSharp 解码，无需额外处理

            items.Add(new ComicSummary
            {
                Id = WebUtility.HtmlDecode(id.Trim()),
                Title = Clean(title),
                CoverUrl = cover.Trim(),
                Author = "",
            });
        }

        // 站点搜索未暴露总页数，默认 1；保留分页器解析为未来翻页预留
        var totalPages = ParseLastPage(doc);
        return new SearchParseResult { Items = items, TotalPages = totalPages };
    }

    public static DetailParseResult ParseComicDetail(string html)
    {
        var doc = Parser.ParseDocument(html);
        var title = Clean(doc.QuerySelector(".comics-detail__title")?.TextContent ?? "");

        var intro = Clean(doc.QuerySelector(".comics-detail__desc")?.TextContent ?? "");

        var cover = doc.QuerySelector(".comics-detail__poster amp-img, .comics-detail amp-img, .comics-detail__poster img")
            ?.GetAttribute("src") ?? "";

        var chapters = new List<(string href, string title)>();
        var els = doc.QuerySelectorAll("#chapter-items a[href], #chapters_other_list a[href]");
        if (els.Length == 0)
            els = doc.QuerySelectorAll(".l-content:nth-child(3) a[href]");
        foreach (var el in els)
        {
            var href = el.GetAttribute("href") ?? "";
            var t = el.QuerySelector("span")?.TextContent?.Trim() ?? Clean(el.TextContent);
            if (href.Length > 0 && t.Length > 0)
                chapters.Add((WebUtility.HtmlDecode(href), t));
        }

        return new DetailParseResult
        {
            Title = title,
            Cover = cover.Trim(),
            Intro = intro,
            Chapters = chapters,
        };
    }

    public static List<string> ParseChapterImages(string html, string baseUrl)
    {
        var doc = Parser.ParseDocument(html);
        var urls = new List<string>();
        foreach (var el in doc.QuerySelectorAll(".comic-contain img, .comic-contain amp-img"))
        {
            var src = el.GetAttribute("src") ?? "";
            if (src.Length == 0) continue;
            src = NormalizeImageUrl(src, baseUrl);
            urls.Add(src);
        }
        return urls;
    }

    public static string NormalizeImageUrl(string src, string baseUrl)
    {
        // CDN 镜像替换：sN.bzcdn.net 在部分网络下 HTTPS 被重置/404，换到同站 static 域
        src = Regex.Replace(src, @"s\d+\.bzcdn\.net", "static-tw.baozimhcn.com", RegexOptions.IgnoreCase);
        src = src.Replace(".baozicdn.com/", "-mha1-nlams.baozicdn.com/", StringComparison.OrdinalIgnoreCase);
        src = WebUtility.HtmlDecode(src);
        if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return src;
        if (src.StartsWith("//", StringComparison.Ordinal)) return "https:" + src;
        return Abs(baseUrl, src);
    }

    public static string Abs(string baseUrl, string href)
    {
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return href;
        if (href.StartsWith("//", StringComparison.Ordinal)) return "https:" + href;
        return baseUrl.TrimEnd('/') + "/" + href.TrimStart('/');
    }

    private static long ParseLastPage(AngleSharp.Dom.IDocument doc)
    {
        // 尝试从分页器取末页，失败则 1
        var last = doc.QuerySelectorAll(".pagination a, .paginator a").LastOrDefault();
        if (last is not null && long.TryParse(Clean(last.TextContent), out var p)) return Math.Max(1, p);
        return 1;
    }

    private static string Clean(string? text) => (text ?? "").Trim();
}

