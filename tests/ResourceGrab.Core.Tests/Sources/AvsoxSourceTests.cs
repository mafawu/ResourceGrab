using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class AvsoxSourceTests
{
    private const string Number = "FC2-PPV-3175496";
    private const string SearchUrl = "https://avsox.click/search/FC2-PPV-3175496";
    private const string MirrorSearchUrl = "https://avsox.com/search/FC2-PPV-3175496";
    private const string DetailUrl = "https://avsox.click/?v=avsox1abcde";

    private static VideoSourceRequest Request(string number = Number) =>
        new(number, VideoContentKind.Fc2, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        [SearchUrl] = """
            <html><body><div id="waterfall">
              <div class="item"><a href="./?v=avsox1abcde"><div class="id">FC2-PPV-3175496</div><div class="title">FC2-PPV-3175496 サンプル</div></a></div>
              <div class="item"><a href="./?v=avsox2fghij"><div class="id">FC2-PPV-3175500</div><div class="title">FC2-PPV-3175500 タイトル2</div></a></div>
            </div></body></html>
            """,
        [DetailUrl] = """
            <html><body>
            <div id="video_title"><h3><a>FC2-PPV-3175496 サンプルタイトルテスト</a></h3></div>
            <div id="video_jacket"><img src="//pics.example.com/fc23175496pl.jpg" /></div>
            <div id="video_info" class="info">
              <div id="video_date" class="item"><table><tr><td class="header">発売日:</td><td class="text">2024-01-20</td></tr></table></div>
              <div id="video_maker" class="item"><table><tr><td class="header">製作商:</td><td class="text"><a href="#">FC2</a></td></tr></table></div>
              <div id="video_genres" class="item"><table><tr><td class="header">類別:</td><td class="text"><a href="#">素人</a> <a href="#">巨乳</a></td></tr></table></div>
              <div id="video_cast" class="item"><table><tr><td class="header">女優:</td><td class="text"><a href="#">無名素人</a></td></tr></table></div>
            </div>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_MatchingNumber_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new AvsoxSource(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("avsox", result.SourceId);
        Assert.Equal("サンプルタイトルテスト", meta.Title);   // 番号前缀被剥掉
        Assert.Equal("https://pics.example.com/fc23175496pl.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2024, 1, 20), meta.ReleaseDate);
        Assert.Equal("FC2", meta.Studio);
        Assert.Contains("素人", meta.Tags);
        Assert.Contains("無名素人", meta.Actors);
        Assert.True(meta.SourceUrls.ContainsKey("avsox"));
        Assert.Equal(DetailUrl, meta.SourceUrls["avsox"]);
    }

    [Fact]
    public async Task FetchAsync_PrimarySiteFails_FallsBackToMirror()
    {
        // 主站搜索 404 → 换镜像 avsox.com
        var pages = Pages();
        var mirrorSearchUrl = MirrorSearchUrl;
        var mirrorDetailUrl = "https://avsox.com/?v=avsox1abcde";
        pages.Remove(SearchUrl);
        pages[mirrorSearchUrl] = """
            <html><body><div id="waterfall"><div class="item"><a href="./?v=avsox1abcde"><div class="id">FC2-PPV-3175496</div></a></div></div></body></html>
            """;
        pages[mirrorDetailUrl] = """
            <html><body><div id="video_title"><h3><a>FC2-PPV-3175496 ミラータイトル</a></h3></div>
            <div id="video_jacket"><img src="/p/fc2.jpg" /></div></body></html>
            """;
        var fetcher = new FakeFetcher(pages);
        var source = new AvsoxSource(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        Assert.Equal("ミラータイトル", result.Metadata!.Title);
        Assert.Contains(mirrorSearchUrl, fetcher.Requested);
    }

    [Fact]
    public async Task FetchAsync_NoMatchingCard_ReturnsNoMatch()
    {
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div id='waterfall'></div></body></html>";
        var source = new AvsoxSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_WrongNumberInCard_ReturnsNoMatch()
    {
        // 搜索结果只命中 FC2-PPV-3175500，目标 ...496 不应被误匹配
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div id='waterfall'><div class='item'><a href='./?v=x'><div class='id'>FC2-PPV-3175500</div></a></div></div></body></html>";
        var source = new AvsoxSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new AvsoxSource(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        var source = new AvsoxSource(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
