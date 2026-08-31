using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class DahliaSourceTests
{
    private const string SearchUrl = "https://dahlia-av.jp/?s=DLD-001";
    private const string DetailUrl = "https://dahlia-av.jp/works/dld001/";

    private static VideoSourceRequest Request(string number = "DLD-001") =>
        new(number, VideoContentKind.Censored, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        [SearchUrl] = """
            <html><body>
            <article>
              <h2><a href="https://dahlia-av.jp/works/dld001/">DLD-001 白昼夢</a></h2>
              <img src="https://dahlia-av.jp/img/dld001_s.jpg">
            </article>
            <article>
              <h2><a href="https://dahlia-av.jp/works/dld002/">DLD-002 別作品</a></h2>
            </article>
            </body></html>
            """,
        [DetailUrl] = """
            <html><head><meta property="og:image" content="https://dahlia-av.jp/img/dld001_l.jpg"></head><body>
            <h1 class="entry-title">DLD-001 白昼夢</h1>
            <time datetime="2024-06-07">2024.06.07</time>
            <table>
              <tr><th>シリーズ</th><td>DAHLIA</td></tr>
              <tr><th>出演</th><td><a href="#">小島みなみ</a></td></tr>
            </table>
            <a rel="tag" href="/tag/monochrome/">モノクロ</a>
            <a rel="tag" href="/tag/solo/">単体作品</a>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_MatchingNumber_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new DahliaSource(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("dahlia", result.SourceId);
        Assert.Equal("DLD-001 白昼夢", meta.Title);
        Assert.Equal("https://dahlia-av.jp/img/dld001_l.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2024, 6, 7), meta.ReleaseDate);
        Assert.Equal("DAHLIA", meta.Series);
        Assert.Contains("小島みなみ", meta.Actors);
        Assert.Contains("モノクロ", meta.Tags);
        Assert.True(meta.SourceUrls.ContainsKey("dahlia"));
    }

    [Fact]
    public async Task FetchAsync_NoMatchingCard_ReturnsNoMatch()
    {
        // 搜索结果只命中 DLD-002，目标 DLD-001 不应被误匹配
        var pages = Pages();
        pages[SearchUrl] = "<html><body><article><h2><a href='https://dahlia-av.jp/works/dld002/'>DLD-002 別作品</a></h2></article></body></html>";
        var source = new DahliaSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new DahliaSource(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        var source = new DahliaSource(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
