using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class Kin8SourceTests
{
    private const string SearchUrl = "https://www.kin8tengoku.com/search/?keyword=KTG-001";
    private const string DetailUrl = "https://www.kin8tengoku.com/detailpages/1234.html";

    private static VideoSourceRequest Request(string number = "KTG-001") =>
        new(number, VideoContentKind.Uncensored, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        [SearchUrl] = """
            <html><body><div class="list">
              <a href="/detailpages/1234.html">KTG-001 無修正サンプル</a>
              <a href="/detailpages/5678.html">KTG-002 別作品</a>
            </div></body></html>
            """,
        [DetailUrl] = """
            <html><body>
            <h1>KTG-001 無修正サンプルタイトル</h1>
            <div class="detail-info"><img src="/img/1234_main.jpg"></div>
            <table>
              <tr><th>発売日</th><td>2024-05-01</td></tr>
              <tr><th>収録時間</th><td>130 分</td></tr>
              <tr><th>出演者</th><td><a href="#">花咲みなみ</a> <a href="#">星野あかり</a></td></tr>
              <tr><th>ジャンル</th><td><a href="#">巨乳</a> <a href="#">独占配信</a></td></tr>
            </table>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_MatchingNumber_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new Kin8Source(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("kin8", result.SourceId);
        Assert.Equal("KTG-001 無修正サンプルタイトル", meta.Title);
        Assert.Equal("https://www.kin8tengoku.com/img/1234_main.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2024, 5, 1), meta.ReleaseDate);
        Assert.Equal(130, meta.RuntimeMinutes);
        Assert.Contains("花咲みなみ", meta.Actors);
        Assert.Contains("巨乳", meta.Tags);
        Assert.True(meta.SourceUrls.ContainsKey("kin8"));
    }

    [Fact]
    public async Task FetchAsync_NoMatchingCard_ReturnsNoMatch()
    {
        // 搜索结果只命中 KTG-002，目标 KTG-001 不应被误匹配
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div class='list'><a href='/detailpages/5678.html'>KTG-002 別作品</a></div></body></html>";
        var source = new Kin8Source(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new Kin8Source(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        var source = new Kin8Source(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
