using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: Prestige — 有码厂牌官方站源（プレステージ）。
// 参考 amane（github.com/sqzw-x/amane）crawlers/sites/prestige.py 的同源设计重写。
// 注意: 站点为日本 IP 限定，需在 sourceConfigs.prestige.proxyOverride 配置日本出口代理，
//       否则请求会被站点直接拒绝（403/地域页）。
// 搜索: GET {base}/search?keyword={番号}；详情页 URL 形如 /goods/{番号}/，
//       候选 = 所有指向 /goods/ 的链接，文本 MatchesNumber 精确校验
//       （置信度低：搜索结果页 HTML 结构需联网冒烟核实）。
// 详情: 标题 og:title、封面 og:image（回退 h1 / 首图）；日期/演员/厂牌按 dt/th/td
//       日文标签扫描（発売日、出演女優、メーカー）。
// ---------------------------------------------------------------------------

public sealed class PrestigeSource : HtmlScrapeSourceBase
{
    private const string BaseUrl = "https://www.prestige-av.com";

    public PrestigeSource(IResilientFetcher fetcher, ILogger? logger = null) : base(fetcher, logger) { }

    public override string Id => "prestige";
    public override string DisplayName => "Prestige";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Censored]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        var (prefix, digits) = SplitNumber(request.Number);
        if (digits.Length == 0) return null;

        // ── 搜索 ──
        var searchUrl = $"{BaseUrl}/search?keyword={Uri.EscapeDataString(request.Number)}"; // 置信度低：需联网冒烟核实
        var searchHtml = await GetHtmlAsync(searchUrl, referer: BaseUrl + "/", ct: ct);
        var doc = new HtmlParser().ParseDocument(searchHtml);

        // 置信度低：结果链接按 /goods/{番号}/ 形态假设（Prestige 官方商品页路径）
        string? detailPath = null;
        foreach (var a in doc.QuerySelectorAll("a[href*='/goods/']"))
        {
            var context = a.TextContent + " " + a.GetAttribute("href");
            if (MatchesNumber(context, request.Number))
            {
                detailPath = a.GetAttribute("href");
                break;
            }
        }
        // 兜底：唯一命中时站点可能直接渲染了详情内容（og:title 已含番号）
        if (detailPath is null && MatchesNumber(doc.QuerySelector("meta[property='og:title']")?.GetAttribute("content") ?? "", request.Number))
            detailPath = searchUrl;
        if (detailPath is null) return null;

        // ── 详情 ──
        var detailUrl = ResolveUrl(BaseUrl, detailPath);
        var html = await GetHtmlAsync(detailUrl, referer: searchUrl, ct: ct);
        var root = new HtmlParser().ParseDocument(html);

        var rawTitle = (root.QuerySelector("meta[property='og:title']")?.GetAttribute("content")
                        ?? root.QuerySelector("h1")?.TextContent ?? "").Trim();
        // 标题形如 "PRED-300 实际标题"，剥掉番号前缀
        var title = Regex.Replace(rawTitle, $"^{Regex.Escape(request.Number)}\\s*", "", RegexOptions.IgnoreCase).Trim();

        var cover = root.QuerySelector("meta[property='og:image']")?.GetAttribute("content")
                    ?? root.QuerySelector("img[src*='.jpg']")?.GetAttribute("src") ?? "";

        var metadata = NewMetadata(Id, detailUrl);
        metadata.Title = title;
        metadata.CoverUrl = cover.StartsWith("//") ? "https:" + cover : ResolveUrl(BaseUrl, cover);
        metadata.ReleaseDate = ParseDate(TextByLabel(root, "発売日", "商品発売日", "配信開始日"));
        metadata.Studio = WebUtility.HtmlDecode(TextByLabel(root, "メーカー") ?? "");
        metadata.Publisher = WebUtility.HtmlDecode(TextByLabel(root, "レーベル") ?? "");
        metadata.Series = WebUtility.HtmlDecode(TextByLabel(root, "シリーズ") ?? "");

        if (ElementByLabel(root, "出演", "出演女優", "女優") is { } actorCell)
            AddLinksOrSplit(actorCell, metadata.Actors);
        if (ElementByLabel(root, "ジャンル") is { } tagCell)
            AddLinksOrSplit(tagCell, metadata.Tags);

        return title.Length == 0 && metadata.CoverUrl.Length == 0 ? null : metadata;
    }

    /// <summary>按标签扫描 th/dt/td 行，返回值单元格（官方站规格表 dt/dd 与 table 布局均覆盖）。</summary>
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
