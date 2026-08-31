using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: FC2PPVDB — FC2 PPV 数据库站（fc2ppvdb.com）。
// 参考 amane（github.com/sqzw-x/amane）crawlers/sites/fc2ppvdb.py 的同源设计重写。
// FC2 番号 "FC2-1234567" 的纯数字部分即条目 ID，详情页直构 /articles/{id}，无需搜索步。
// 解析要点: h1 标题、og:image 封面、女优/标签链接按 href 路径段（/actress/、/tag）识别。
// 置信度低：详情页选择器（日期/女优/标签的容器结构）为推测，需联网冒烟核实。
// ---------------------------------------------------------------------------

public sealed class Fc2ppvdbSource : HtmlScrapeSourceBase
{
    private const string BaseUrl = "https://fc2ppvdb.com";

    public Fc2ppvdbSource(IResilientFetcher fetcher, ILogger? logger = null)
        : base(fetcher, logger)
    {
    }

    public override string Id => "fc2ppvdb";
    public override string DisplayName => "FC2PPVDB";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Fc2]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        // FC2 番号 → 纯数字条目 ID（"FC2-1234567" / "FC2PPV-1234567" → "1234567"）
        var idMatch = Regex.Match(request.Number, @"(\d{6,})");
        if (!idMatch.Success) return null;

        // 详情页直构（无搜索步）；404 由基类翻译为 HttpError —— 直构 URL 未命中时
        // 站点通常回 404/软 404 页，软 404（200 + 错误标题）在下方按 NoMatch 处理。
        var detailUrl = $"{BaseUrl}/articles/{idMatch.Groups[1].Value}";
        var html = await GetHtmlAsync(detailUrl, referer: BaseUrl + "/", ct: ct);
        var root = new HtmlParser().ParseDocument(html);

        var metadata = NewMetadata(Id, detailUrl);

        // 标题：页面主 h1
        metadata.Title = WebUtility.HtmlDecode(root.QuerySelector("h1")?.TextContent.Trim() ?? "");

        // 置信度低：封面优先 og:image（最稳），回退正文首图
        var cover = root.QuerySelector("meta[property='og:image']")?.GetAttribute("content")
                    ?? root.QuerySelector("img.cover, .article-image img, article img")?.GetAttribute("src");
        metadata.CoverUrl = cover is { Length: > 0 } ? ResolveUrl(BaseUrl, cover) : "";

        // 日期：优先 <time datetime>，回退日期容器文本
        // 置信度低：需联网核实日期容器
        var dateText = root.QuerySelector("time[datetime]")?.GetAttribute("datetime")
                       ?? root.QuerySelector(".date, .release-date, .article-meta time")?.TextContent;
        metadata.ReleaseDate = ParseDate(dateText);

        // 女优 / 标签：按链接 href 路径段识别（置信度低：需联网核实路径段）
        foreach (var a in root.QuerySelectorAll("a[href*='/actress/'], a[href*='/model']"))
        {
            var actor = WebUtility.HtmlDecode(a.TextContent.Trim());
            if (actor.Length > 0 && actor != "-" && !metadata.Actors.Contains(actor))
                metadata.Actors.Add(actor);
        }
        foreach (var a in root.QuerySelectorAll("a[href*='/tag']"))
        {
            var tag = WebUtility.HtmlDecode(a.TextContent.Trim());
            if (tag.Length > 0 && tag != "-" && !metadata.Tags.Contains(tag))
                metadata.Tags.Add(tag);
        }

        // 软 404 判定：站点对不存在的条目可能返回 200 错误页（无 og:image + "404" 标题）
        if (metadata.Title.Length == 0 && metadata.CoverUrl.Length == 0) return null;
        if (Regex.IsMatch(metadata.Title, @"404|not found|お探しのページ|ページが見つかり", RegexOptions.IgnoreCase))
            return null;

        return metadata;
    }
}
