using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: MGStage — 官方素人站（www.mgstage.com，日文）。
// 参考 amane（github.com/sqzw-x/amane）crawlers/sites/mgstage.py 的同源设计重写。
// 搜索: GET /search/product/?keyword={番号}；结果链接 /product/detail/{id}/。
// 详情: .detail_data 表格按日文表头解析（品番/発売日/収録時間/出演/シリーズ/メーカー/レーベル/ジャンル）。
// 年龄确认需 cookie adc=1：基类 GetHtmlAsync 不透传 Cookie 头，依赖抓取栈的 Cookie 罐
// （对应 VideoSourceConfig.Cookie 配置 "adc=1"）；Cookie 罐是否覆盖该源需联网核实，
// 未生效时搜索会被重定向到年龄确认页（无商品链接 → NoMatch）。
// 置信度中：搜索与详情选择器基于 mgstage 已知布局，仍需联网冒烟核实。
// ---------------------------------------------------------------------------

public sealed class MgstageSource : HtmlScrapeSourceBase
{
    private const string BaseUrl = "https://www.mgstage.com";

    public MgstageSource(IResilientFetcher fetcher, ILogger? logger = null)
        : base(fetcher, logger)
    {
    }

    public override string Id => "mgstage";
    public override string DisplayName => "MGStage";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Amateur]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        // ── 搜索 ──
        var searchUrl = $"{BaseUrl}/search/product/?keyword={Uri.EscapeDataString(request.Number)}";
        var searchHtml = await GetHtmlAsync(searchUrl, referer: BaseUrl + "/", ct: ct);
        var doc = new HtmlParser().ParseDocument(searchHtml);

        // 结果卡片：href 含 /product/detail/ 的链接，卡片文本精确匹配番号
        // 唯一命中时站点可能直接 302 到详情页 —— 详情页内的自链接/相关链接同样能匹配上
        string? detailUrl = null;
        foreach (var a in doc.QuerySelectorAll("a[href*='/product/detail/']"))
        {
            if (MatchesNumber(a.TextContent, request.Number))
            {
                detailUrl = ResolveUrl(BaseUrl, a.GetAttribute("href") ?? "");
                break;
            }
        }
        if (detailUrl is null) return null; // 搜索页无命中 → NoMatch

        // ── 详情 ──
        var html = await GetHtmlAsync(detailUrl, referer: searchUrl, ct: ct);
        var root = new HtmlParser().ParseDocument(html);

        var metadata = NewMetadata(Id, detailUrl);

        // 标题：h1.tag（置信度中：需联网核实）
        var rawTitle = root.QuerySelector("h1.tag")?.TextContent
                       ?? root.QuerySelector("h1")?.TextContent
                       ?? "";
        // 标题可能以番号开头（"SIRO-5000 实际标题"），把番号前缀剥掉
        var title = Regex.Replace(rawTitle.Trim(), $"^{Regex.Escape(request.Number)}\\s*", "", RegexOptions.IgnoreCase).Trim();
        metadata.Title = WebUtility.HtmlDecode(title);

        // 封面：#CenterPhotoImg 主图，回退 og:image（置信度中：需联网核实）
        var cover = root.QuerySelector("#CenterPhotoImg")?.GetAttribute("src")
                    ?? root.QuerySelector("meta[property='og:image']")?.GetAttribute("content");
        metadata.CoverUrl = cover is { Length: > 0 } ? ResolveUrl(BaseUrl, cover) : "";

        // .detail_data 表格按日文表头解析（置信度中：表头文案需联网核实）
        foreach (var row in root.QuerySelectorAll(".detail_data tr"))
        {
            var header = row.QuerySelector("th")?.TextContent.Trim().TrimEnd('：', ':') ?? "";
            var cell = row.QuerySelector("td");
            if (cell is null) continue;
            var cellText = System.Net.WebUtility.HtmlDecode(cell.TextContent.Trim());
            switch (header)
            {
                case "品番":
                    break; // 番号已知，无需回填
                case "発売日" or "配信開始日" or "配信開始":
                    metadata.ReleaseDate ??= ParseDate(cellText);
                    break;
                case "収録時間":
                    metadata.RuntimeMinutes = ParseMinutes(cellText);
                    break;
                case "出演" or "出演者":
                    foreach (var actorA in cell.QuerySelectorAll("a"))
                    {
                        var actor = System.Net.WebUtility.HtmlDecode(actorA.TextContent.Trim());
                        if (actor.Length > 0 && actor != "-" && !metadata.Actors.Contains(actor))
                            metadata.Actors.Add(actor);
                    }
                    break;
                case "シリーズ":
                    metadata.Series = cellText == "-" ? "" : cellText;
                    break;
                case "メーカー":
                    metadata.Studio = cellText == "-" ? "" : cellText;
                    break;
                case "レーベル":
                    metadata.Publisher = cellText == "-" ? "" : cellText;
                    break;
                case "ジャンル":
                    foreach (var tagA in cell.QuerySelectorAll("a"))
                    {
                        var tag = System.Net.WebUtility.HtmlDecode(tagA.TextContent.Trim());
                        if (tag.Length > 0 && tag != "-" && !metadata.Tags.Contains(tag))
                            metadata.Tags.Add(tag);
                    }
                    break;
            }
        }

        // 样例图：#sample-photo 区块（置信度低：需联网核实）
        foreach (var img in root.QuerySelectorAll("#sample-photo img, .sample_image img"))
        {
            var src = img.GetAttribute("src");
            if (!string.IsNullOrEmpty(src)) metadata.PreviewImageUrls.Add(ResolveUrl(BaseUrl, src));
        }

        return metadata.Title.Length == 0 && metadata.CoverUrl.Length == 0 ? null : metadata;
    }
}
