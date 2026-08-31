using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape;
using Xunit;

namespace ResourceGrab.Core.Tests.Services.VideoScrape;

// M7: 抓取图构建器和调度器测试

public class FetchGraphTests
{
    [Fact]
    public void Build_GeneratesFieldChains()
    {
        var config = new VideoScrapeAdvancedSettings();
        var nodes = new List<VideoFetchNode>
        {
            new() { SourceId = "javbus" },
            new() { SourceId = "javdb" },
            new() { SourceId = "airav" },
        };

        var chains = VideoFetchGraphBuilder.Build(config, VideoContentKind.Censored, nodes);

        Assert.NotEmpty(chains);
        Assert.Contains("title", chains.Keys);
        Assert.Contains("coverUrl", chains.Keys);
    }

    [Fact]
    public void Build_TitleChain_FollowsPriority()
    {
        var config = new VideoScrapeAdvancedSettings();
        var nodes = new List<VideoFetchNode>
        {
            new() { SourceId = "dmm" },
            new() { SourceId = "javdb" },
            new() { SourceId = "javbus" },
        };

        var chains = VideoFetchGraphBuilder.Build(config, VideoContentKind.Censored, nodes);

        // title 优先级 censored: ["dmm", "javdb"], 然后补充其余节点
        var titleChain = chains["title"];
        Assert.Equal("dmm", titleChain[0].SourceId);
        Assert.Equal("javdb", titleChain[1].SourceId);
    }

    [Fact]
    public void Build_ChineseKind_UsesChineseTitleOverride()
    {
        var config = new VideoScrapeAdvancedSettings();
        var nodes = new List<VideoFetchNode>
        {
            new() { SourceId = "iqqtv" },
            new() { SourceId = "airav" },
            new() { SourceId = "javdb" },
        };

        var chains = VideoFetchGraphBuilder.Build(config, VideoContentKind.Chinese, nodes);

        // title 优先级 chinese: ["iqqtv", "airav", "javdb"]
        var titleChain = chains["title"];
        Assert.Equal("iqqtv", titleChain[0].SourceId);
        Assert.Equal("airav", titleChain[1].SourceId);
    }

    [Fact]
    public void Build_SkipsUnregisteredNodes()
    {
        var config = new VideoScrapeAdvancedSettings();
        var nodes = new List<VideoFetchNode>
        {
            new() { SourceId = "javbus" },
            // dmm 未注册
        };

        var chains = VideoFetchGraphBuilder.Build(config, VideoContentKind.Censored, nodes);

        var titleChain = chains["title"];
        // dmm 不在 nodes 中，所以 titleChain 只有 javbus
        Assert.DoesNotContain(titleChain, n => n.SourceId == "dmm");
    }

    [Fact]
    public void VideoFetchNode_Equality_IsCaseInsensitive()
    {
        var a = new VideoFetchNode { SourceId = "JavBus" };
        var b = new VideoFetchNode { SourceId = "javbus" };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void VideoFetchNode_WithLanguage_DiffersFromNull()
    {
        var a = new VideoFetchNode { SourceId = "iqqtv" };
        var b = new VideoFetchNode { SourceId = "iqqtv", Language = "zh-CN" };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Scheduler_UsesCache_WhenAvailable()
    {
        var config = new VideoScrapeAdvancedSettings();
        var tempDir = Path.Combine(Path.GetTempPath(), $"sched_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var cache = new DiskJsonSnapshotCache(tempDir);
            var meta = new VideoScrapeMetadata { Source = "javbus", Title = "Cached" };
            await cache.SetAsync(new VideoSourceSnapshot
            {
                Number = "SSIS-405", SourceId = "javbus", SchemaVersion = "v1", Metadata = meta
            });

            var registry = new VideoScrapeSourceRegistry([]);
            var scheduler = new VideoFetchGraphScheduler(registry, cache);

            var request = new VideoScrapeRequest
            {
                Number = "SSIS-405",
                ContentKind = VideoContentKind.Censored,
                AvailableNodes = [new() { SourceId = "javbus" }]
            };

            var results = await scheduler.ExecuteAsync(request, VideoScrapeCacheMode.Auto, CancellationToken.None);

            Assert.Single(results);
            Assert.Equal("Cached", results[0].Metadata!.Title);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
