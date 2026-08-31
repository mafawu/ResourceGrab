using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class ThePornDbSourceTests
{
    private const string SearchUrl = "https://api.theporndb.net/scenes?q=TEST-001";
    private const string DetailUrl = "https://api.theporndb.net/scenes/sc123";

    private static VideoSourceRequest Request(string number = "TEST-001") =>
        new(number, VideoContentKind.Western, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        [SearchUrl] = """
            {"data":[
              {"id":"sc999","title":"TEST-002 Other Scene","slug":"test-002-other-scene"},
              {"id":"sc123","title":"TEST-001 Hot Night","slug":"test-001-hot-night"}
            ]}
            """,
        [DetailUrl] = """
            {"data":{"id":"sc123","title":"TEST-001 Hot Night","slug":"test-001-hot-night",
              "description":"サンプル説明","date":"2024-06-01","duration":42,
              "cover_url":{"url":"https://cdn.theporndb.net/scenes/test-001-cover.jpg"},
              "performers":[{"name":"Jane Doe"},{"name":"Ann Lee"}],
              "tags":[{"name":"TagA"}],
              "site":{"name":"TestSite"}}}
            """,
    };

    [Fact]
    public async Task FetchAsync_MatchingScene_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new ThePornDbSource(fetcher, apiKey: "test-key");

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("theporndb", result.SourceId);
        Assert.Equal("TEST-001 Hot Night", meta.Title);
        Assert.Equal("https://cdn.theporndb.net/scenes/test-001-cover.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2024, 6, 1), meta.ReleaseDate);
        Assert.Equal(42, meta.RuntimeMinutes);
        Assert.Equal("TestSite", meta.Studio);
        Assert.Contains("Jane Doe", meta.Actors);
        Assert.Contains("Ann Lee", meta.Actors);
        Assert.Contains("TagA", meta.Tags);
        Assert.True(meta.SourceUrls.ContainsKey("theporndb"));
        // 搜索命中后请求详情端点
        Assert.Equal(DetailUrl, fetcher.Requested[^1]);
    }

    [Fact]
    public async Task FetchAsync_WrongNumberInResults_ReturnsNoMatch()
    {
        // 搜索结果只含 TEST-002，目标 TEST-001 不应被误匹配
        var pages = Pages();
        pages[SearchUrl] = """
            {"data":[{"id":"sc999","title":"TEST-002 Other Scene","slug":"test-002-other-scene"}]}
            """;
        var source = new ThePornDbSource(new FakeFetcher(pages), apiKey: "test-key");

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_NoApiKey_ReturnsNoMatchWithoutRequests()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new ThePornDbSource(fetcher);   // apiKey 未配置

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
        Assert.Empty(fetcher.Requested);   // 未发出任何请求
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new ThePornDbSource(new FakeFetcher([], forceStatus: 403), apiKey: "test-key");

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingEndpoint_ReturnsHttpError()
    {
        var source = new ThePornDbSource(new FakeFetcher([]), apiKey: "test-key");

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
