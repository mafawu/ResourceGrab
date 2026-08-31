using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class Fc2SourceTests
{
    private const string SearchUrl = "https://adult.fc2.com/search/?q=FC2-2345678";
    private const string DetailUrl = "https://adult.fc2.com/a/content/2345678/";

    private static VideoSourceRequest Request(string number = "FC2-2345678") =>
        new(number, VideoContentKind.Fc2, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        [SearchUrl] = """
            <html><body>
              <a href="/a/content/2345678/"><span class="title">FC2-2345678 素人投稿ムービー</span></a>
              <a href="/a/content/9999999/"><span class="title">FC2-9999999 别的条目</span></a>
            </body></html>
            """,
        [DetailUrl] = """
            <html>
            <head>
              <meta property="og:image" content="https://adult.fc2.com/img/2345678.jpg" />
              <meta property="og:description" content="個人撮影投稿のテスト概要" />
            </head>
            <body>
              <h1>FC2-2345678 素人投稿ムービー</h1>
              <div class="info"><time datetime="2021-09-20">2021-09-20</time></div>
              <div class="tags"><a href="/search/?category=tag3">素人</a> <a href="/search/?category=tag7">ナンパ</a></div>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_MatchingCard_ReturnsSuccessWithMetadata()
    {
        var source = new Fc2Source(new FakeFetcher(Pages()));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("fc2", result.SourceId);
        Assert.Equal("FC2-2345678 素人投稿ムービー", meta.Title);
        Assert.Equal("https://adult.fc2.com/img/2345678.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2021, 9, 20), meta.ReleaseDate);
        Assert.Equal("個人撮影投稿のテスト概要", meta.Description);
        Assert.Contains("素人", meta.Tags);
        Assert.True(meta.SourceUrls.ContainsKey("fc2"));
    }

    [Fact]
    public async Task FetchAsync_SevenDigitFc2Id_PureDigitMatch()
    {
        // 基类 MatchesNumber 候选正则数字段上限 6 位，7 位 FC2 id 需 MatchesFc2Id 兜底
        var pages = Pages();
        pages[SearchUrl] = "<html><body><a href='/a/content/2345678/'>FC2-2345678</a></body></html>";
        var source = new Fc2Source(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_NoMatchingCard_ReturnsNoMatch()
    {
        // 搜索结果只有别的番号 → 不误匹配 → NoMatch
        var pages = Pages();
        pages[SearchUrl] = "<html><body><a href='/a/content/9999999/'><span class='title'>FC2-9999999 别的条目</span></a></body></html>";
        var source = new Fc2Source(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new Fc2Source(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        var source = new Fc2Source(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
