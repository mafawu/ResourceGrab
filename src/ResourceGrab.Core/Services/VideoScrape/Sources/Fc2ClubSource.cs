using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: FC2 Club — FC2 备选站（fc2club.com）。
// 站点结构公开资料少，按"详情页直构 + 搜索页兜底"设计：
//   1) 直构 https://fc2club.com/html/FC2PPV-{id}.html（fc2club 历史详情 slug 形态之一，推测）
//   2) 404 时退回搜索页 /search/{番号}，结果链接 href 含 /html/，卡片文本精确匹配番号
// 解析要点: h1/.article-title 标题、og:image 封面、正文标签链接按 href 路径段识别。
// 置信度低：详情 slug、搜索端点与页面选择器均为推测，需联网冒烟核实。
// ---------------------------------------------------------------------------

public sealed class Fc2ClubSource : HtmlScrapeSourceBase
{
    private const string BaseUrl = "https://fc2club.com";

    public Fc2ClubSource(IResilientFetcher fetcher, ILogger? logger = null)
        : base(fetcher, logger)
    {
    }

    public override string Id => "fc2club";
    public override string DisplayName => "FC2 Club";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Fc2]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        // FC2 番号 → 纯数字条目 ID（"FC2-1234567" → "1234567"）
        var idMatch = Regex.Match(request.Number, @"(\d{6,})");
        if (!idMatch.Success) return null;

        // ── 1) 详情页直构（slug 为推测，置信度低）──
        var directUrl = $"{BaseUrl}/html/FC2PPV-{idMatch.Groups[1].Value}.html";
        string directHtml;
        try
        {
            directHtml = await GetHtmlAsync(directUrl, referer: BaseUrl + "/", ct: ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // 直构未命中 → 退回搜索页兜底
            Logger?.Warn($"[Fc2Club] 直构详情 404，退回搜索页: {directUrl}");
            return await FetchViaSearchAsync(request, ct);
        }
        return ParseDetail(directHtml, directUrl, request.Number);

        // ── 2) 搜索页兜底 ──
        async Task<VideoScrapeMetadata?> FetchViaSearchAsync(VideoSourceRequest req, CancellationToken token)
        {
            var searchUrl = $"{BaseUrl}/search/{Uri.EscapeDataString(req.Number)}";
            var searchHtml = await GetHtmlAsync(searchUrl, referer: BaseUrl + "/", ct: token);
            var doc = new HtmlParser().ParseDocument(searchHtml);

            // 结果链接：href 含 /html/ 的卡片，卡片文本精确匹配番号
            // 置信度低：需联网冒烟核实搜索结果结构
            foreach (var a in doc.QuerySelectorAll("a[href]"))
            {
                var href = a.GetAttribute("href") ?? "";
                if (!href.Contains("/html/", StringComparison.OrdinalIgnoreCase)) continue;
                var cardText = a.TextContent;
                if (MatchesNumber(cardText, req.Number) || MatchesFc2Id(cardText, req.Number))
                {
                    var detailUrl = ResolveUrl(BaseUrl, href);
                    var detailHtml = await GetHtmlAsync(detailUrl, referer: searchUrl, ct: token);
                    return ParseDetail(detailHtml, detailUrl, req.Number);
                }
            }
            return null; // 搜索页无命中 → NoMatch
        }
    }

    private VideoScrapeMetadata? ParseDetail(string html, string detailUrl, string number)
    {
        var root = new HtmlParser().ParseDocument(html);

        var metadata = NewMetadata(Id, detailUrl);

        // 置信度低：标题覆盖文章标题与普通 h1 两种形态
        var rawTitle = root.QuerySelector("h1.article-title")?.TextContent
                       ?? root.QuerySelector("h1")?.TextContent
                       ?? "";
        // 标题形如 "FC2-PPV-1234567 实际标题"，把番号前缀剥掉
        var title = Regex.Replace(rawTitle.Trim(), $@"^(FC2[-_. ]?PPV[-_. ]?0*\d+)\s*", "", RegexOptions.IgnoreCase).Trim();
        metadata.Title = WebUtility.HtmlDecode(title);

        // 置信度低：封面优先 og:image，回退正文首图
        var cover = root.QuerySelector("meta[property='og:image']")?.GetAttribute("content")
                    ?? root.QuerySelector(".article-content img, article img, .content img")?.GetAttribute("src");
        metadata.CoverUrl = cover is { Length: > 0 } ? ResolveUrl(BaseUrl, cover) : "";

        // 日期：优先 <time datetime>，回退 .date 容器（置信度低：需联网核实）
        var dateText = root.QuerySelector("time[datetime]")?.GetAttribute("datetime")
                       ?? root.QuerySelector(".date, .article-meta .date")?.TextContent;
        metadata.ReleaseDate = ParseDate(dateText);

        // 标签：正文里 href 含 /tag 或 /tags 的链接（置信度低：需联网核实）
        foreach (var a in root.QuerySelectorAll("a[href*='tag']"))
        {
            var tag = WebUtility.HtmlDecode(a.TextContent.Trim());
            if (tag.Length > 0 && tag != "-" && !metadata.Tags.Contains(tag))
                metadata.Tags.Add(tag);
        }

        return metadata.Title.Length == 0 && metadata.CoverUrl.Length == 0 ? null : metadata;
    }

    /// <summary>
    /// FC2 纯数字 ID 匹配：基类 MatchesNumber 的候选正则数字段上限 6 位，
    /// 而 FC2 番号常为 7 位，故补充 "FC2[-_ ]?PPV[-_ ]?数字" 形态的精确比对（前导零等价）。
    /// </summary>
    private static bool MatchesFc2Id(string text, string number)
    {
        var m = Regex.Match(number, @"(\d{6,})");
        if (!m.Success) return false;
        var id = m.Groups[1].Value.TrimStart('0');
        foreach (Match c in Regex.Matches(text, @"(?i)FC2[-_. ]?PPV[-_. ]?0*(\d{6,})"))
            if (c.Groups[1].Value.TrimStart('0') == id) return true;
        return false;
    }
}
