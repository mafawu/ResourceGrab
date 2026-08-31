using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: Kin8（kin8tengoku，金8天国）— 无修正（Uncensored）源。
// 参考 amane（github.com/sqzw-x/amane）crawlers/sites/kin8.py 的同源设计重写。
// 搜索: GET {base}/search/?keyword={番号}；候选 = 所有指向 /detailpages/ 的链接，
//       链接及其所在卡片文本经 MatchesNumber 精确校验；无候选时兜底认为搜索页
//       直接渲染了详情内容（h1 含番号）—— 端点与结构置信度低：需联网冒烟核实。
// 详情: /detailpages/<id>.html，字段按日文标签（th/dt）扫描提取。
// ---------------------------------------------------------------------------

public sealed class Kin8Source : HtmlScrapeSourceBase
{
    private const string BaseUrl = "https://www.kin8tengoku.com";

    public Kin8Source(IResilientFetcher fetcher, ILogger? logger = null) : base(fetcher, logger) { }

    public override string Id => "kin8";
    public override string DisplayName => "Kin8Tengoku";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Uncensored]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        // ── 搜索 ──
        // 置信度低：kin8 搜索端点/卡片结构需联网冒烟核实；番号无法直构详情 URL，
        // 只能靠搜索（或搜索页直接呈现详情的兜底）。
        var searchUrl = $"{BaseUrl}/search/?keyword={Uri.EscapeDataString(request.Number)}";
        var searchHtml = await GetHtmlAsync(searchUrl, referer: BaseUrl + "/", ct: ct);
        var doc = new HtmlParser().ParseDocument(searchHtml);

        string? detailPath = null;
        foreach (var a in doc.QuerySelectorAll("a[href*='/detailpages/']")) // 置信度低：详情链接路径模式
        {
            var context = a.TextContent + " " + a.ParentElement?.TextContent;
            if (MatchesNumber(context, request.Number))
            {
                detailPath = a.GetAttribute("href");
                break;
            }
        }
        // 兜底：唯一命中时站点可能从搜索页直接渲染详情内容（无跳转）
        if (detailPath is null && MatchesNumber(doc.QuerySelector("h1")?.TextContent ?? "", request.Number))
            detailPath = searchUrl;
        if (detailPath is null) return null;

        // ── 详情 ──
        var detailUrl = ResolveUrl(BaseUrl, detailPath);
        var html = await GetHtmlAsync(detailUrl, referer: searchUrl, ct: ct);
        var root = new HtmlParser().ParseDocument(html);

        var title = (root.QuerySelector("h1")?.TextContent ?? root.QuerySelector("title")?.TextContent ?? "").Trim();
        // 置信度低：封面取详情区内首张 jpg 内容图
        var coverNode = root.QuerySelector("div.detail-info img[src]") ?? root.QuerySelector("img[src*='.jpg']");

        var metadata = NewMetadata(Id, detailUrl);
        metadata.Title = title;
        metadata.CoverUrl = ResolveUrl(BaseUrl, coverNode?.GetAttribute("src") ?? "");

        metadata.ReleaseDate = ParseDate(TextByLabel(root, "発売日", "配信開始日"));
        metadata.RuntimeMinutes = ParseMinutes(TextByLabel(root, "収録時間"));
        metadata.Director = WebUtility.HtmlDecode(TextByLabel(root, "監督") ?? "");

        var actorCell = ElementByLabel(root, "出演者", "出演", "女優");
        if (actorCell is not null) AddLinksOrSplit(actorCell, metadata.Actors);
        var tagCell = ElementByLabel(root, "ジャンル", "タグ", "カテゴリ");
        if (tagCell is not null) AddLinksOrSplit(tagCell, metadata.Tags);

        return title.Length == 0 && metadata.CoverUrl.Length == 0 ? null : metadata;
    }

    /// <summary>按日文标签扫描 th/dt 行，返回标签对应值单元格文本。</summary>
    private static string? TextByLabel(IDocument root, params string[] labels)
        => ElementByLabel(root, labels)?.TextContent.Trim();

    private static IElement? ElementByLabel(IDocument root, params string[] labels)
    {
        foreach (var th in root.QuerySelectorAll("th,dt"))
        {
            var label = th.TextContent.Trim().TrimEnd('：', ':').Trim();
            if (!labels.Contains(label)) continue;
            var value = th.NextElementSibling;
            if (value is not null) return value;
        }
        return null;
    }

    /// <summary>值单元格优先取链接文本，无链接时按分隔符拆分纯文本。</summary>
    private static void AddLinksOrSplit(IElement cell, List<string> target)
    {
        var links = cell.QuerySelectorAll("a");
        if (links.Any())
        {
            foreach (var a in links)
            {
                var text = WebUtility.HtmlDecode(a.TextContent.Trim());
                if (text.Length > 0 && text != "-") target.Add(text);
            }
            return;
        }
        foreach (var part in WebUtility.HtmlDecode(cell.TextContent).Split('　', ' ', '、', ',', '/'))
        {
            var text = part.Trim();
            if (text.Length > 0 && text != "-") target.Add(text);
        }
    }
}
