using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: FreeJavBT — 磁力链接 + 中字标记源（无码/FC2/中字/西洋通吃）。
// 参考 amane（github.com/sqzw-x/amane）crawlers/sites/freejavbt.py 的同源设计重写。
// 搜索: GET /search?q={番号}，卡片 .card（a 指向 /watch/{slug}）。
// 详情: h5.card-title 标题、img.card-img-top 封面、<p><strong>字段名</strong> 值</p> 信息行、
//       a.badge 标签；磁力链接用正则 magnet:?xt=urn:btih:... 从 HTML 直接提取（HasMagnet）。
// 中字标记: 详情页含"中文字幕"（标题/标签/正文任一）→ HasChineseSubtitle。
// 置信度低：卡片/详情选择器为模板站通用猜测，需联网冒烟核实。
// ---------------------------------------------------------------------------

public sealed class FreejavbtSource : HtmlScrapeSourceBase
{
    private const string BaseUrl = "https://freejavbt.com";
    private static readonly Regex MagnetRegex = new(
        @"magnet:\?xt=urn:btih:[0-9A-Za-z]{8,}",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    public FreejavbtSource(IResilientFetcher fetcher, ILogger? logger = null)
        : base(fetcher, logger)
    {
    }

    public override string Id => "freejavbt";
    public override string DisplayName => "FreeJavBT";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>(
        [
            VideoContentKind.Uncensored, VideoContentKind.Fc2,
            VideoContentKind.Chinese, VideoContentKind.Western,
        ]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        // ── 搜索 ──
        var searchUrl = $"{BaseUrl}/search?q={Uri.EscapeDataString(request.Number)}";
        var searchHtml = await GetHtmlAsync(searchUrl, referer: BaseUrl + "/", ct: ct);
        var searchDoc = new HtmlParser().ParseDocument(searchHtml);

        // 结果卡片：.card 内 a[href]，标题在 .card-title（或卡片文本）—— 用它做番号精确匹配
        // 置信度低：需联网冒烟核实卡片结构
        string? detailPath = null;
        foreach (var card in searchDoc.QuerySelectorAll(".card a[href]"))
        {
            var text = card.QuerySelector(".card-title")?.TextContent
                       ?? card.QuerySelector("h5")?.TextContent
                       ?? card.TextContent;
            if (MatchesNumber(text, request.Number))
            {
                detailPath = card.GetAttribute("href");
                break;
            }
        }
        if (detailPath is null) return null;

        // ── 详情 ──
        var detailUrl = ResolveUrl(BaseUrl, detailPath);
        var html = await GetHtmlAsync(detailUrl, referer: searchUrl, ct: ct);
        var root = new HtmlParser().ParseDocument(html);

        var title = (root.QuerySelector("h5.card-title")?.TextContent
                     ?? root.QuerySelector("h1")?.TextContent
                     ?? "").Trim();

        var metadata = NewMetadata(Id, detailUrl);
        metadata.Title = title;

        // 封面：img.card-img-top（src 或 data-src）
        var coverNode = root.QuerySelector("img.card-img-top") ?? root.QuerySelector(".video-cover img");
        metadata.CoverUrl = ResolveUrl(BaseUrl, Attr(coverNode, "src") ?? Attr(coverNode, "data-src") ?? "");

        // 标签：a.badge
        foreach (var a in root.QuerySelectorAll("a.badge"))
        {
            var tag = WebUtility.HtmlDecode(a.TextContent.Trim());
            if (tag.Length > 0 && tag != "-" && !metadata.Tags.Contains(tag)) metadata.Tags.Add(tag);
        }

        // 信息行：<p> 内 <strong>/<span.header>/<b> 做字段名
        foreach (var row in root.QuerySelectorAll("p"))
        {
            var labelNode = row.QuerySelector("strong") ?? row.QuerySelector("span.header") ?? row.QuerySelector("b");
            var label = (labelNode?.TextContent ?? "").Trim().TrimEnd(':', '：');
            if (label.Length == 0) continue;
            var value = RowValue(row, labelNode);

            switch (label)
            {
                case "發行日期" or "发行日期" or "日期" or "発売日":
                    metadata.ReleaseDate ??= ParseDate(value);
                    break;
                case "片長" or "片长" or "時長" or "时长" or "長度" or "长度":
                    metadata.RuntimeMinutes = ParseMinutes(value);
                    break;
                case "演員" or "演员" or "出演者" or "女優":
                    AddAnchorValues(row, metadata.Actors);
                    break;
                case "片商" or "製作" or "メーカー":
                    metadata.Studio = FirstAnchorOrText(row, value);
                    break;
            }
        }

        // 磁力链接：正则直接从 HTML 提取（详情页有磁力即 HasMagnet=true）
        metadata.HasMagnet = MagnetRegex.IsMatch(html);

        // 中字标记：详情页标题/标签/正文含"中文字幕"
        metadata.HasChineseSubtitle = html.Contains("中文字幕", StringComparison.Ordinal);

        return title.Length == 0 && metadata.CoverUrl.Length == 0 ? null : metadata;
    }

    /// <summary>取元素属性值，空串视为缺失（模板站懒加载 img 常写 src="" data-src=真实地址）。</summary>
    private static string? Attr(IElement? node, string name)
    {
        var value = node?.GetAttribute(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string RowValue(IElement row, IElement? labelNode)
    {
        if (labelNode is not null)
        {
            var remaining = row.TextContent;
            var idx = remaining.IndexOf(labelNode.TextContent, StringComparison.Ordinal);
            if (idx >= 0) return WebUtility.HtmlDecode(remaining[(idx + labelNode.TextContent.Length)..].Trim());
        }
        return WebUtility.HtmlDecode(row.TextContent.Trim());
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
