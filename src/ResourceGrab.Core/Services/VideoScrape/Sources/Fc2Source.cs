using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// M9: FC2 通用
// 状态: 待实现
// ---------------------------------------------------------------------------

public sealed class Fc2Source : IVideoScrapeSource
{
    public string Id => "fc2";
    public string DisplayName => "FC2";
    public IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Fc2]);

    public async ValueTask<VideoSourceFetchResult> FetchAsync(
        VideoSourceRequest request, CancellationToken ct)
    {
        await Task.CompletedTask;
        return VideoSourceFetchResult.Failure(Id, VideoSourceOutcome.Disabled, "Not yet implemented");
    }
}
