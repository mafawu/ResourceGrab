using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// M8: Avsox — 无码/FC2 搜索
// 状态: 待实现
// ---------------------------------------------------------------------------

public sealed class AvsoxSource : IVideoScrapeSource
{
    public string Id => "avsox";
    public string DisplayName => "Avsox";
    public IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Uncensored, VideoContentKind.Fc2]);

    public async ValueTask<VideoSourceFetchResult> FetchAsync(
        VideoSourceRequest request, CancellationToken ct)
    {
        await Task.CompletedTask;
        return VideoSourceFetchResult.Failure(Id, VideoSourceOutcome.Disabled, "Not yet implemented");
    }
}
