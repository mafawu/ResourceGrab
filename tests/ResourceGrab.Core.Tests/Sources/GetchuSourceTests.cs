using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class GetchuSourceTests
{
    private const string HomePage = "http://www.getchu.com/";
    private const string SearchUrl = "http://www.getchu.com/php/nsearch.phtml?search_str=GETC-001&genre=anime";
    private const string DetailUrl = "http://www.getchu.com/soft.phtml?id=98765";

    private static VideoSourceRequest Request(string number = "GETC-001") =>
        new(number, VideoContentKind.Hentai, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        // 年龄墙预热：先 GET 首页让 Cookie 罐持有 adult_check_flag cookie
        [HomePage] = "<html><body>top</body></html>",
        [SearchUrl] = """
            <html><body><div class="result">
              <a href="/soft.phtml?id=98765">GETC-001 魔法少女サンプル</a>
              <a href="/soft.phtml?id=11111">GETC-002 別作品</a>
            </div></body></html>
            """,
        [DetailUrl] = """
            <html><body>
            <div class="wrap_title"><a>GETC-001 魔法少女サンプルタイトル</a></div>
            <img src="http://image.getchu.com/brandnew/98765/a_main.jpg">
            <table>
              <tr><td>発売日：</td><td>2024-03-29</td></tr>
              <tr><td>ブランド：</td><td>サンプルブランド</td></tr>
              <tr><td>ジャンル：</td><td><a href="#">魔法少女</a> <a href="#">アニメ</a></td></tr>
            </table>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_MatchingNumber_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new GetchuSource(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("getchu", result.SourceId);
        Assert.Equal("GETC-001 魔法少女サンプルタイトル", meta.Title);
        Assert.Equal("http://image.getchu.com/brandnew/98765/a_main.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2024, 3, 29), meta.ReleaseDate);
        Assert.Equal("サンプルブランド", meta.Studio);
        Assert.Contains("魔法少女", meta.Tags);
        Assert.True(meta.SourceUrls.ContainsKey("getchu"));
        // 年龄墙预热：首页请求先于搜索
        Assert.Equal(HomePage, fetcher.Requested[0]);
    }

    [Fact]
    public async Task FetchAsync_NoMatchingCard_ReturnsNoMatch()
    {
        // 搜索结果只命中 GETC-002，目标 GETC-001 不应被误匹配
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div class='result'><a href='/soft.phtml?id=11111'>GETC-002 別作品</a></div></body></html>";
        var source = new GetchuSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new GetchuSource(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        var source = new GetchuSource(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
