using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class PrestigeSourceTests
{
    private const string SearchUrl = "https://www.prestige-av.com/search?keyword=PRED-300";
    private const string DetailUrl = "https://www.prestige-av.com/goods/PRED-300/";

    private static VideoSourceRequest Request(string number = "PRED-300") =>
        new(number, VideoContentKind.Censored, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        [SearchUrl] = """
            <html><body><div class="search-result">
              <a href="/goods/PRED-300/">PRED-300 サンプル作品</a>
              <a href="/goods/PRED-301/">PRED-301 別作品</a>
            </div></body></html>
            """,
        [DetailUrl] = """
            <html><head>
            <meta property="og:title" content="PRED-300 サンプルタイトル">
            <meta property="og:image" content="https://www.prestige-av.com/assets/cover/PRED-300.jpg">
            </head><body>
            <h1 class="product__title">PRED-300 サンプルタイトル</h1>
            <dl class="product__spec">
              <dt>発売日</dt><dd>2024-02-01</dd>
              <dt>メーカー</dt><dd>プレステージ</dd>
              <dt>出演女優</dt><dd><a href="#">女優A</a> <a href="#">女優B</a></dd>
            </dl>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_MatchingNumber_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new PrestigeSource(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("prestige", result.SourceId);
        Assert.Equal("サンプルタイトル", meta.Title);   // 番号前缀被剥掉
        Assert.Equal("https://www.prestige-av.com/assets/cover/PRED-300.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2024, 2, 1), meta.ReleaseDate);
        Assert.Equal("プレステージ", meta.Studio);
        Assert.Contains("女優A", meta.Actors);
        Assert.Contains("女優B", meta.Actors);
        Assert.True(meta.SourceUrls.ContainsKey("prestige"));
        Assert.Equal(SearchUrl, fetcher.Requested[0]);
        Assert.Equal(DetailUrl, fetcher.Requested[1]);
    }

    [Fact]
    public async Task FetchAsync_WrongNumberInResults_ReturnsNoMatch()
    {
        // 搜索结果只命中 PRED-301，目标 PRED-300 不应被误匹配
        var pages = Pages();
        pages[SearchUrl] = "<html><body><a href='/goods/PRED-301/'>PRED-301 別作品</a></body></html>";
        var source = new PrestigeSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new PrestigeSource(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        var source = new PrestigeSource(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
