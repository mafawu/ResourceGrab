using System.Collections.Concurrent;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M1: 默认来源注册中心 — 通过 DI 注册 IVideoScrapeSource，由此统一查找
// ---------------------------------------------------------------------------

public sealed class VideoScrapeSourceRegistry : IVideoScrapeSourceRegistry
{
    private readonly Dictionary<string, IVideoScrapeSource> _byId;

    public VideoScrapeSourceRegistry(IEnumerable<IVideoScrapeSource> sources)
    {
        _byId = sources.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IVideoScrapeSource> Sources => _byId.Values;

    public IVideoScrapeSource? Get(string sourceId) =>
        _byId.TryGetValue(sourceId, out var source) ? source : null;

    public IReadOnlyList<IVideoScrapeSource> Resolve(IEnumerable<string> sourceIds) =>
        sourceIds
            .Select(id => Get(id))
            .Where(s => s is not null)
            .Cast<IVideoScrapeSource>()
            .ToList();
}
