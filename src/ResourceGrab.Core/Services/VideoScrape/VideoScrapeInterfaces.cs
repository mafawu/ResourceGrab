using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M1: 基础来源接口 — 替代 IVideoScraper
// ---------------------------------------------------------------------------

public interface IVideoScrapeSource
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlySet<VideoContentKind> SupportedKinds { get; }
    ValueTask<VideoSourceFetchResult> FetchAsync(
        VideoSourceRequest request,
        CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------
// M1: 来源注册中心
// ---------------------------------------------------------------------------

public interface IVideoScrapeSourceRegistry
{
    IReadOnlyCollection<IVideoScrapeSource> Sources { get; }
    IVideoScrapeSource? Get(string sourceId);
    IReadOnlyList<IVideoScrapeSource> Resolve(IEnumerable<string> sourceIds);
}
