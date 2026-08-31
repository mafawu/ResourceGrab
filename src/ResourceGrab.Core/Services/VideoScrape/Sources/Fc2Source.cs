using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: FC2 — FC2 官方内容市场兜底源（adult.fc2.com，日文）。
// 参考 amane（github.com/sqzw-x/amane）crawlers/sites/fc2.py 的同源设计重写。
// 搜索: GET /search/?q={番号}（官方站按关键词检索，结果卡片文本做精确番号校验）。
// 详情: 结果链接（href 含 /content/）→ h1 标题、og:image 封面、标签链接。
// 置信度低：adult.fc2.com 搜索端点与卡片/详情选择器均为推测，需联网冒烟核实。
// ---------------------------------------------------------------------------

public sealed class Fc2Source : HtmlScrapeSourceBase
{
    private const string BaseUrl = "https://adult.fc2.com";

    public Fc2Source(IResilientFetcher fetcher, ILogger? logger = null)
        : base(fetcher, logger)
    {
    }

    public override string Id => "fc2";
    public override string DisplayName => "FC2";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Fc2]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        // ── 搜索 ──
        var searchUrl = $"{BaseUrl}/search/?q={Uri.EscapeDataString(request.Number)}";
        var searchHtml = await GetHtmlAsync(searchUrl, referer: BaseUrl + "/", ct: ct);
        var doc = new HtmlParser().ParseDocument(searchHtml);

        // 结果卡片：href 含 /content/ 的链接，卡片文本精确匹配番号
        // 置信度低：需联网冒烟核实搜索结果结构
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href") ?? "";
            if (!href.Contains("/content/", StringComparison.OrdinalIgnoreCase)) continue;
            var cardText = a.TextContent;
            if (!MatchesNumber(cardText, request.Number) && !MatchesFc2Id(cardText, request.Number)) continue;

            // ── 详情 ──
            var detailUrl = ResolveUrl(BaseUrl, href);
            var detailHtml = await GetHtmlAsync(detailUrl, referer: searchUrl, ct: ct);
            var root = new HtmlParser().ParseDocument(detailHtml);

            var metadata = NewMetadata(Id, detailUrl);
            metadata.Title = WebUtility.HtmlDecode(root.QuerySelector("h1")?.TextContent.Trim() ?? "");

            // 置信度低：封面优先 og:image，回退正文首图
            var cover = root.QuerySelector("meta[property='og:image']")?.GetAttribute("content")
                        ?? root.QuerySelector(".content img, article img, main img")?.GetAttribute("src");
            metadata.CoverUrl = cover is { Length: > 0 } ? ResolveUrl(BaseUrl, cover) : "";

            // 概要：og:description（置信度低：需联网核实）
            metadata.Description = WebUtility.HtmlDecode(
                root.QuerySelector("meta[property='og:description']")?.GetAttribute("content")?.Trim() ?? "");

            // 日期：优先 <time datetime>，回退 .date 容器（置信度低：需联网核实）
            var dateText = root.QuerySelector("time[datetime]")?.GetAttribute("datetime")
                           ?? root.QuerySelector(".date, .info time")?.TextContent;
            metadata.ReleaseDate = ParseDate(dateText);

            // 标签：href 含 /tag 或 /search/…category 的链接（置信度低：需联网核实路径段）
            foreach (var tagA in root.QuerySelectorAll("a[href*='tag']"))
            {
                var tag = WebUtility.HtmlDecode(tagA.TextContent.Trim());
                if (tag.Length > 0 && tag != "-" && !metadata.Tags.Contains(tag))
                    metadata.Tags.Add(tag);
            }

            return metadata.Title.Length == 0 && metadata.CoverUrl.Length == 0 ? null : metadata;
        }
        return null; // 搜索页无命中 → NoMatch
    }

    /// <summary>
    /// FC2 纯数字 ID 匹配：基类 MatchesNumber 的候选正则数字段上限 6 位，
    /// 而 FC2 番号常为 7 位，故补充 "FC2[-_ ]?数字" 形态的精确比对（前导零等价）。
    /// </summary>
    private static bool MatchesFc2Id(string text, string number)
    {
        var m = Regex.Match(number, @"(\d{6,})");
        if (!m.Success) return false;
        var id = m.Groups[1].Value.TrimStart('0');
        foreach (Match c in Regex.Matches(text, @"(?i)FC2[-_. ]?PPV?[-_. ]?0*(\d{6,})"))
            if (c.Groups[1].Value.TrimStart('0') == id) return true;
        return false;
    }
}
