using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class Fc2ppvdbSourceTests
{
    private const string DetailUrl = "https://fc2ppvdb.com/articles/1234567";

    private static VideoSourceRequest Request(string number = "FC2-1234567") =>
        new(number, VideoContentKind.Fc2, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        [DetailUrl] = """
            <html>
            <head><meta property="og:image" content="https://fc2ppvdb.com/assets/cover/1234567.jpg" /></head>
            <body>
              <h1>FC2-1234567 サンプル女子大生テスト</h1>
              <div class="article-meta"><time datetime="2023-06-15">2023-06-15</time></div>
              <div class="actress"><a href="/actress/45">佐藤花子</a></div>
              <div class="tags"><a href="/tags/5">素人</a> <a href="/tags/9">巨乳</a></div>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_DetailPageFound_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new Fc2ppvdbSource(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("fc2ppvdb", result.SourceId);
        Assert.Equal("FC2-1234567 サンプル女子大生テスト", meta.Title);
        Assert.Equal("https://fc2ppvdb.com/assets/cover/1234567.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2023, 6, 15), meta.ReleaseDate);
        Assert.Contains("佐藤花子", meta.Actors);
        Assert.Contains("素人", meta.Tags);
        Assert.Contains("巨乳", meta.Tags);
        Assert.True(meta.SourceUrls.ContainsKey("fc2ppvdb"));
        // 详情页直构：应只发一次请求（无搜索步）
        Assert.Equal([DetailUrl], fetcher.Requested);
    }

    [Fact]
    public async Task FetchAsync_SoftNotFoundPage_ReturnsNoMatch()
    {
        // 站点对不存在的条目可能返回 200 错误页（无 og:image + "404" 标题）→ NoMatch
        var pages = Pages();
        pages[DetailUrl] = "<html><body><h1>404 Not Found</h1></body></html>";
        var source = new Fc2ppvdbSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_NumberWithoutDigits_ReturnsNoMatchWithoutRequest()
    {
        // 番号不含 6 位以上纯数字 id → 直构无从下手，不发请求直接 NoMatch
        var fetcher = new FakeFetcher(Pages());
        var source = new Fc2ppvdbSource(fetcher);

        var result = await source.FetchAsync(Request("SOME-THING"), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Empty(fetcher.Requested);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new Fc2ppvdbSource(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        // 直构 URL 不在字典 → 404 → HttpError（基类把 404 归为网络层失败语义）
        var source = new Fc2ppvdbSource(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
