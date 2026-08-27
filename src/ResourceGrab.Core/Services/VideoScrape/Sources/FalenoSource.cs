using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

public sealed class FalenoSource : IVideoScrapeSource
{
    public string Id => "faleno";
    public string DisplayName => "Faleno";
    public IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Censored]);

    public async ValueTask<VideoSourceFetchResult> FetchAsync(
        VideoSourceRequest request, CancellationToken ct)
    {
        await Task.CompletedTask;
        return VideoSourceFetchResult.Failure(Id, VideoSourceOutcome.Disabled, "Not yet implemented");
    }
}
