using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape;
using Xunit;

namespace ResourceGrab.Core.Tests.Services.VideoScrape;

// 阶段2.5: 分级波次调度测试 —— 首批命中即停，未命中才继续下一批

public class WaveSchedulerTests
{
    /// <summary>可编程的桩源：记录调用并返回预设 Outcome。</summary>
    private sealed class StubSource : IVideoScrapeSource
    {
        private readonly VideoSourceFetchResult _result;
        public StubSource(string id, VideoSourceOutcome outcome)
        {
            Id = id;
            _result = outcome == VideoSourceOutcome.Success
                ? VideoSourceFetchResult.Success(id, new VideoScrapeMetadata { Source = id, Title = $"{id}-title" })
                : VideoSourceFetchResult.NoMatch(id);
        }
        public string Id { get; }
        public string DisplayName => Id;
        public IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
            new HashSet<VideoContentKind>([VideoContentKind.Censored]);
        public List<string> Calls { get; } = [];
        public ValueTask<VideoSourceFetchResult> FetchAsync(VideoSourceRequest request, CancellationToken ct)
        {
            Calls.Add(request.Number);
            return ValueTask.FromResult(_result);
        }
    }

    private static VideoScrapeRequest Request(params VideoFetchNode[] nodes) => new()
    {
        Number = "SNOS-001",
        ContentKind = VideoContentKind.Censored,
        AvailableNodes = nodes,
    };

    [Fact]
    public async Task Tier1Hit_Tier2NeverFetched()
    {
        var tier1 = new StubSource("missav", VideoSourceOutcome.Success);
        var tier2 = new StubSource("javlibrary", VideoSourceOutcome.Success);
        var registry = new VideoScrapeSourceRegistry([tier1, tier2]);
        var scheduler = new VideoFetchGraphScheduler(registry);

        var results = await scheduler.ExecuteAsync(Request(
            new VideoFetchNode { SourceId = "missav", Tier = 1 },
            new VideoFetchNode { SourceId = "javlibrary", Tier = 2 }), VideoScrapeCacheMode.Auto, CancellationToken.None);

        Assert.Empty(tier2.Calls);                       // 首批命中，第二波不再抓
        Assert.Equal(VideoSourceOutcome.Success, results.Single(r => r.SourceId == "missav").Outcome);
    }

    [Fact]
    public async Task Tier1Missed_FallsToTier2()
    {
        var tier1 = new StubSource("missav", VideoSourceOutcome.NoMatch);
        var tier2 = new StubSource("javlibrary", VideoSourceOutcome.Success);
        var registry = new VideoScrapeSourceRegistry([tier1, tier2]);
        var scheduler = new VideoFetchGraphScheduler(registry);

        var results = await scheduler.ExecuteAsync(Request(
            new VideoFetchNode { SourceId = "missav", Tier = 1 },
            new VideoFetchNode { SourceId = "javlibrary", Tier = 2 }), VideoScrapeCacheMode.Auto, CancellationToken.None);

        Assert.Single(tier1.Calls);
        Assert.Single(tier2.Calls);                      // 首批未命中 → 继续第二波
        Assert.Contains(results, r => r.SourceId == "javlibrary" && r.Outcome == VideoSourceOutcome.Success);
    }

    [Fact]
    public async Task Tier1_AllFetched_Concurrently()
    {
        // 首批 2-3 个源是"一起抓"的：同一 Tier 内不受早停影响
        var a = new StubSource("missav", VideoSourceOutcome.NoMatch);
        var b = new StubSource("javbus", VideoSourceOutcome.NoMatch);
        var c = new StubSource("javdb", VideoSourceOutcome.Success);
        var registry = new VideoScrapeSourceRegistry([a, b, c]);
        var scheduler = new VideoFetchGraphScheduler(registry);

        var results = await scheduler.ExecuteAsync(Request(
            new VideoFetchNode { SourceId = "missav", Tier = 1 },
            new VideoFetchNode { SourceId = "javbus", Tier = 1 },
            new VideoFetchNode { SourceId = "javdb", Tier = 1 }), VideoScrapeCacheMode.Auto, CancellationToken.None);

        Assert.Single(a.Calls);
        Assert.Single(b.Calls);
        Assert.Single(c.Calls);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task Tier3_NeverFetched_WhenTier2Hits()
    {
        var t1 = new StubSource("missav", VideoSourceOutcome.NoMatch);
        var t2 = new StubSource("javlibrary", VideoSourceOutcome.Success);
        var t3 = new StubSource("dmm", VideoSourceOutcome.Success);
        var registry = new VideoScrapeSourceRegistry([t1, t2, t3]);
        var scheduler = new VideoFetchGraphScheduler(registry);

        var results = await scheduler.ExecuteAsync(Request(
            new VideoFetchNode { SourceId = "missav", Tier = 1 },
            new VideoFetchNode { SourceId = "javlibrary", Tier = 2 },
            new VideoFetchNode { SourceId = "dmm", Tier = 3 }), VideoScrapeCacheMode.Auto, CancellationToken.None);

        Assert.Empty(t3.Calls);                          // 第二波命中，第三波跳过
    }
}
