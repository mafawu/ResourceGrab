using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

public sealed class GetchuSource : IVideoScrapeSource
{
    public string Id => "getchu";
    public string DisplayName => "Getchu";
    public IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Hentai]);

    public async ValueTask<VideoSourceFetchResult> FetchAsync(
        VideoSourceRequest request, CancellationToken ct)
    {
        await Task.CompletedTask;
        return VideoSourceFetchResult.Failure(Id, VideoSourceOutcome.Disabled, "Not yet implemented");
    }
}
