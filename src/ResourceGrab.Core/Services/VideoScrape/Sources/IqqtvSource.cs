using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: IQQTV — 中文字幕站（多语言模板站）。
// 参考 amane（github.com/sqzw-x/amane）crawlers/sites/iqqtv.py 的同源设计重写。
// 搜索: GET {base}{语言前缀}/vsearch?keyword={番号}，模板站卡片列表；
//       request.Language 非空时拼成 /{lang}/ 路径前缀（zh-CN → /zh_cn/），空则走站点默认。
// 详情: 卡片 href 指向的详情页 —— h4.title 标题、.video-cover 封面、<p><strong>字段名</strong> 值</p> 信息行。
// 中字标记: 标题或 tag 含"中文字幕" → HasChineseSubtitle。
// 置信度低：卡片/详情选择器为模板站通用猜测，需联网冒烟核实。
// ---------------------------------------------------------------------------

public sealed class IqqtvSource : HtmlScrapeSourceBase
{
    private const string BaseUrl = "https://iqqtv.com";

    public IqqtvSource(IResilientFetcher fetcher, ILogger? logger = null)
        : base(fetcher, logger)
    {
    }

    public override string Id => "iqqtv";
    public override string DisplayName => "IQQTV";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Chinese]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        // 多语言路径前缀：zh-CN → /zh_cn/；Language 为空时用根路径（站点默认语言）
        var prefix = string.IsNullOrWhiteSpace(request.Language)
            ? "/"
            : "/" + request.Language.Trim().ToLowerInvariant().Replace('-', '_') + "/";

        // ── 搜索 ──
        var searchUrl = $"{BaseUrl}{prefix}vsearch?keyword={Uri.EscapeDataString(request.Number)}";
        var searchHtml = await GetHtmlAsync(searchUrl, referer: BaseUrl + prefix, ct: ct);
        var searchDoc = new HtmlParser().ParseDocument(searchHtml);

        // 卡片列表：遍历结果区链接，取卡片文本（含 .title 子节点）做番号精确匹配
        // 置信度低：需联网冒烟核实卡片选择器
        string? detailPath = null;
        foreach (var card in searchDoc.QuerySelectorAll("a[href]"))
        {
            var text = card.QuerySelector(".title")?.TextContent
                       ?? card.QuerySelector("h4")?.TextContent
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

        var title = (root.QuerySelector("h4.title")?.TextContent
                     ?? root.QuerySelector("h1")?.TextContent
                     ?? "").Trim();

        var metadata = NewMetadata(Id, detailUrl);
        metadata.Title = title;

        // 封面：.video-cover img，模板站常用 data-src 懒加载
        var coverNode = root.QuerySelector(".video-cover img") ?? root.QuerySelector("img[data-src]");
        metadata.CoverUrl = ResolveUrl(BaseUrl, Attr(coverNode, "src") ?? Attr(coverNode, "data-src") ?? "");

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
                case "片長" or "片长" or "時長" or "时长" or "長度" or "长度" or "収録時間":
                    metadata.RuntimeMinutes = ParseMinutes(value);
                    break;
                case "演員" or "演员" or "出演者" or "女優":
                    AddAnchorValues(row, metadata.Actors);
                    break;
                case "類型" or "类型" or "標籤" or "标签" or "ジャンル":
                    AddAnchorValues(row, metadata.Tags);
                    if (metadata.Tags.Count == 0) AddSplitValues(value, metadata.Tags);
                    break;
                case "片商" or "製作" or "メーカー":
                    metadata.Studio = FirstAnchorOrText(row, value);
                    break;
                case "系列" or "シリーズ":
                    metadata.Series = FirstAnchorOrText(row, value);
                    break;
            }
        }

        // 中字标记：标题或 tag 含"中文字幕"
        metadata.HasChineseSubtitle =
            title.Contains("中文字幕", StringComparison.Ordinal)
            || metadata.Tags.Contains("中文字幕");

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
        // 去掉 label 节点文本后剩余部分
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

    private static void AddSplitValues(string value, List<string> target)
    {
        foreach (var part in Regex.Split(value, "[,，、/|]"))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0 && trimmed != "-" && !target.Contains(trimmed)) target.Add(trimmed);
        }
    }

    private static string FirstAnchorOrText(IElement row, string fallback)
    {
        var first = row.QuerySelector("a")?.TextContent.Trim();
        return first is { Length: > 0 } && first != "-" ? WebUtility.HtmlDecode(first) : fallback;
    }
}
