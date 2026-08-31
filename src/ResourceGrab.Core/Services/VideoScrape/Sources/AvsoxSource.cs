using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: Avsox — 无码/FC2 搜索站（JavLibrary 同族模板）。
// 参考 amane（github.com/sqzw-x/amane）crawlers/sites/avsox.py 的同源设计重写。
// 搜索: GET {base}/search/{番号}，bootstrap 卡片瀑布流（div.item 内 div.id 番号）。
// 详情: 卡片 href（./?v=xxxx 类）→ 结构近似 JavLibrary（#video_title / #video_jacket / #video_info）。
// 域名易失效：硬编码主站 + 镜像列表，搜索失败按序轮换（参考 JavLibrarySource 的 _baseUrls 模式）。
// 置信度低：搜索卡片选择器与详情字段需联网冒烟核实。
// ---------------------------------------------------------------------------

public sealed class AvsoxSource : HtmlScrapeSourceBase
{
    // 主站 + 备选镜像（配置层 MirrorUrls 语义的硬编码等价实现，按序轮换）
    private static readonly string[] BaseUrls =
    [
        "https://avsox.click",
        "https://avsox.com",
    ];

    public AvsoxSource(IResilientFetcher fetcher, ILogger? logger = null)
        : base(fetcher, logger)
    {
    }

    public override string Id => "avsox";
    public override string DisplayName => "Avsox";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>(
        [
            VideoContentKind.Uncensored, VideoContentKind.Fc2,
        ]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        // 依次尝试基础地址（首个失败换镜像）
        Exception? lastError = null;
        foreach (var baseUrl in BaseUrls)
        {
            try
            {
                return await FetchWithBaseUrlAsync(baseUrl.TrimEnd('/'), request, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                Logger?.Warn($"[Avsox] 基础地址失败，换下一个: {baseUrl} ({ex.Message})");
            }
        }
        throw lastError ?? new HttpRequestException("Avsox 所有基础地址均请求失败");
    }

    private async Task<VideoScrapeMetadata?> FetchWithBaseUrlAsync(string baseUrl, VideoSourceRequest request, CancellationToken ct)
    {
        // ── 搜索（路径式：/search/{番号}）──
        var searchUrl = $"{baseUrl}/search/{Uri.EscapeDataString(request.Number)}";
        var searchHtml = await GetHtmlAsync(searchUrl, referer: baseUrl + "/", ct: ct);
        var searchDoc = new HtmlParser().ParseDocument(searchHtml);

        // 结果卡片：瀑布流 div.item > a[href]，卡片内 div.id 是番号 —— 用它做精确匹配
        // 置信度低：需联网冒烟核实卡片结构
        string? detailPath = null;
        foreach (var card in searchDoc.QuerySelectorAll("div.item a[href]"))
        {
            var cardId = card.QuerySelector("div.id")?.TextContent.Trim()
                         ?? card.QuerySelector("div.title")?.TextContent.Trim()
                         ?? card.TextContent;
            if (MatchesNumber(cardId, request.Number))
            {
                detailPath = card.GetAttribute("href");
                break;
            }
        }
        if (detailPath is null) return null;

        // ── 详情（布局近似 JavLibrary）──
        var detailUrl = ResolveUrl(baseUrl, detailPath);
        var html = await GetHtmlAsync(detailUrl, referer: searchUrl, ct: ct);
        var root = new HtmlParser().ParseDocument(html);

        var rawTitle = root.QuerySelector("#video_title a")?.TextContent
                       ?? root.QuerySelector("#video_title")?.TextContent
                       ?? root.QuerySelector("h3")?.TextContent
                       ?? "";
        // 标题形如 "SNOS-001 实际标题"，把番号前缀剥掉
        var title = Regex.Replace(rawTitle.Trim(), $"^{Regex.Escape(request.Number)}\\s*", "", RegexOptions.IgnoreCase).Trim();

        string Field(string id)
        {
            var node = root.QuerySelector($"#{id} td.text");
            return WebUtility.HtmlDecode(node?.TextContent.Trim() ?? "");
        }

        var metadata = NewMetadata(Id, detailUrl);
        metadata.Title = title;
        metadata.ReleaseDate = ParseDate(Field("video_date"));
        metadata.RuntimeMinutes = ParseMinutes(root.QuerySelector("#video_length span.text")?.TextContent);
        metadata.Director = Field("video_director");
        metadata.Studio = Field("video_maker");
        metadata.Publisher = Field("video_label");

        foreach (var a in root.QuerySelectorAll("#video_genres td.text a"))
        {
            var tag = WebUtility.HtmlDecode(a.TextContent.Trim());
            if (tag.Length > 0 && tag != "-") metadata.Tags.Add(tag);
        }
        foreach (var a in root.QuerySelectorAll("#video_cast td.text a"))
        {
            var actor = WebUtility.HtmlDecode(a.TextContent.Trim());
            if (actor.Length > 0 && actor != "-") metadata.Actors.Add(actor);
        }

        // 封面：#video_jacket img（src 或 data-src）
        var jacket = root.QuerySelector("#video_jacket img") ?? root.QuerySelector(".photo-frame img");
        var cover = jacket?.GetAttribute("src");
        if (string.IsNullOrEmpty(cover)) cover = jacket?.GetAttribute("data-src");
        if (string.IsNullOrEmpty(cover)) cover = "";
        metadata.CoverUrl = cover.StartsWith("//") ? "https:" + cover : ResolveUrl(baseUrl, cover);

        return title.Length == 0 && metadata.CoverUrl.Length == 0 ? null : metadata;
    }
}
