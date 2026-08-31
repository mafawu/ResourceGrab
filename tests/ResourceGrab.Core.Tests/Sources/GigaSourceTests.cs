using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class GigaSourceTests
{
    private const string SearchUrl = "https://www.giga-web.jp/search/?keyword=GHKP-042";
    private const string DetailUrl = "https://www.giga-web.jp/product/detail/12345/";

    private static VideoSourceRequest Request(string number = "GHKP-042") =>
        new(number, VideoContentKind.Censored, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        [SearchUrl] = """
            <html><body><div class="searchresult">
              <a href="/product/detail/12345/">GHKP-042 戦隊ヒロイン</a>
              <a href="/product/detail/67890/">GHKP-043 続編</a>
            </div></body></html>
            """,
        [DetailUrl] = """
            <html><head><meta property="og:image" content="https://www.giga-web.jp/img/ghkp042_l.jpg"></head><body>
            <h1>GHKP-042 戦隊ヒロイン 危機一髪</h1>
            <table>
              <tr><th>発売日</th><td>2024-04-12</td></tr>
              <tr><th>収録時間</th><td>85 分</td></tr>
              <tr><th>メーカー</th><td>GIGA</td></tr>
              <tr><th>出演者</th><td><a href="#">坂本みなみ</a></td></tr>
              <tr><th>ジャンル</th><td><a href="#">特撮</a><a href="#">ヒロイン</a></td></tr>
            </table>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_MatchingNumber_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new GigaSource(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("giga", result.SourceId);
        Assert.Equal("GHKP-042 戦隊ヒロイン 危機一髪", meta.Title);
        Assert.Equal("https://www.giga-web.jp/img/ghkp042_l.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2024, 4, 12), meta.ReleaseDate);
        Assert.Equal(85, meta.RuntimeMinutes);
        Assert.Equal("GIGA", meta.Studio);
        Assert.Contains("坂本みなみ", meta.Actors);
        Assert.Contains("特撮", meta.Tags);
        Assert.True(meta.SourceUrls.ContainsKey("giga"));
    }

    [Fact]
    public async Task FetchAsync_NoMatchingCard_ReturnsNoMatch()
    {
        // 搜索结果只命中 GHKP-043，目标 GHKP-042 不应被误匹配
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div class='searchresult'><a href='/product/detail/67890/'>GHKP-043 続編</a></div></body></html>";
        var source = new GigaSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new GigaSource(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        var source = new GigaSource(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
