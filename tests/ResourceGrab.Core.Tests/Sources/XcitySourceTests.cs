using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class XcitySourceTests
{
    private const string HomePage = "http://www.xcity.jp/";
    private const string SearchUrl = "http://www.xcity.jp/search/?keyword=SSIS-001";
    private const string DetailUrl = "http://www.xcity.jp/avod/detail/?id=12345";

    private static VideoSourceRequest Request(string number = "SSIS-001") =>
        new(number, VideoContentKind.Censored, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        // 年龄墙预热：先 GET 首页让 Cookie 罐持有年龄确认 cookie
        [HomePage] = "<html><body>top</body></html>",
        [SearchUrl] = """
            <html><body><div class="result">
              <a href="/avod/detail/?id=12345">SSIS-001 サンプル</a>
              <a href="/avod/detail/?id=67890">SSIS-002 別作品</a>
            </div></body></html>
            """,
        [DetailUrl] = """
            <html><body>
            <h1>SSIS-001 サンプルタイトルテスト</h1>
            <img src="http://img.xcity.jp/main/big/ssis001.jpg">
            <table>
              <tr><th>発売日</th><td>2024-02-09</td></tr>
              <tr><th>収録時間</th><td>150 分</td></tr>
              <tr><th>監督</th><td>佐山愛</td></tr>
              <tr><th>メーカー</th><td>S1</td></tr>
              <tr><th>レーベル</th><td>S1 NO.1 STYLE</td></tr>
              <tr><th>シリーズ</th><td>シリーズ名</td></tr>
              <tr><th>出演者</th><td><a href="#">朝比奈えみり</a></td></tr>
              <tr><th>ジャンル</th><td><a href="#">巨乳</a><a href="#">単体作品</a></td></tr>
            </table>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_MatchingNumber_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new XcitySource(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("xcity", result.SourceId);
        Assert.Equal("SSIS-001 サンプルタイトルテスト", meta.Title);
        Assert.Equal("http://img.xcity.jp/main/big/ssis001.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2024, 2, 9), meta.ReleaseDate);
        Assert.Equal(150, meta.RuntimeMinutes);
        Assert.Equal("佐山愛", meta.Director);
        Assert.Equal("S1", meta.Studio);
        Assert.Equal("S1 NO.1 STYLE", meta.Publisher);
        Assert.Equal("シリーズ名", meta.Series);
        Assert.Contains("朝比奈えみり", meta.Actors);
        Assert.Contains("単体作品", meta.Tags);
        Assert.True(meta.SourceUrls.ContainsKey("xcity"));
        // 年龄墙预热：首页请求先于搜索
        Assert.Equal(HomePage, fetcher.Requested[0]);
    }

    [Fact]
    public async Task FetchAsync_NoMatchingCard_ReturnsNoMatch()
    {
        // 搜索结果只命中 SSIS-002，目标 SSIS-001 不应被误匹配
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div class='result'><a href='/avod/detail/?id=67890'>SSIS-002 別作品</a></div></body></html>";
        var source = new XcitySource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new XcitySource(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        var source = new XcitySource(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
