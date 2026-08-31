using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: Faleno — 厂牌官网（faleno.jp）有码源。布局与 Dahlia 同系，但按约定独立实现。
// 参考 amane（github.com/sqzw-x/amane）crawlers/sites/faleno.py 的同源设计重写。
// 搜索: GET {base}/?s={番号}（站点为 WordPress 架构的推测用法，置信度低：需联网
//       冒烟核实）；遍历文章卡片（article 块内的标题链接），番号 MatchesNumber 校验。
// 详情: 标题 h1/entry-title；封面 og:image 回退正文首图；发售日优先 time[datetime]；
//       演员/系列按标签单元格扫描（値单元格无链接时拆分纯文本）；
//       标签取 a[rel='tag']（WordPress 标签链接）。
// ---------------------------------------------------------------------------

public sealed class FalenoSource : HtmlScrapeSourceBase
{
    private const string BaseUrl = "https://faleno.jp";

    public FalenoSource(IResilientFetcher fetcher, ILogger? logger = null) : base(fetcher, logger) { }

    public override string Id => "faleno";
    public override string DisplayName => "Faleno";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Censored]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        // ── 搜索 ──
        var searchUrl = $"{BaseUrl}/?s={Uri.EscapeDataString(request.Number)}"; // 置信度低：需联网冒烟核实
        var searchHtml = await GetHtmlAsync(searchUrl, referer: BaseUrl + "/", ct: ct);
        var doc = new HtmlParser().ParseDocument(searchHtml);

        string? detailUrl = null;
        // 逐个卡片检查：article 块内的标题链接优先，整卡文本参与番号校验
        foreach (var card in doc.QuerySelectorAll("article, div.work, div.product")) // 置信度低：卡片容器选择器
        {
            var link = card.QuerySelector("h2 a[href]") ?? card.QuerySelector("h3 a[href]")
                ?? card.QuerySelector("a[href]");
            if (link is null) continue;
            if (MatchesNumber(card.TextContent, request.Number))
            {
                detailUrl = ResolveUrl(BaseUrl, link.GetAttribute("href") ?? "");
                break;
            }
        }
        // 兜底：无卡片容器时直接看页内链接
        if (detailUrl is null)
        {
            foreach (var a in doc.QuerySelectorAll("main a[href], a[href*='/works/'], a[href*='/product/']"))
            {
                if (!MatchesNumber(a.TextContent, request.Number)) continue;
                detailUrl = ResolveUrl(BaseUrl, a.GetAttribute("href") ?? "");
                break;
            }
        }
        if (detailUrl is null) return null;

        // ── 详情 ──
        var html = await GetHtmlAsync(detailUrl, referer: searchUrl, ct: ct);
        var root = new HtmlParser().ParseDocument(html);

        var title = (root.QuerySelector("h1.entry-title")?.TextContent
                     ?? root.QuerySelector("h1")?.TextContent ?? "").Trim();
        // 置信度低：封面优先 og:image，回退正文首图
        var cover =
            root.QuerySelector("meta[property='og:image']")?.GetAttribute("content")
            ?? root.QuerySelector(".entry-content img[src]")?.GetAttribute("src")
            ?? "";

        var metadata = NewMetadata(Id, detailUrl);
        metadata.Title = title;
        metadata.CoverUrl = ResolveUrl(BaseUrl, cover);

        // 发售日：优先 time[datetime] 属性，回退発売日标签扫描
        var timeAttr = root.QuerySelector("time[datetime]")?.GetAttribute("datetime");
        metadata.ReleaseDate = ParseDate(timeAttr) ?? ParseDate(TextByLabel(root, "発売日"));
        metadata.Series = WebUtility.HtmlDecode(TextByLabel(root, "シリーズ") ?? "");
        metadata.Studio = WebUtility.HtmlDecode(TextByLabel(root, "メーカー") ?? "");

        var actorCell = ElementByLabel(root, "出演", "出演者", "女優");
        if (actorCell is not null) AddLinksOrSplit(actorCell, metadata.Actors);
        foreach (var tag in root.QuerySelectorAll("a[rel='tag']")) // WordPress 标签链接
        {
            var tagText = WebUtility.HtmlDecode(tag.TextContent.Trim());
            if (tagText.Length > 0 && !metadata.Tags.Contains(tagText)) metadata.Tags.Add(tagText);
        }

        return title.Length == 0 && metadata.CoverUrl.Length == 0 ? null : metadata;
    }

    /// <summary>按标签扫描 th/dt/td 行，返回标签对应值单元格文本。</summary>
    private static string? TextByLabel(IDocument root, params string[] labels)
        => ElementByLabel(root, labels)?.TextContent.Trim();

    private static IElement? ElementByLabel(IDocument root, params string[] labels)
    {
        foreach (var cell in root.QuerySelectorAll("th,dt,td"))
        {
            var label = cell.TextContent.Trim().TrimEnd('：', ':').Trim();
            // 标签单元格应为短文本，避免把内容列误当标签
            if (label.Length == 0 || label.Length > 12 || !labels.Contains(label)) continue;
            var value = cell.NextElementSibling;
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
