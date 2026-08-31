using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape;
using Xunit;

namespace ResourceGrab.Core.Tests.Services.VideoScrape;

// M2: 配置校验测试

public class ConfigValidatorTests
{
    private static IVideoScrapeSourceRegistry CreateRegistry(params string[] ids)
    {
        var sources = ids.Select(id => new FakeSourceForValidator(id)).Cast<IVideoScrapeSource>().ToList();
        return new VideoScrapeSourceRegistry(sources);
    }

    private sealed class FakeSourceForValidator(string id) : IVideoScrapeSource
    {
        public string Id => id;
        public string DisplayName => id;
        public IReadOnlySet<VideoContentKind> SupportedKinds => new HashSet<VideoContentKind>([VideoContentKind.Censored]);
        public ValueTask<VideoSourceFetchResult> FetchAsync(VideoSourceRequest request, CancellationToken ct) =>
            new(VideoSourceFetchResult.NoMatch(Id));
    }

    [Fact]
    public void Validate_ReturnsOk_WhenAllSourcesRegistered()
    {
        var config = new VideoScrapeAdvancedSettings
        {
            ContentRoutes =
            [
                new() { Kind = VideoContentKind.Censored, Sources = ["javbus", "javdb"] }
            ]
        };
        var registry = CreateRegistry("javbus", "javdb", "airav");

        var result = VideoScrapeConfigValidator.Validate(config, registry);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WarnsAboutUnregisteredSources()
    {
        var config = new VideoScrapeAdvancedSettings
        {
            ContentRoutes =
            [
                new() { Kind = VideoContentKind.Censored, Sources = ["javbus", "nonexistent"] }
            ]
        };
        var registry = CreateRegistry("javbus");

        var result = VideoScrapeConfigValidator.Validate(config, registry);

        Assert.True(result.IsValid); // 只警告不报错
        Assert.Contains(result.Warnings, w => w.Contains("nonexistent"));
    }

    [Fact]
    public void Validate_WarnsAboutDuplicateSourcesInRoute()
    {
        var config = new VideoScrapeAdvancedSettings
        {
            ContentRoutes =
            [
                new() { Kind = VideoContentKind.Censored, Sources = ["javbus", "javbus"] }
            ]
        };
        var registry = CreateRegistry("javbus");

        var result = VideoScrapeConfigValidator.Validate(config, registry);

        Assert.Contains(result.Warnings, w => w.Contains("重复"));
    }
}
