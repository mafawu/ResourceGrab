using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class Fc2ClubSourceTests
{
    private const string DirectUrl = "https://fc2club.com/html/FC2PPV-7654321.html";
    private const string SearchUrl = "https://fc2club.com/search/FC2-7654321";
    private const string DetailUrl = "https://fc2club.com/html/FC2-PPV-7654321.html";

    private static VideoSourceRequest Request(string number = "FC2-7654321") =>
        new(number, VideoContentKind.Fc2, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        [DirectUrl] = """
            <html>
            <head><meta property="og:image" content="https://fc2club.com/img/7654321.jpg" /></head>
            <body>
              <h1 class="article-title">FC2-PPV-7654321 素人個人撮影テスト</h1>
              <div class="article-meta"><time datetime="2022-11-05">2022-11-05</time></div>
              <div class="article-content">
                <img src="/img/7654321.jpg" />
                <a href="/tags/amateur/">素人</a> <a href="/tags/bigin/">巨乳</a>
              </div>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_DirectDetailFound_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new Fc2ClubSource(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("fc2club", result.SourceId);
        Assert.Equal("素人個人撮影テスト", meta.Title);
        Assert.Equal("https://fc2club.com/img/7654321.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2022, 11, 5), meta.ReleaseDate);
        Assert.Contains("素人", meta.Tags);
        Assert.True(meta.SourceUrls.ContainsKey("fc2club"));
        Assert.Equal([DirectUrl], fetcher.Requested);   // 直构命中时无需搜索步
    }

    [Fact]
    public async Task FetchAsync_DirectDetail404_FallsBackToSearchAndSucceeds()
    {
        var fetcher = new FakeFetcher(PagesWithoutDirectDetail());
        var source = new Fc2ClubSource(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("素人個人撮影テスト", meta.Title);
        Assert.Contains("素人", meta.Tags);
        // 请求顺序：直构详情 404 → 搜索页 → 命中卡片对应详情
        Assert.Equal([DirectUrl, SearchUrl, DetailUrl], fetcher.Requested);
    }

    private static Dictionary<string, string> PagesWithoutDirectDetail() => new(StringComparer.Ordinal)
    {
        // 搜索卡片指向的详情页（URL 与直构 slug 不同：/html/FC2-PPV-...html）
        [DetailUrl] = """
            <html>
            <head><meta property="og:image" content="https://fc2club.com/img/7654321.jpg" /></head>
            <body>
              <h1 class="article-title">FC2-PPV-7654321 素人個人撮影テスト</h1>
              <div class="article-content">
                <img src="/img/7654321.jpg" />
                <a href="/tags/amateur/">素人</a> <a href="/tags/bigin/">巨乳</a>
              </div>
            </body></html>
            """,
        [SearchUrl] = """
            <html><body>
              <a href="/html/FC2-PPV-7654321.html">FC2-PPV-7654321 素人個人撮影テスト</a>
              <a href="/html/FC2-PPV-1111111.html">FC2-PPV-1111111 别的条目</a>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_SearchPageNoHit_ReturnsNoMatch()
    {
        // 直构 404 → 搜索兜底 → 搜索页有结果但番号不匹配 → NoMatch
        var pages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SearchUrl] = """
                <html><body><a href="/html/FC2-PPV-1111111.html">FC2-PPV-1111111 别的条目</a></body></html>
                """,
        };
        var source = new Fc2ClubSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new Fc2ClubSource(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPages_ReturnsHttpError()
    {
        // 直构 404 + 搜索页 404 → HttpError（404 归网络层失败语义）
        var source = new Fc2ClubSource(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
