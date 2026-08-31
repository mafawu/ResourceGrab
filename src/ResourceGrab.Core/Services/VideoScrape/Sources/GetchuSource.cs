using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: Getchu — 美少女游戏/动画（Hentai）源。
// 参考 amane（github.com/sqzw-x/amane）crawlers/sites/getchu.py 的同源设计重写。
// 年龄墙: amane 方案携带 cookie adult_check_flag=1；基类 GetHtmlAsync 不支持附加
//         Cookie 头，因此在搜索/详情前先 GET 一次站点首页，依赖 MultiTierFetcher
//         的 Cookie 罐把首页下发的年龄确认 cookie 带到后续请求（预热失败不阻断）。
// 编码: getchu 为 Shift_JIS 站点，正文解码依赖抓取栈按 charset 处理（需联网核实）。
// 搜索: GET {base}/php/nsearch.phtml?search_str={番号}&genre=anime（置信度低：需联网
//       冒烟核实）；候选 = 所有指向 /soft.phtml?id= 的链接，文本 MatchesNumber 校验；
//       详情: /soft.phtml?id=xxxx，字段按标签单元格扫描（発売日/ブランド/ジャンル等）。
// ---------------------------------------------------------------------------

public sealed class GetchuSource : HtmlScrapeSourceBase
{
    private const string BaseUrl = "http://www.getchu.com";

    public GetchuSource(IResilientFetcher fetcher, ILogger? logger = null) : base(fetcher, logger) { }

    public override string Id => "getchu";
    public override string DisplayName => "Getchu";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Hentai]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        await WarmUpAsync(ct);

        // ── 搜索 ──
        var searchUrl =
            $"{BaseUrl}/php/nsearch.phtml?search_str={Uri.EscapeDataString(request.Number)}&genre=anime"; // 置信度低：需联网冒烟核实
        var searchHtml = await GetHtmlAsync(searchUrl, referer: BaseUrl + "/", ct: ct);
        var doc = new HtmlParser().ParseDocument(searchHtml);

        string? detailPath = null;
        foreach (var a in doc.QuerySelectorAll("a[href*='soft.phtml?id=']")) // 置信度低：详情链接路径模式
        {
            var context = a.TextContent + " " + a.ParentElement?.TextContent;
            if (MatchesNumber(context, request.Number))
            {
                detailPath = a.GetAttribute("href");
                break;
            }
        }
        // 兜底：唯一命中时站点可能从搜索页直接渲染了详情内容（标题区含番号）
        if (detailPath is null && MatchesNumber(doc.QuerySelector("h1")?.TextContent ?? "", request.Number))
            detailPath = searchUrl;
        if (detailPath is null) return null;

        // ── 详情 ──
        var detailUrl = ResolveUrl(BaseUrl, detailPath);
        var html = await GetHtmlAsync(detailUrl, referer: searchUrl, ct: ct);
        var root = new HtmlParser().ParseDocument(html);

        var title = (root.QuerySelector(".wrap_title a")?.TextContent   // 置信度低：需联网冒烟核实
                     ?? root.QuerySelector("h1")?.TextContent ?? "").Trim();
        // 置信度低：主封面图通常含 "_main"（如 a_main.jpg），回退 og:image
        var cover =
            root.QuerySelector("img[src*='_main']")?.GetAttribute("src")
            ?? root.QuerySelector("meta[property='og:image']")?.GetAttribute("content")
            ?? "";

        var metadata = NewMetadata(Id, detailUrl);
        metadata.Title = title;
        metadata.CoverUrl = ResolveUrl(BaseUrl, cover);
        metadata.ReleaseDate = ParseDate(TextByLabel(root, "発売日"));
        metadata.Studio = WebUtility.HtmlDecode(TextByLabel(root, "ブランド", "メーカー") ?? "");
        metadata.Series = WebUtility.HtmlDecode(TextByLabel(root, "シリーズ") ?? "");

        var actorCell = ElementByLabel(root, "出演", "声優", "キャスト", "出演者");
        if (actorCell is not null) AddLinksOrSplit(actorCell, metadata.Actors);
        var tagCell = ElementByLabel(root, "ジャンル", "カテゴリ");
        if (tagCell is not null) AddLinksOrSplit(tagCell, metadata.Tags);

        return title.Length == 0 && metadata.CoverUrl.Length == 0 ? null : metadata;
    }

    /// <summary>
    /// 年龄墙预热：GetHtmlAsync 无法附带 Cookie 头，先请求一次首页，
    /// 由抓取栈的 Cookie 罐持有年龄确认 cookie（amane: adult_check_flag=1）供后续使用。
    /// </summary>
    private async Task WarmUpAsync(CancellationToken ct)
    {
        try
        {
            await GetHtmlAsync(BaseUrl + "/", ct: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // 首页预热失败仅影响年龄 cookie 下发，不阻断主流程
        }
    }

    /// <summary>按标签扫描 th/dt/td 行（getchu 的信息表用 td 做标签列），返回值单元格文本。</summary>
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
