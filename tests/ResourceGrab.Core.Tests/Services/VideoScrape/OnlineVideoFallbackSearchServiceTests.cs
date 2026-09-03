using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape;
using Xunit;

namespace ResourceGrab.Core.Tests.Services.VideoScrape;

// 在线搜索换源兜底测试：MissAV 关键词搜不到且关键词为番号时，
// 按 contentRoutes 顺序逐源按番号取详情，首个命中即返回。
// 全部用内存 stub 源，禁止真实网络。

public class OnlineVideoFallbackSearchServiceTests
{
    private sealed class StubSource : IVideoScrapeSource
    {
        private readonly VideoSourceFetchResult _result;
        public List<string> Requests { get; } = [];

        public StubSource(string id, VideoSourceFetchResult result)
        {
            Id = id;
            _result = result;
        }

        public string Id { get; }
        public string DisplayName => Id.ToUpperInvariant();
        public IReadOnlySet<VideoContentKind> SupportedKinds { get; init; }
            = new HashSet<VideoContentKind> { VideoContentKind.Censored, VideoContentKind.Fc2, VideoContentKind.Uncensored };

        public ValueTask<VideoSourceFetchResult> FetchAsync(VideoSourceRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Number);
            return ValueTask.FromResult(_result);
        }
    }

    private static VideoScrapeMetadata MakeMeta(string sourceId) => new()
    {
        Source = sourceId,
        Title = "测试标题",
        Actors = ["女优A"],
        CoverUrl = $"https://img.example/{sourceId}-cover.jpg",
        SourceUrls = { [sourceId] = $"https://{sourceId}.example/dvd/ssis-960" },
    };

    private static VideoScrapeAdvancedSettings MakeAdvanced(params string[] route) => new()
    {
        ContentRoutes = [new ContentRouteEntry { Kind = VideoContentKind.Censored, Sources = [.. route] }],
        SourceConfigs = route.ToDictionary(id => id, _ => new VideoSourceConfig { Enabled = true }),
    };

    private static OnlineVideoFallbackSearchService MakeService(
        VideoScrapeAdvancedSettings advanced, IReadOnlyList<IVideoScrapeSource> sources,
        IVideoSourceSnapshotCache? cache = null) =>
        new(new VideoScrapeSourceRegistry(sources), advanced, cache, NullLogger.Instance);

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();
        public string LogDirectory => Path.Combine(Path.GetTempPath(), "fallback-search-test-logs");
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    [Fact]
    public async Task SearchAsync_FirstHitSourceWins_InRouteOrder()
    {
        var javbus = new StubSource("javbus", VideoSourceFetchResult.Failure("javbus", VideoSourceOutcome.NoMatch, null));
        var javdb = new StubSource("javdb", VideoSourceFetchResult.Success("javdb", MakeMeta("javdb")));
        var dmm = new StubSource("dmm", VideoSourceFetchResult.Success("dmm", MakeMeta("dmm")));
        var service = MakeService(MakeAdvanced("missav", "javbus", "javdb", "dmm"), [javbus, javdb, dmm]);

        var hit = await service.SearchAsync("SSIS-960");

        Assert.NotNull(hit);
        Assert.Equal("javdb", hit.Summary.SourceId);
        Assert.Equal("javdb/ssis-960", hit.Summary.Id);   // 合成 Id：sourceId/番号小写
        Assert.Equal("SSIS-960", hit.Summary.Number);
        Assert.Equal("JAVDB", hit.Summary.KindLabel);      // 角标显示兜底来源
        Assert.Equal("测试标题", hit.Detail.Title);
        Assert.Equal("https://javdb.example/dvd/ssis-960", hit.Detail.VideoUrl);
        // missav 在路由中但被跳过（它就是刚没搜到的源）；javbus 排在前、NoMatch 后轮到 javdb 命中，
        // dmm 不应再被请求
        Assert.Single(javbus.Requests, "SSIS-960");
        Assert.Single(javdb.Requests, "SSIS-960");
        Assert.Empty(dmm.Requests);
    }

    [Fact]
    public async Task SearchAsync_DisabledAndUnregisteredSources_Skipped()
    {
        var javdb = new StubSource("javdb", VideoSourceFetchResult.Success("javdb", MakeMeta("javdb")));
        var advanced = MakeAdvanced("missav", "javbus", "javdb");
        advanced.SourceConfigs["javbus"] = new VideoSourceConfig { Enabled = false };
        // javbus 未注册进 registry：应被跳过而不是抛异常
        var service = MakeService(advanced, [javdb]);

        var hit = await service.SearchAsync("ssis-960");

        Assert.NotNull(hit);
        Assert.Equal("javdb", hit.Summary.SourceId);
    }

    [Fact]
    public async Task SearchAsync_NoSourceHits_ReturnsNull()
    {
        var javbus = new StubSource("javbus", VideoSourceFetchResult.Failure("javbus", VideoSourceOutcome.NoMatch, null));
        var javdb = new StubSource("javdb", VideoSourceFetchResult.Failure("javdb", VideoSourceOutcome.Blocked, "cf"));
        var service = MakeService(MakeAdvanced("javbus", "javdb"), [javbus, javdb]);

        var hit = await service.SearchAsync("SSIS-960");

        Assert.Null(hit);
        // 每个源各被请求了一次，都未命中
        Assert.Single(javbus.Requests, "SSIS-960");
        Assert.Single(javdb.Requests, "SSIS-960");
    }

    [Fact]
    public async Task SearchAsync_NonNumberKeyword_ReturnsNullWithoutNetwork()
    {
        var javbus = new StubSource("javbus", VideoSourceFetchResult.Success("javbus", MakeMeta("javbus")));
        var service = MakeService(MakeAdvanced("javbus"), [javbus]);

        var hit = await service.SearchAsync("桃果あかり");

        Assert.Null(hit);
        Assert.Empty(javbus.Requests); // 非番号关键词不联网
    }

    [Fact]
    public async Task SearchAsync_SnapshotCacheHit_ZeroNetwork()
    {
        var javbus = new StubSource("javbus", VideoSourceFetchResult.Success("javbus", MakeMeta("javbus")));
        var cache = new StubCache { Snapshots =
        {
            ["javbus|SSIS-960"] = new VideoSourceSnapshot
            {
                Number = "SSIS-960", SourceId = "javbus", SchemaVersion = "v1",
                Metadata = MakeMeta("javbus"),
            },
        } };
        var service = MakeService(MakeAdvanced("javbus"), [javbus], cache);

        var hit = await service.SearchAsync("SSIS-960");

        Assert.NotNull(hit);
        Assert.Equal("测试标题", hit.Summary.Title);
        Assert.Empty(javbus.Requests); // 缓存命中，不联网
    }

    private sealed class StubCache : IVideoSourceSnapshotCache
    {
        public Dictionary<string, VideoSourceSnapshot> Snapshots { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<VideoSourceSnapshot?> GetAsync(string number, string sourceId, string? language = null)
            => ValueTask.FromResult(Snapshots.TryGetValue($"{sourceId}|{number}", out var s) ? s : null);

        public ValueTask SetAsync(VideoSourceSnapshot snapshot)
        {
            Snapshots[$"{snapshot.SourceId}|{snapshot.Number}"] = snapshot;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> HasAsync(string number, string sourceId, string? language = null)
            => ValueTask.FromResult(Snapshots.ContainsKey($"{sourceId}|{number}"));

        public ValueTask ClearAsync(string number) => ValueTask.CompletedTask;
        public ValueTask ClearAllAsync() => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<string>> GetCachedSourceIdsAsync(string number)
            => ValueTask.FromResult<IReadOnlyList<string>>(
                Snapshots.Values.Where(s => s.Number == number).Select(s => s.SourceId).ToList());
    }
}
