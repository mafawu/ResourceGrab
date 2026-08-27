using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// M9: FC2PPVDB — FC2 PPV 数据库
// 状态: 待实现
// ---------------------------------------------------------------------------

public sealed class Fc2ppvdbSource : IVideoScrapeSource
{
    public string Id => "fc2ppvdb";
    public string DisplayName => "FC2PPVDB";
    public IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Fc2]);

    public async ValueTask<VideoSourceFetchResult> FetchAsync(
        VideoSourceRequest request, CancellationToken ct)
    {
        await Task.CompletedTask;
        return VideoSourceFetchResult.Failure(Id, VideoSourceOutcome.Disabled, "Not yet implemented");
    }
}
