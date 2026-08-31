using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Services.VideoScrape;
using Xunit;

namespace ResourceGrab.Core.Tests.Services.VideoScrape;

// M2: 配置扩展测试

public class ConfigExtensionTests
{
    [Theory]
    [InlineData(VideoNumberKind.Normal, VideoContentKind.Censored)]
    [InlineData(VideoNumberKind.Fc2, VideoContentKind.Fc2)]
    [InlineData(VideoNumberKind.Uncensored, VideoContentKind.Uncensored)]
    [InlineData(VideoNumberKind.Unknown, VideoContentKind.Unknown)]
    public void ToContentKind_MapsCorrectly(VideoNumberKind input, VideoContentKind expected)
    {
        Assert.Equal(expected, input.ToContentKind());
    }

    [Fact]
    public void GetSourcesForKind_ReturnsRoute()
    {
        var config = new VideoScrapeAdvancedSettings();

        var sources = config.GetSourcesForKind(VideoContentKind.Censored);

        Assert.NotEmpty(sources);
        Assert.Contains("dmm", sources);
        Assert.Contains("javdb", sources);
    }

    [Fact]
    public void GetSourcesForKind_ReturnsEmpty_ForUnknown()
    {
        var config = new VideoScrapeAdvancedSettings();

        var sources = config.GetSourcesForKind(VideoContentKind.Unknown);

        Assert.Empty(sources);
    }

    [Fact]
    public void GetFieldPriority_ReturnsDefault_WhenNoOverride()
    {
        var config = new VideoScrapeAdvancedSettings();

        var priority = config.GetFieldPriority("coverUrl", VideoContentKind.Censored);

        Assert.Contains("dmm", priority);
        Assert.Contains("javbus", priority);
    }

    [Fact]
    public void GetFieldPriority_ReturnsOverride_WhenKindMatches()
    {
        var config = new VideoScrapeAdvancedSettings();

        var priority = config.GetFieldPriority("title", VideoContentKind.Chinese);

        Assert.Contains("iqqtv", priority);
        Assert.Contains("airav", priority);
    }

    [Fact]
    public void IsSourceEnabled_ReturnsTrue_ForDefaultEnabled()
    {
        var config = new VideoScrapeAdvancedSettings();

        Assert.True(config.IsSourceEnabled("javbus"));
        Assert.True(config.IsSourceEnabled("javdb"));
    }

    [Fact]
    public void IsSourceEnabled_ReturnsFalse_ForDefaultDisabled()
    {
        var config = new VideoScrapeAdvancedSettings();

        // 阶段2 后 dmm 已实现并默认启用；仍默认禁用的是未实现/需用户配置的源
        Assert.False(config.IsSourceEnabled("official"));
        Assert.False(config.IsSourceEnabled("r18dev"));
        Assert.False(config.IsSourceEnabled("theporndb"));
    }
}
