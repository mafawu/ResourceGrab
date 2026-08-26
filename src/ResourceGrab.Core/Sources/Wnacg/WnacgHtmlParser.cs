using System.Text.Json;
using System.Text.Json.Serialization;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace ResourceGrab.Core.Sources.Wnacg;

/// <summary>
/// wnacg HTML 解析器：站点改版时只需调整本文件的选择器。
/// 页面结构与 https://github.com/lanyeeee/wnacg-downloader 的解析逻辑保持一致，
/// 差异点：搜索结果页没有"总结果数 &lt;b&gt;"，改用分页器末页计算总页数。
/// </summary>
public static class WnacgHtmlParser
{
    private static readonly HtmlParser Parser = new();

    private sealed class ImgListItem
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("caption")] public string Caption { get; set; } = "";
    }

    /// <summary>搜索/分类页解析结果。</summary>
    public sealed class SearchParseResult
    {
        public List<ComicSummary> Items { get; init; } = new();
        public long TotalPages { get; init; } = 1;
    }

    /// <summary>详情页解析结果。</summary>
    public sealed class DetailParseResult
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public string Cover { get; init; } = "";
        public string Category { get; init; } = "";
        public string Intro { get; init; } = "";
        public long ImageCount { get; init; }
        public List<string> Tags { get; init; } = new();
    }

    public static SearchParseResult ParseSearchResults(string html)
    {
        var doc = Parser.ParseDocument(html);
        var items = new List<ComicSummary>();
        foreach (var li in doc.QuerySelectorAll(".li.gallary_item"))
        {
            var summary = ParseSearchItem(li);
            if (summary is not null)
            {
                items.Add(summary);
            }
        }

        var currentPage = ParseCurrentPage(doc);
        var lastPage = ParseLastPage(doc);
        return new SearchParseResult
        {
            Items = items,
            TotalPages = Math.Max(currentPage, lastPage),
        };
    }

    private static ComicSummary? ParseSearchItem(IElement li)
    {
        var titleLink = li.QuerySelector(".title > a");
        if (titleLink is null)
        {
            return null;
        }
        var href = titleLink.GetAttribute("href") ?? "";
        var id = ExtractId(href, "/photos-index-aid-");
        if (id is null)
        {
            return null;
        }

        var img = li.QuerySelector("img");
        var coverSrc = img?.GetAttribute("src") ?? "";
        return new ComicSummary
        {
            Id = id,
            Title = Clean(titleLink.TextContent),
            CoverUrl = coverSrc.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? coverSrc
                : "https:" + coverSrc,
            Author = "",
        };
    }

    /// <summary>从链接提取形如 /photos-index-aid-{id}.html 的 id；失败返回 null。</summary>
    private static string? ExtractId(string href, string prefix)
    {
        if (!href.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }
        var rest = href[prefix.Length..];
        if (!rest.EndsWith(".html", StringComparison.Ordinal))
        {
            return null;
        }
        return rest[..^".html".Length];
    }

    public static DetailParseResult ParseComicDetail(string html)
    {
        var doc = Parser.ParseDocument(html);

        var link = doc.QuerySelector("head > link[href^='/feed-index-aid-']");
        var id = link is null ? "" : ExtractId(link.GetAttribute("href") ?? "", "/feed-index-aid-") ?? "";

        var h2 = doc.QuerySelector("#bodywrap > h2");
        var title = Clean(h2?.TextContent ?? "");

        var coverSrc = doc.QuerySelector(".asTBcell.uwthumb > img")?.GetAttribute("src") ?? "";
        // 站点返回的封面 src 带多个前导斜杠，去掉后补 https 前缀
        var cover = coverSrc.TrimStart('/');
        cover = cover.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? cover : "https://" + cover;

        var labels = doc.QuerySelectorAll(".asTBcell.uwconn > label").Select(l => Clean(l.TextContent)).ToList();
        var category = labels.FirstOrDefault(l => l.StartsWith("分類：", StringComparison.Ordinal))?.Replace("分類：", "") ?? "";
        var imageCountText = labels.FirstOrDefault(l => l.StartsWith("頁數：", StringComparison.Ordinal));
        var imageCount = 0L;
        if (imageCountText is not null && imageCountText.EndsWith("P", StringComparison.Ordinal))
        {
            long.TryParse(imageCountText[3..^1].Replace(",", ""), out imageCount);
        }

        var tags = doc.QuerySelectorAll(".tagshow")
            .Select(t => Clean(t.TextContent))
            .Where(t => t.Length > 0)
            .ToList();

        var intro = doc.QuerySelector(".asTBcell.uwconn > p")?.TextContent ?? "";
        intro = Clean(intro);

        return new DetailParseResult
        {
            Id = id,
            Title = title,
            Cover = cover,
            Category = category,
            Intro = intro,
            ImageCount = imageCount,
            Tags = tags,
        };
    }

    /// <summary>
    /// 解析图片列表：从页面中 "var imglist = [...]" 行提取 JSON。
    /// 原始格式形如 { url: fast_img_host+"//img5.qy0.ru/...", caption: "[001]"}，
    /// 需要把键补引号、去掉 fast_img_host+ 前缀；最后一张是收藏图，需过滤。
    /// </summary>
    public static List<string> ParseImgList(string html)
    {
        var line = html.Split('\n').FirstOrDefault(l => l.Contains("var imglist = ", StringComparison.Ordinal));
        if (line is null)
        {
            throw new JmException("没有找到包含`imglist`的行");
        }
        var start = line.IndexOf('[');
        var end = line.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            throw new JmException("没有在`imglist`行中找到数组");
        }

        var json = line[start..(end + 1)]
            .Replace("url:", "\"url\":")
            .Replace("caption:", "\"caption\":")
            .Replace("fast_img_host+", "")
            .Replace("\\\"", "\"");

        var items = JsonSerializer.Deserialize<List<ImgListItem>>(json) ?? new List<ImgListItem>();
        return items
            .Select(i => i.Url.Trim())
            .Where(u => u.Length > 0 && !u.EndsWith("shoucang.jpg", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>解析首页分类链接：/albums-index-cate-{id}.html → 分类列表（去重、过滤"更多"）。</summary>
    public static List<ComicCategory> ParseCategories(string html)
    {
        var doc = Parser.ParseDocument(html);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in doc.QuerySelectorAll("a[href^='/albums-index-cate-']"))
        {
            var href = a.GetAttribute("href") ?? "";
            var id = ExtractId(href.Replace("-page-1", ""), "/albums-index-cate-");
            var name = Clean(a.TextContent);
            if (id is null || name.Length == 0 || name.Contains("更多", StringComparison.Ordinal))
            {
                continue;
            }
            seen.TryAdd(id, name);
        }
        return seen.Select(kv => new ComicCategory { Id = kv.Key, Name = kv.Value }).ToList();
    }

    private static long ParseCurrentPage(IDocument doc)
    {
        var span = doc.QuerySelector(".thispage");
        if (span is null || !long.TryParse(Clean(span.TextContent), out var page))
        {
            return 1;
        }
        return page;
    }

    /// <summary>取分页器最后一个链接的页码作为总页数；无链接（仅一页）时返回 1。</summary>
    private static long ParseLastPage(IDocument doc)
    {
        var link = doc.QuerySelectorAll(".f_left.paginator > a").LastOrDefault();
        if (link is null || !long.TryParse(Clean(link.TextContent), out var page))
        {
            return 1;
        }
        return page;
    }

    private static string Clean(string? text) => (text ?? "").Trim();
}
