using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class FalenoSourceTests
{
    private const string SearchUrl = "https://faleno.jp/?s=FSDSS-077";
    private const string DetailUrl = "https://faleno.jp/works/fsdss077/";

    private static VideoSourceRequest Request(string number = "FSDSS-077") =>
        new(number, VideoContentKind.Censored, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        [SearchUrl] = """
            <html><body>
            <article>
              <h2><a href="https://faleno.jp/works/fsdss077/">FSDSS-077 波多野結衣</a></h2>
              <img src="https://faleno.jp/img/fsdss077_s.jpg">
            </article>
            <article>
              <h2><a href="https://faleno.jp/works/fsdss078/">FSDSS-078 別作品</a></h2>
            </article>
            </body></html>
            """,
        [DetailUrl] = """
            <html><head><meta property="og:image" content="https://faleno.jp/img/fsdss077_l.jpg"></head><body>
            <h1>FSDSS-077 サンプルタイトル</h1>
            <table>
              <tr><th>発売日</th><td>2023-10-19</td></tr>
              <tr><th>メーカー</th><td>FALENO</td></tr>
              <tr><th>シリーズ</th><td>FALENO STAR</td></tr>
              <tr><th>出演者</th><td>波多野結衣</td></tr>
            </table>
            <a rel="tag" href="/tag/solo/">単体作品</a>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_MatchingNumber_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new FalenoSource(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("faleno", result.SourceId);
        Assert.Equal("FSDSS-077 サンプルタイトル", meta.Title);
        Assert.Equal("https://faleno.jp/img/fsdss077_l.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2023, 10, 19), meta.ReleaseDate);
        Assert.Equal("FALENO", meta.Studio);
        Assert.Equal("FALENO STAR", meta.Series);
        Assert.Contains("波多野結衣", meta.Actors);
        Assert.Contains("単体作品", meta.Tags);
        Assert.True(meta.SourceUrls.ContainsKey("faleno"));
    }

    [Fact]
    public async Task FetchAsync_NoMatchingCard_ReturnsNoMatch()
    {
        // 搜索结果只命中 FSDSS-078，目标 FSDSS-077 不应被误匹配
        var pages = Pages();
        pages[SearchUrl] = "<html><body><article><h2><a href='https://faleno.jp/works/fsdss078/'>FSDSS-078 別作品</a></h2></article></body></html>";
        var source = new FalenoSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new FalenoSource(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        var source = new FalenoSource(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
