using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class Jav321SourceTests
{
    private const string SearchUrl = "https://www.jav321.com/search";
    private const string DetailUrl = "https://www.jav321.com/video/snos001";

    private static VideoSourceRequest Request(string number = "SNOS-001") =>
        new(number, VideoContentKind.Censored, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        // 搜索为 POST uname=...，FakeFetcher 与 GET 同表驱动
        [SearchUrl] = """
            <html><body><div class="videos">
              <div class="video"><a href="./video/snos001"><div class="id">SNOS-001</div><div class="title">SNOS-001 サンプル</div></a></div>
              <div class="video"><a href="./video/snos002"><div class="id">SNOS-002</div><div class="title">SNOS-002 タイトル2</div></a></div>
            </div></body></html>
            """,
        [DetailUrl] = """
            <html><body><div class="panel-body"><div class="row">
              <div class="col-md-3"><img src="/package/snos001pl.jpg" /></div>
              <div class="col-md-9">
                <h3>SNOS-001 サンプルタイトルテスト</h3>
                <p><b>出演者:</b> <a href="#">佐藤花子</a> <a href="#">鈴木一郎</a></p>
                <p><b>メーカー:</b> <a href="#">SUNMEDIA</a></p>
                <p><b>発売日:</b> 2024-03-15</p>
                <p><b>シリーズ:</b> <a href="#">サンプルシリーズ</a></p>
              </div>
            </div></div></body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_MatchingNumber_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new Jav321Source(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("jav321", result.SourceId);
        Assert.Equal("SNOS-001 サンプルタイトルテスト", meta.Title);
        Assert.Equal("https://www.jav321.com/package/snos001pl.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2024, 3, 15), meta.ReleaseDate);
        Assert.Equal("SUNMEDIA", meta.Studio);
        Assert.Equal("サンプルシリーズ", meta.Series);
        Assert.Contains("佐藤花子", meta.Actors);
        Assert.Contains("鈴木一郎", meta.Actors);
        Assert.True(meta.SourceUrls.ContainsKey("jav321"));
        Assert.Equal(DetailUrl, meta.SourceUrls["jav321"]);
    }

    [Fact]
    public async Task FetchAsync_SearchPostsForm()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new Jav321Source(fetcher);

        await source.FetchAsync(Request(), CancellationToken.None);

        // 搜索必须是 POST uname 表单到 /search
        Assert.Equal(SearchUrl, fetcher.Requested[0]);
    }

    [Fact]
    public async Task FetchAsync_NoMatchingCard_ReturnsNoMatch()
    {
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div class='videos'></div></body></html>";
        var source = new Jav321Source(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_WrongNumberInCard_ReturnsNoMatch()
    {
        // 搜索结果只命中 SNOS-002，目标 SNOS-001 不应被误匹配
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div class='videos'><div class='video'><a href='./video/x'><div class='id'>SNOS-002</div></a></div></div></body></html>";
        var source = new Jav321Source(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_DirectDetailPage_ParsesWithoutDetailRequest()
    {
        // 单命中时站点 302 直接返回详情页 body
        var pages = Pages();
        pages[SearchUrl] = pages[DetailUrl];
        var fetcher = new FakeFetcher(pages);
        var source = new Jav321Source(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        Assert.Equal("SNOS-001 サンプルタイトルテスト", result.Metadata!.Title);
        Assert.Equal(SearchUrl, fetcher.Requested.Single());
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new Jav321Source(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        var source = new Jav321Source(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
