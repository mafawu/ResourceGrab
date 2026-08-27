using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// M8: FreeJavBT — 磁力链接 + 中字标记
// 状态: 待实现
// ---------------------------------------------------------------------------

public sealed class FreejavbtSource : IVideoScrapeSource
{
    public string Id => "freejavbt";
    public string DisplayName => "FreeJavBT";
    public IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Uncensored, VideoContentKind.Fc2, VideoContentKind.Chinese, VideoContentKind.Western]);

    public async ValueTask<VideoSourceFetchResult> FetchAsync(
        VideoSourceRequest request, CancellationToken ct)
    {
        await Task.CompletedTask;
        return VideoSourceFetchResult.Failure(Id, VideoSourceOutcome.Disabled, "Not yet implemented");
    }
}
