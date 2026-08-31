using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape;
using Xunit;

namespace ResourceGrab.Core.Tests.Services.VideoScrape;

// M1: 来源注册中心测试

public class RegistryTests
{
    private static IVideoScrapeSourceRegistry CreateRegistry(params IVideoScrapeSource[] sources) =>
        new VideoScrapeSourceRegistry(sources);

    private sealed class FakeSource(string id, params VideoContentKind[] kinds) : IVideoScrapeSource
    {
        public string Id => id;
        public string DisplayName => id.ToUpperInvariant();
        public IReadOnlySet<VideoContentKind> SupportedKinds => new HashSet<VideoContentKind>(kinds);
        public ValueTask<VideoSourceFetchResult> FetchAsync(VideoSourceRequest request, CancellationToken ct) =>
            new(VideoSourceFetchResult.NoMatch(Id));
    }

    [Fact]
    public void Get_ReturnsSource_WhenRegistered()
    {
        var src = new FakeSource("test");
        var registry = CreateRegistry(src);

        Assert.Same(src, registry.Get("test"));
    }

    [Fact]
    public void Get_ReturnsNull_WhenNotRegistered()
    {
        var registry = CreateRegistry();

        Assert.Null(registry.Get("nonexistent"));
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        var src = new FakeSource("JavBus");
        var registry = CreateRegistry(src);

        Assert.Same(src, registry.Get("javbus"));
        Assert.Same(src, registry.Get("JAVBUS"));
    }

    [Fact]
    public void Resolve_ReturnsMultiple_InOrder()
    {
        var a = new FakeSource("a");
        var b = new FakeSource("b");
        var c = new FakeSource("c");
        var registry = CreateRegistry(a, b, c);

        var result = registry.Resolve(["c", "a"]);

        Assert.Equal(2, result.Count);
        Assert.Equal("c", result[0].Id);
        Assert.Equal("a", result[1].Id);
    }

    [Fact]
    public void Resolve_SkipsUnregisteredSources()
    {
        var a = new FakeSource("a");
        var registry = CreateRegistry(a);

        var result = registry.Resolve(["a", "missing", "also-missing"]);

        Assert.Single(result);
        Assert.Equal("a", result[0].Id);
    }

    [Fact]
    public void Sources_ReturnsAllRegistered()
    {
        var a = new FakeSource("a");
        var b = new FakeSource("b");
        var registry = CreateRegistry(a, b);

        Assert.Equal(2, registry.Sources.Count);
    }
}
