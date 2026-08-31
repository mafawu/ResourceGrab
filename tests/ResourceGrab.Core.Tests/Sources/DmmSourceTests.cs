using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class DmmSourceTests
{
    private const string SearchPadded = "https://www.dmm.co.jp/search/=/searchstr=SNOS00001/sort=ranking/";
    private const string SearchOriginal = "https://www.dmm.co.jp/search/=/searchstr=SNOS-001/sort=ranking/";
    private const string DigitalApi = "https://api.video.dmm.co.jp/graphql";
    private const string MonoDetail = "https://www.dmm.co.jp/mono/dv/-/detail/=/cid=snos00001/";

    private static VideoSourceRequest Request(string number = "SNOS-001") =>
        new(number, VideoContentKind.Censored, null, null, new VideoSourceFetchOptions());

    /// <summary>搜索页 script 内嵌 JSON：detailUrl 带转义斜杠 \/，数字 cid 用零填充 5 位形态。</summary>
    private static string SearchHtml() => """
        <html><body><script>
        var itemlist = {"result":[
          {"detailUrl":"\/mono\/dv\/-\/detail\/=\/cid=other00001\/","title":"別作品"},
          {"detailUrl":"\/digital\/videoa\/-\/detail\/=\/cid=snos00001\/","title":"SNOS-001 サンプル"}
        ]};
        </script></body></html>
        """;

    private static Dictionary<string, string> DigitalPages() => new(StringComparer.Ordinal)
    {
        [SearchPadded] = SearchHtml(),
        [SearchOriginal] = SearchHtml(),
        [DigitalApi] = """
            {"data":{"ppvContent":{"title":"サンプルタイトル","description":"サンプル説明文",
              "releaseDate":"2024-05-10","runtime":150,
              "maker":{"name":"サンプルメーカー"},"director":{"name":"サンプル監督"},
              "actresses":[{"name":"女優一"},{"name":"女優二"}],"genres":[{"name":"ジャンル一"}],
              "packageImage":{"url":"https://pics.dmm.co.jp/digital/video/snos00001/snos00001ps.jpg"}}}}
            """,
    };

    [Fact]
    public async Task FetchAsync_DigitalGraphQL_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(DigitalPages());
        var source = new DmmSource(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("dmm", result.SourceId);
        Assert.Equal("サンプルタイトル", meta.Title);
        // 图片增强: ps.jpg → pl.jpg
        Assert.Equal("https://pics.dmm.co.jp/digital/video/snos00001/snos00001pl.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2024, 5, 10), meta.ReleaseDate);
        Assert.Equal(150, meta.RuntimeMinutes);
        Assert.Equal("サンプルメーカー", meta.Studio);
        Assert.Equal("サンプル監督", meta.Director);
        Assert.Contains("女優一", meta.Actors);
        Assert.Contains("女優二", meta.Actors);
        Assert.Contains("ジャンル一", meta.Tags);
        Assert.True(meta.SourceUrls.ContainsKey("dmm"));
        // 搜索后走 GraphQL 端点
        Assert.Equal(DigitalApi, fetcher.Requested[^1]);
    }

    [Fact]
    public async Task FetchAsync_HtmlChannel_ReturnsSuccessWithMetadata()
    {
        var pages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SearchPadded] = """
                <html><body><script>
                var itemlist = {"result":[{"detailUrl":"\/mono\/dv\/-\/detail\/=\/cid=snos00001\/","title":"SNOS-001"}]};
                </script></body></html>
                """,
            [SearchOriginal] = "<html><body></body></html>",
            [MonoDetail] = """
                <html><head>
                <meta property="og:title" content="SNOS-001 モノサンプル">
                </head><body>
                <h1 id="title">SNOS-001 モノサンプル</h1>
                <div id="package-photo"><img id="package-img" src="https://pics.dmm.co.jp/mono/movie/snos00001/snos00001ps.jpg"></div>
                <dl class="info__table">
                  <dt>メーカー</dt><dd><a href="#">メーカーA</a></dd>
                  <dt>レーベル</dt><dd><a href="#">レーベルA</a></dd>
                  <dt>監督</dt><dd><a href="#">監督A</a></dd>
                  <dt>シリーズ</dt><dd><a href="#">シリーズA</a></dd>
                  <dt>ジャンル</dt><dd><a href="#">タグA</a> <a href="#">タグB</a></dd>
                  <dt>出演者</dt><dd><a href="#">出演A</a> <a href="#">出演B</a></dd>
                  <dt>発売日</dt><dd>2024-01-05</dd>
                  <dt>収録時間</dt><dd>120 分</dd>
                </dl>
                </body></html>
                """,
        };
        var source = new DmmSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("モノサンプル", meta.Title);   // 番号前缀被剥掉
        // 图片增强: ps.jpg → pl.jpg
        Assert.Equal("https://pics.dmm.co.jp/mono/movie/snos00001/snos00001pl.jpg", meta.CoverUrl);
        Assert.Equal("メーカーA", meta.Studio);
        Assert.Equal("レーベルA", meta.Publisher);
        Assert.Equal("監督A", meta.Director);
        Assert.Equal("シリーズA", meta.Series);
        Assert.Contains("タグA", meta.Tags);
        Assert.Contains("出演A", meta.Actors);
        Assert.Equal(new DateTime(2024, 1, 5), meta.ReleaseDate);
        Assert.Equal(120, meta.RuntimeMinutes);
    }

    [Fact]
    public async Task FetchAsync_WrongNumberInSearchResult_ReturnsNoMatch()
    {
        // 搜索结果只含 other00001，目标 SNOS-001 不应被误匹配
        var pages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SearchPadded] = """
                <html><body><script>
                var itemlist = {"result":[{"detailUrl":"\/mono\/dv\/-\/detail\/=\/cid=other00001\/","title":"別作品"}]};
                </script></body></html>
                """,
            [SearchOriginal] = "<html><body></body></html>",
        };
        var source = new DmmSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new DmmSource(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        var source = new DmmSource(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
