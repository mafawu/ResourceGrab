using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class FreejavbtSourceTests
{
    private const string SearchUrl = "https://freejavbt.com/search?q=SONE-001";
    private const string DetailUrl = "https://freejavbt.com/watch/sone-001";

    private static VideoSourceRequest Request(string number = "SONE-001") =>
        new(number, VideoContentKind.Chinese, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        [SearchUrl] = """
            <html><body><div class="row">
              <div class="card"><a href="/watch/sone-001"><h5 class="card-title">SONE-001 中文字幕</h5></a></div>
              <div class="card"><a href="/watch/sone-002"><h5 class="card-title">SONE-002</h5></a></div>
            </div></body></html>
            """,
        [DetailUrl] = """
            <html><body>
              <h5 class="card-title">SONE-001 中文字幕 サンプル</h5>
              <img class="card-img-top" src="" data-src="/covers/sone001.jpg" />
              <p><strong>發行日期:</strong> 2024-05-01</p>
              <p><strong>片長:</strong> 95 分鐘</p>
              <p><strong>演員:</strong> <a href="#">星乃莉子</a></p>
              <div><a class="badge" href="#">中文字幕</a><a class="badge" href="#">巨乳</a></div>
              <a href="magnet:?xt=urn:btih:ABCDEF0123456789ABCDEF0123456789">下載</a>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_MatchingNumber_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new FreejavbtSource(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("freejavbt", result.SourceId);
        Assert.Equal("SONE-001 中文字幕 サンプル", meta.Title);
        Assert.Equal("https://freejavbt.com/covers/sone001.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2024, 5, 1), meta.ReleaseDate);
        Assert.Equal(95, meta.RuntimeMinutes);
        Assert.Contains("星乃莉子", meta.Actors);
        Assert.Contains("巨乳", meta.Tags);
        Assert.True(meta.HasMagnet);            // 详情页命中 magnet 正则
        Assert.True(meta.HasChineseSubtitle);   // 页面含"中文字幕"
        Assert.True(meta.SourceUrls.ContainsKey("freejavbt"));
    }

    [Fact]
    public async Task FetchAsync_NoMagnetOnPage_HasMagnetFalse()
    {
        var pages = Pages();
        pages[DetailUrl] = """
            <html><body>
              <h5 class="card-title">SONE-001</h5>
              <img class="card-img-top" src="/covers/sone001.jpg" />
              <p><strong>發行日期:</strong> 2024-05-01</p>
            </body></html>
            """;
        var source = new FreejavbtSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        Assert.False(result.Metadata!.HasMagnet);
    }

    [Fact]
    public async Task FetchAsync_NoMatchingCard_ReturnsNoMatch()
    {
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div class='row'></div></body></html>";
        var source = new FreejavbtSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_WrongNumberInCard_ReturnsNoMatch()
    {
        // 搜索结果只命中 SONE-002，目标 SONE-001 不应被误匹配（SONE-002 含数字后缀差异）
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div class='row'><div class='card'><a href='/watch/sone-002'><h5 class='card-title'>SONE-002</h5></a></div></div></body></html>";
        var source = new FreejavbtSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new FreejavbtSource(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        var source = new FreejavbtSource(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
