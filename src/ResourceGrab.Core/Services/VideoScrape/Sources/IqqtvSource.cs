using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// M8: IQQTV — 中文站点（zh-CN / zh-TW 多语言，中字标记）
// 状态: 待实现
// ---------------------------------------------------------------------------

public sealed class IqqtvSource : IVideoScrapeSource
{
    public string Id => "iqqtv";
    public string DisplayName => "IQQTV";
    public IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Chinese, VideoContentKind.Uncensored]);

    public async ValueTask<VideoSourceFetchResult> FetchAsync(
        VideoSourceRequest request, CancellationToken ct)
    {
        // TODO: 实现 IQQTV 抓取逻辑
        await Task.CompletedTask;
        return VideoSourceFetchResult.Failure(Id, VideoSourceOutcome.Disabled, "Not yet implemented");
    }
}
