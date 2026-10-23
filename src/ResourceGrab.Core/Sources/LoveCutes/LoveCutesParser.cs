using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace ResourceGrab.Core.Sources.LoveCutes;

/// <summary>
/// lovecutes HTML / JSON-LD 解析器。
/// 站点改版时只需调整本文件的选择器与 JSON 字段名。
/// <para>
/// 站点结构（2026-10 实测）：
/// <list type="bullet">
/// <item>列表页卡片：<c>article.excerpt[data-track-content-id][data-content-kind]</c>，
/// 链接是 <c>a.imgbox-link[href="/article/{id}/"]</c>，封面是 <c>img.imgbox-img[src]</c>。</item>
/// <item>详情页：JSON-LD <c>ImageGallery</c> 给出本页 10 张图 + <c>pagination.totalPages</c>，
/// 完整图集需按 <c>/page/{n}/</c> 逐页拼接；标题取 <c>h1</c>（JSON-LD 的 name 带"分类 - 站名"后缀）。</item>
/// <item>维度索引页：JSON-LD <c>CollectionPage.mainEntity</c> 是 <c>ItemList</c>，
/// 含 <c>numberOfItems</c> 总数与 <c>itemListElement[]</c>（name + url）。</item>
/// </list>
/// </para>
/// </summary>
public static class LoveCutesParser
{
    private static readonly HtmlParser Parser = new();

    /// <summary>列表 / 搜索结果解析结果。</summary>
    public sealed class ListParseResult
    {
        public List<ComicSummary> Items { get; init; } = new();

        /// <summary>总页数。</summary>
        public long TotalPages { get; init; } = 1;

        /// <summary>站点报告的总命中数；未提供时为 0。</summary>
        public long Total { get; init; }
    }

    /// <summary>图集详情单页解析结果。</summary>
    public sealed class GalleryParseResult
    {
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        public List<string> Keywords { get; init; } = new();

        /// <summary>图集总图片数（JSON-LD <c>numberOfItems</c>），非本页张数。</summary>
        public long ImageCount { get; init; }

        public long CurrentPage { get; init; } = 1;
        public long TotalPages { get; init; } = 1;

        /// <summary>本页图片直链（顺序即站点顺序）。</summary>
        public List<string> ImageUrls { get; init; } = new();

        public string CoverUrl { get; init; } = "";

        /// <summary>所属分类的数字 id（来自 BreadcrumbList 的 /type/{n}/）。</summary>
        public string CategoryId { get; init; } = "";
    }

    /// <summary>维度索引解析结果。</summary>
    public sealed class IndexParseResult
    {
        /// <summary>该索引下的条目总数（JSON-LD <c>numberOfItems</c>）。</summary>
        public long Total { get; init; }

        public List<TaxonomyItem> Items { get; init; } = new();
    }

    // ====================== 列表 / 搜索 ======================

    public static ListParseResult ParseArticleList(string html)
    {
        var doc = Parser.ParseDocument(html);
        var items = new List<ComicSummary>();

        foreach (var card in doc.QuerySelectorAll("article.excerpt"))
        {
            var kind = card.GetAttribute("data-content-kind");
            // 列表里混有视频位（data-content-kind="video"），图集源只收 album
            if (kind is not null && !kind.Equals("album", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var link = card.QuerySelector("a.imgbox-link") ?? card.QuerySelector("a[href*='/article/']");
            var id = card.GetAttribute("data-track-content-id");
            if (string.IsNullOrWhiteSpace(id))
            {
                id = ExtractArticleId(link?.GetAttribute("href"));
            }
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var img = card.QuerySelector("img.imgbox-img") ?? card.QuerySelector("img");
            var title = Clean(link?.GetAttribute("title") ?? img?.GetAttribute("alt") ?? link?.TextContent);
            // 站点在每张卡上带一个分类入口（a[href="/type/{n}/"]），其文本即分类名
            var category = Clean(card.QuerySelector("a[href^='/type/']")?.TextContent);

            items.Add(new ComicSummary
            {
                Id = id!,
                Title = title,
                CoverUrl = Absolutize(img?.GetAttribute("src")),
                // 标题形如 "[私房] 人妻玩物 - …"，与漫画源一致填充作者/社团
                Author = ExtractAuthorPrefix(title),
                Category = category,
            });
        }

        return new ListParseResult
        {
            Items = items,
            TotalPages = ParseTotalPages(doc),
            Total = ParseReportedTotal(doc),
        };
    }

    /// <summary>从 <c>/article/{id}/</c> 链接提取数字 id。</summary>
    internal static string ExtractArticleId(string? href)
    {
        if (string.IsNullOrEmpty(href)) return "";
        var m = Regex.Match(href, @"/article/(\d+)/?");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>
    /// 标题开头的方/书名号标记即作者或社团，如 <c>[DJAWA] …</c>、<c>【社团】…</c>。
    /// 站点标题本身不含多作者分隔约定，故只取首段；无标记时返回空串。
    /// </summary>
    internal static string ExtractAuthorPrefix(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var m = Regex.Match(title, @"^\s*[\[【]([^\]】]{1,40})[\]】]");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    /// <summary>
    /// 总页数：优先取分页条 <c>a[data-page]</c> 的最大值，兜底扫 <c>/page/{n}/</c> 链接。
    /// </summary>
    private static long ParseTotalPages(IDocument doc)
    {
        long max = 1;

        foreach (var a in doc.QuerySelectorAll("a[data-page]"))
        {
            if (long.TryParse(a.GetAttribute("data-page"), out var n) && n > max) max = n;
        }

        if (max <= 1)
        {
            foreach (var a in doc.QuerySelectorAll("a[href*='/page/']"))
            {
                var m = Regex.Match(a.GetAttribute("href") ?? "", @"/page/(\d+)/");
                if (m.Success && long.TryParse(m.Groups[1].Value, out var n) && n > max) max = n;
            }
        }

        return max;
    }

    /// <summary>
    /// 站点报告的总命中数：搜索结果页为 <c>SearchResultsPage.mainEntity.numberOfItems</c>，
    /// 列表页为根级 <c>ItemList.numberOfItems</c>。
    /// </summary>
    private static long ParseReportedTotal(IDocument doc)
    {
        foreach (var el in EnumerateJsonLdObjects(doc))
        {
            if (el.TryGetProperty("mainEntity", out var me) && me.ValueKind == JsonValueKind.Object &&
                TryLong(me, "numberOfItems", out var inner))
            {
                return inner;
            }
            if (el.TryGetProperty("@type", out var t) && t.ValueKind == JsonValueKind.String &&
                t.GetString() is "ItemList" or "CollectionPage" &&
                TryLong(el, "numberOfItems", out var direct))
            {
                return direct;
            }
        }
        return 0;
    }

    // ====================== 图集详情 ======================

    public static GalleryParseResult ParseGallery(string html)
    {
        var doc = Parser.ParseDocument(html);
        var gallery = FindJsonLd(doc, "ImageGallery");

        var urls = new List<string>();
        var keywords = new List<string>();
        long imageCount = 0, currentPage = 1, totalPages = 1;
        var description = "";
        var jsonName = "";

        if (gallery is { } gal)
        {
            description = Str(gal, "description");
            jsonName = Str(gal, "name");
            TryLong(gal, "numberOfItems", out imageCount);

            if (gal.TryGetProperty("keywords", out var kw) && kw.ValueKind == JsonValueKind.String)
            {
                keywords = kw.GetString()!
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(s => s.Length > 0)
                    .ToList();
            }

            if (gal.TryGetProperty("pagination", out var pg) && pg.ValueKind == JsonValueKind.Object)
            {
                if (TryLong(pg, "currentPage", out var cp)) currentPage = cp;
                if (TryLong(pg, "totalPages", out var tp)) totalPages = tp;
            }

            if (gal.TryGetProperty("itemListElement", out var ile) && ile.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in ile.EnumerateArray())
                {
                    if (it.ValueKind != JsonValueKind.Object) continue;
                    var u = Str(it, "contentUrl");
                    if (u.Length > 0) urls.Add(Absolutize(u));
                }
            }
        }

        // 分类：BreadcrumbList 里 /type/{n}/ 一项同时给出 id 与中文名
        var (categoryId, categoryName) = ParseBreadcrumbCategory(doc);

        // 标题：页面 h1 最干净（JSON-LD 的 name 形如 "… - 分类 - 爱妹子"）
        var title = Clean(doc.QuerySelector("h1")?.TextContent);
        if (title.Length == 0) title = StripJsonNameSuffix(jsonName, categoryName);

        // 封面：og:image 即首图；缺失时退回首图
        var cover = Absolutize(doc.QuerySelector("meta[property='og:image']")?.GetAttribute("content"));
        if (cover.Length == 0 && urls.Count > 0) cover = urls[0];

        return new GalleryParseResult
        {
            Title = title,
            Description = description,
            Keywords = keywords,
            ImageCount = imageCount,
            CurrentPage = currentPage,
            TotalPages = totalPages < 1 ? 1 : totalPages,
            ImageUrls = urls,
            CoverUrl = cover,
            CategoryId = categoryId,
        };
    }

    /// <summary>从 BreadcrumbList 取分类：<c>/type/{n}/</c> 项的数字 id 与展示名。</summary>
    private static (string Id, string Name) ParseBreadcrumbCategory(IDocument doc)
    {
        var bc = FindJsonLd(doc, "BreadcrumbList");
        if (bc is not { } list || !list.TryGetProperty("itemListElement", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return ("", "");
        }

        foreach (var it in items.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) continue;
            var m = Regex.Match(Str(it, "item"), @"/type/(\d+)/");
            if (m.Success) return (m.Groups[1].Value, Clean(Str(it, "name")));
        }
        return ("", "");
    }

    // ====================== 维度索引 ======================

    /// <summary>分类导航：从任意分类页侧的 <c>a[href^='/type/']</c> 枚举全部分类（站内无 /type/ 索引页）。</summary>
    public static List<ComicCategory> ParseCategoryNav(string html)
    {
        var doc = Parser.ParseDocument(html);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var a in doc.QuerySelectorAll("a[href^='/type/']"))
        {
            var m = Regex.Match(a.GetAttribute("href") ?? "", @"^/type/(\d+)/");
            if (!m.Success) continue;
            var name = Clean(a.TextContent);
            if (name.Length == 0) continue;
            seen.TryAdd(m.Groups[1].Value, name);
        }

        return seen.Select(kv => new ComicCategory { Id = kv.Key, Name = kv.Value }).ToList();
    }

    /// <summary>
    /// 维度索引页（标签 / 专题 / 实体）：读 JSON-LD <c>CollectionPage.mainEntity</c> 的
    /// <c>numberOfItems</c> 与 <c>itemListElement[]</c>。
    /// </summary>
    public static IndexParseResult ParseTaxonomyIndex(string html)
    {
        var doc = Parser.ParseDocument(html);
        var items = new List<TaxonomyItem>();
        long total = 0;

        foreach (var el in EnumerateJsonLdObjects(doc))
        {
            if (!el.TryGetProperty("mainEntity", out var me) || me.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (!me.TryGetProperty("itemListElement", out var ile) || ile.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            TryLong(me, "numberOfItems", out total);

            foreach (var it in ile.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;
                var name = Clean(Str(it, "name"));
                var id = ToTaxonomyId(Str(it, "url"));
                if (name.Length > 0 && id.Length > 0)
                {
                    items.Add(new TaxonomyItem(id, name));
                }
            }
            break;
        }

        return new IndexParseResult { Total = total, Items = items };
    }

    /// <summary>
    /// 把索引页里的绝对 URL 归一化为"浏览标识"：
    /// <c>/tag/1503/</c> → <c>1503</c>；<c>/topic/xxx/</c> → <c>xxx</c>；
    /// <c>/entity/creator/djawa/</c> → <c>creator/djawa</c>（实体需保留子路径以区分 creator/performer）。
    /// </summary>
    internal static string ToTaxonomyId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var path = Regex.Replace(url.Trim(), @"^https?://[^/]+", "");
        var m = Regex.Match(path, @"^/(?:tag|type|topic)/([^/]+)/?$");
        if (m.Success) return m.Groups[1].Value;
        var e = Regex.Match(path, @"^/entity/([^/]+/[^/]+)/?$");
        return e.Success ? e.Groups[1].Value : "";
    }

    // ====================== JSON-LD 工具 ======================

    /// <summary>枚举页面中全部 ld+json 对象（已 Clone，可安全脱离 JsonDocument 生命周期）。</summary>
    private static IEnumerable<JsonElement> EnumerateJsonLdObjects(IDocument doc)
    {
        foreach (var s in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            var text = s.TextContent;
            if (string.IsNullOrWhiteSpace(text)) continue;

            JsonDocument d;
            try { d = JsonDocument.Parse(text); }
            catch (JsonException) { continue; }

            using (d)
            {
                if (d.RootElement.ValueKind == JsonValueKind.Object)
                {
                    yield return d.RootElement.Clone();
                }
            }
        }
    }

    /// <summary>取 <c>@type</c> 匹配的第一个 ld+json 对象。</summary>
    private static JsonElement? FindJsonLd(IDocument doc, string type)
    {
        foreach (var el in EnumerateJsonLdObjects(doc))
        {
            if (el.TryGetProperty("@type", out var t) && t.ValueKind == JsonValueKind.String &&
                string.Equals(t.GetString(), type, StringComparison.Ordinal))
            {
                return el;
            }
        }
        return null;
    }

    private static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static bool TryLong(JsonElement obj, string name, out long value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var v)) return false;
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                return v.TryGetInt64(out value);
            case JsonValueKind.String:
                return long.TryParse(v.GetString(), out value);
            default:
                return false;
        }
    }

    // ====================== 文本工具 ======================

    /// <summary>相对 URL 补全为绝对地址。</summary>
    internal static string Absolutize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var u = url.Trim();
        if (u.StartsWith("//", StringComparison.Ordinal)) return "https:" + u;
        if (u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return u;
        return LoveCutesConstants.BaseUrl + (u.StartsWith('/') ? u : "/" + u);
    }

    /// <summary>站点名，用于剥离 JSON-LD name 的尾部后缀。</summary>
    private const string SiteName = "爱妹子";

    /// <summary>
    /// 去掉 JSON-LD <c>name</c> 尾部的 <c>" - {分类} - {站名}"</c> 后缀，只留图集标题。
    /// 仅作 h1 缺失时的兜底；分类名来自面包屑，按实际值精确裁剪，不做模糊截断。
    /// </summary>
    internal static string StripJsonNameSuffix(string name, string categoryName)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = Clean(name);

        var siteSuffix = " - " + SiteName;
        if (s.EndsWith(siteSuffix, StringComparison.Ordinal))
        {
            s = s[..^siteSuffix.Length].Trim();
        }

        var categorySuffix = " - " + categoryName;
        if (categoryName.Length > 0 && s.EndsWith(categorySuffix, StringComparison.Ordinal))
        {
            s = s[..^categorySuffix.Length].Trim();
        }

        return s;
    }

    /// <summary>压缩空白并去首尾空格。</summary>
    private static string Clean(string? text) =>
        string.IsNullOrEmpty(text) ? "" : Regex.Replace(text, @"\s+", " ").Trim();
}
