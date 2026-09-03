using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Sources.VideoSources;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// MissAV 元数据源 — 包装在线源 MissAvSource（搜索 + 详情解析 + 镜像轮换 + curl
// 兜底均已就绪），把 OnlineVideoDetail 映射为 VideoScrapeMetadata 进图引擎。
// 分级依据（用户指定）：Tier 1，首批必含——访问快、免登录、全部内容类型覆盖，
// 中文标题与磁力/流地址是独有价值。
// ---------------------------------------------------------------------------

public sealed class MissAvScrapeSource : HtmlScrapeSourceBase
{
    private readonly MissAvSource _online;

    public MissAvScrapeSource(IResilientFetcher fetcher, MissAvSource online, ILogger? logger = null)
        : base(fetcher, logger)
    {
        _online = online;
    }

    public override string Id => "missav";
    public override string DisplayName => "MissAV";
    public override IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>(
        [
            VideoContentKind.Censored, VideoContentKind.Fc2, VideoContentKind.Uncensored,
            VideoContentKind.Chinese, VideoContentKind.Amateur, VideoContentKind.Western,
            VideoContentKind.Hentai,
        ]);

    protected override async Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct)
    {
        // 1) 搜索：按番号在 MissAV 站内搜索，卡片标题/Id 精确匹配
        var search = await _online.SearchAsync(request.Number, 1, ct);
        OnlineVideoSummary? hit = null;
        foreach (var item in search.Items)
        {
            if (MatchesNumber(item.Title, request.Number) || MatchesNumber(item.Id, request.Number))
            {
                hit = item;
                break;
            }
        }
        if (hit is null) return null;

        // 2) 详情：复用在线源的 packed-JS/JSON-LD/og 元信息解析
        var videoUrl = _online.GetDetailUrl(hit.Id);
        if (string.IsNullOrEmpty(videoUrl)) return null;
        var detail = await _online.GetDetailAsync(videoUrl, ct);
        if (detail is null || string.IsNullOrWhiteSpace(detail.Title)) return null;

        var metadata = NewMetadata(Id, detail.VideoUrl);
        metadata.Title = detail.Title;
        metadata.OriginalTitle = detail.OriginalTitle;
        metadata.Description = detail.Description;
        metadata.CoverUrl = detail.CoverUrl;
        metadata.Actors.AddRange(detail.Actors);
        metadata.Tags.AddRange(detail.Tags);
        metadata.Tags.AddRange(detail.Genres);
        metadata.ReleaseDate = ParseDate(detail.ReleaseDateText);
        metadata.RuntimeMinutes = DurationToMinutes(detail.DurationText);
        metadata.HasMagnet = detail.MagnetUri is not null;
        if (!string.IsNullOrEmpty(detail.StreamUrl))
            metadata.SourceUrls["missav-stream"] = detail.StreamUrl;
        return metadata;
    }

    /// <summary>"H:mm:ss"/"mm:ss" → 分钟数。</summary>
    private static int DurationToMinutes(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var parts = text.Split(':');
        if (parts.Length == 3 && int.TryParse(parts[0], out var hours) && int.TryParse(parts[1], out var mins))
            return hours * 60 + mins;
        if (parts.Length == 2 && int.TryParse(parts[0], out var minutes))
            return minutes;
        return 0;
    }
}
