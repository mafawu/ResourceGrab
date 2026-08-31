using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: Jav321 — 有码源（标题/封面/演员/日期）。
// 参考 amane（github.com/sqzw-x/amane）crawlers/sites/jav321.py 的同源设计重写。
// 搜索: POST 表单 uname={番号} 到 /search（低置信度：站点未提供公开 API，amane 同款表单）。
//       单命中时站点 302 直接返回详情页 body；多命中返回卡片列表。
// 详情: /video/{id}，bootstrap 两栏布局 —— 左栏 col-md-3 封面（大图在 /package/ 路径下），
//       右栏 col-md-9 h3 标题 + <p><b>字段名:</b> 值</p> 信息行。
// 置信度低：需联网冒烟核实 POST 端点与详情字段标签。
// ---------------------------------------------------------------------------

public sealed class Jav321Source : HtmlScrapeSourceBase
{
    private const string BaseUrl = "https://www.jav321.com";

    public Jav321Source(IResilientFetcher fetcher, ILogger? logger = null)
        : base(fetcher, logger)
    {
    }

    public override string Id => "jav321";
    public override string DisplayName => "Jav321";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Censored]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        // ── 搜索：POST uname 表单（application/x-www-form-urlencoded）──
        var searchUrl = BaseUrl + "/search";
        var searchHtml = await PostAsync(
            searchUrl,
            "application/x-www-form-urlencoded",
            "uname=" + Uri.EscapeDataString(request.Number),
            referer: BaseUrl + "/",
            ct: ct);
        var searchDoc = new HtmlParser().ParseDocument(searchHtml);

        // 单命中时站点直接把详情页渲染出来 —— 用右栏标题区判定，省一次请求
        var directTitle = searchDoc.QuerySelector("div.col-md-9 h3")?.TextContent.Trim() ?? "";
        if (MatchesNumber(directTitle, request.Number))
            return ParseDetail(searchDoc, searchUrl);

        // 多命中：卡片列表（javlibrary 同模板 div.video > a，卡片内 div.id 是番号）
        // 置信度低：需联网冒烟核实搜索结果卡片结构
        string? detailPath = null;
        foreach (var card in searchDoc.QuerySelectorAll("div.video a[href]"))
        {
            var cardId = card.QuerySelector("div.id")?.TextContent.Trim()
                         ?? card.QuerySelector("div.title")?.TextContent.Trim()
                         ?? card.TextContent;
            if (MatchesNumber(cardId, request.Number))
            {
                detailPath = card.GetAttribute("href");
                break;
            }
        }
        if (detailPath is null) return null;

        // ── 详情 ──
        var detailUrl = ResolveUrl(BaseUrl, detailPath);
        var html = await GetHtmlAsync(detailUrl, referer: searchUrl, ct: ct);
        return ParseDetail(new HtmlParser().ParseDocument(html), detailUrl);
    }

    private VideoScrapeMetadata? ParseDetail(IDocument root, string detailUrl)
    {
        var title = root.QuerySelector("div.col-md-9 h3")?.TextContent.Trim()
                    ?? root.QuerySelector("h3")?.TextContent.Trim()
                    ?? "";

        var metadata = NewMetadata(Id, detailUrl);
        metadata.Title = title;

        // 封面：左栏 col-md-3 img；大图在 /package/ 路径下（amane 要点）
        var coverNode = root.QuerySelector("div.col-md-3 img") ?? root.QuerySelector("div.photo-frame img");
        metadata.CoverUrl = coverNode?.GetAttribute("src") is { Length: > 0 } src
            ? src.StartsWith("//") ? "https:" + src : ResolveUrl(BaseUrl, src)
            : "";

        // 信息行：col-md-9 下的 <p>，<b> 是字段名
        foreach (var row in root.QuerySelectorAll("div.col-md-9 p"))
        {
            var label = (row.QuerySelector("b")?.TextContent ?? "").Trim().TrimEnd(':', '：');
            if (label.Length == 0) continue;
            var value = RowValue(row, label);

            switch (label)
            {
                case "発売日" or "發售日" or "发售日":
                    metadata.ReleaseDate ??= ParseDate(value);
                    break;
                case "収録時間" or "收录时间" or "長度" or "长度":
                    metadata.RuntimeMinutes = ParseMinutes(value);
                    break;
                case "出演者" or "女優":
                    AddAnchorValues(row, metadata.Actors);
                    break;
                case "メーカー" or "片商":
                    metadata.Studio = FirstAnchorOrText(row, value);
                    break;
                case "レーベル" or "發行商":
                    metadata.Publisher = FirstAnchorOrText(row, value);
                    break;
                case "監督" or "导演":
                    metadata.Director = FirstAnchorOrText(row, value);
                    break;
                case "シリーズ" or "系列":
                    metadata.Series = FirstAnchorOrText(row, value);
                    break;
                case "ジャンル" or "類別" or "标签":
                    AddAnchorValues(row, metadata.Tags);
                    break;
            }
        }

        return title.Length == 0 && metadata.CoverUrl.Length == 0 ? null : metadata;
    }

    /// <summary>信息行全文去掉 "<label>:" 前缀后的值文本（值可能含锚点，全文拼接即可）。</summary>
    private static string RowValue(IElement row, string label)
    {
        var full = WebUtility.HtmlDecode(row.TextContent.Trim());
        var withColon = label + ":";
        if (full.StartsWith(withColon, StringComparison.Ordinal)) return full[withColon.Length..].Trim();
        return full.StartsWith(label, StringComparison.Ordinal) ? full[label.Length..].TrimStart(' ', ':', '：') : full;
    }

    private static void AddAnchorValues(IElement row, List<string> target)
    {
        foreach (var a in row.QuerySelectorAll("a"))
        {
            var value = WebUtility.HtmlDecode(a.TextContent.Trim());
            if (value.Length > 0 && value != "-" && !target.Contains(value)) target.Add(value);
        }
    }

    private static string FirstAnchorOrText(IElement row, string fallback)
    {
        var first = row.QuerySelector("a")?.TextContent.Trim();
        return first is { Length: > 0 } && first != "-" ? WebUtility.HtmlDecode(first) : fallback;
    }
}
