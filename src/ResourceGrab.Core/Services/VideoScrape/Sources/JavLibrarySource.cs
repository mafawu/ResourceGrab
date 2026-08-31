using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: JavLibrary — 有码主力源（评分、标签、演员）。
// 参考 amane（github.com/sqzw-x/amane）crawlers/sites/javlibrary.py 的同源设计重写。
// 搜索: GET {base}/cn/search.php?keyword={番号}&f=all，结果卡片 div.video 内含
//       div.id（番号）与 a[href=?v=xxxx]；详情: /cn/?v=xxxx 结构化表格 #video_info。
// 支持镜像域名轮换：搜索失败时按 VideoSourceConfig.MirrorUrls 里的基础地址重试。
// ---------------------------------------------------------------------------

public sealed class JavLibrarySource : HtmlScrapeSourceBase
{
    private readonly IReadOnlyList<string> _baseUrls;

    public JavLibrarySource(IResilientFetcher fetcher, ILogger? logger = null,
        IReadOnlyList<string>? baseUrls = null)
        : base(fetcher, logger)
    {
        // 基础地址约定：含语言路径段（如 /cn/），配置为空时用官方主站中文页
        _baseUrls = baseUrls is { Count: > 0 } ? baseUrls : ["https://www.javlibrary.com/cn"];
    }

    public override string Id => "javlibrary";
    public override string DisplayName => "JavLibrary";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>(
        [
            VideoContentKind.Censored, VideoContentKind.Amateur,
        ]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        var (prefix, digits) = SplitNumber(request.Number);
        if (digits.Length == 0) return null;

        // 依次尝试基础地址（首个失败换镜像）
        Exception? lastError = null;
        foreach (var baseUrl in _baseUrls)
        {
            try
            {
                return await FetchWithBaseUrlAsync(baseUrl.TrimEnd('/'), request, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                Logger?.Warn($"[JavLibrary] 基础地址失败，换下一个: {baseUrl} ({ex.Message})");
            }
        }
        throw lastError ?? new HttpRequestException("JavLibrary 所有基础地址均请求失败");
    }

    private async Task<VideoScrapeMetadata?> FetchWithBaseUrlAsync(string baseUrl, VideoSourceRequest request, CancellationToken ct)
    {
        // ── 搜索 ──
        var searchUrl = $"{baseUrl}/search.php?keyword={Uri.EscapeDataString(request.Number)}&f=all";
        var searchHtml = await GetHtmlAsync(searchUrl, referer: baseUrl + "/", ct: ct);
        var doc = new HtmlParser().ParseDocument(searchHtml);

        // 结果卡片：div.video > a[href=?v=xxxx]，卡片内 div.id 是番号 —— 用它做精确匹配
        string? detailPath = null;
        foreach (var card in doc.QuerySelectorAll("div.video a[href]"))
        {
            var cardId = card.QuerySelector("div.id")?.TextContent.Trim() ?? "";
            if (MatchesNumber(cardId, request.Number))
            {
                detailPath = card.GetAttribute("href");
                break;
            }
        }
        // 兜底：搜索页直接跳到了详情页（唯一命中时站点 302），标题区已有番号
        if (detailPath is null)
        {
            var directTitle = doc.QuerySelector("#video_title a")?.TextContent.Trim() ?? "";
            if (MatchesNumber(directTitle, request.Number) || MatchesNumber(doc.QuerySelector("#video_id td.text")?.TextContent ?? "", request.Number))
                detailPath = searchUrl;
        }
        if (detailPath is null) return null;

        // ── 详情 ──
        var detailUrl = ResolveUrl(baseUrl, detailPath);
        var html = await GetHtmlAsync(detailUrl, referer: baseUrl + "/", ct: ct);
        var root = new HtmlParser().ParseDocument(html);

        var rawTitle = root.QuerySelector("#video_title a")?.TextContent.Trim() ?? "";
        // 标题形如 "SNOS-001 实际标题"，把番号前缀剥掉
        var title = Regex.Replace(rawTitle, $"^{Regex.Escape(request.Number)}\\s*", "", RegexOptions.IgnoreCase).Trim();

        string Field(string id)
        {
            var node = root.QuerySelector($"#{id} td.text");
            return System.Net.WebUtility.HtmlDecode(node?.TextContent.Trim() ?? "");
        }

        var metadata = NewMetadata(Id, detailUrl);
        metadata.Title = title;
        metadata.ReleaseDate = ParseDate(Field("video_date"));
        metadata.RuntimeMinutes = ParseMinutes(root.QuerySelector("#video_length span.text")?.TextContent);
        metadata.Director = Field("video_director");
        metadata.Studio = Field("video_maker");
        metadata.Publisher = Field("video_label");

        foreach (var a in root.QuerySelectorAll("#video_genres td.text a"))
            metadata.Tags.Add(System.Net.WebUtility.HtmlDecode(a.TextContent.Trim()));
        foreach (var a in root.QuerySelectorAll("#video_cast td.text a"))
        {
            var actor = System.Net.WebUtility.HtmlDecode(a.TextContent.Trim());
            if (actor.Length > 0 && actor != "-") metadata.Actors.Add(actor);
        }

        // 封面：#video_jacket img（src 或 data-src）
        var jacket = root.QuerySelector("#video_jacket img");
        var cover = jacket?.GetAttribute("src") ?? jacket?.GetAttribute("data-src") ?? "";
        metadata.CoverUrl = cover.StartsWith("//") ? "https:" + cover : ResolveUrl(baseUrl, cover);

        // 评分：#video_review .score .value（5 分制），换算为 10 分制
        var scoreText = root.QuerySelector("#video_review .score .value")?.TextContent.Trim() ?? "";
        if (double.TryParse(scoreText, System.Globalization.CultureInfo.InvariantCulture, out var score) && score > 0)
        {
            metadata.Score = Math.Min(10, score * 2);
            var votes = root.QuerySelector("#video_review .score a")?.TextContent ?? "";
            var voteMatch = Regex.Match(votes, @"(\d+)");
            if (voteMatch.Success && int.TryParse(voteMatch.Groups[1].Value, out var voteCount))
                metadata.ScoreVotes = voteCount;
        }

        return title.Length == 0 && metadata.CoverUrl.Length == 0 ? null : metadata;
    }
}
