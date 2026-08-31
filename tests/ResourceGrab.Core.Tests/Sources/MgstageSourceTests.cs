using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class MgstageSourceTests
{
    private const string SearchUrl = "https://www.mgstage.com/search/product/?keyword=SIRO-5000";
    private const string DetailUrl = "https://www.mgstage.com/product/detail/5000/";

    private static VideoSourceRequest Request(string number = "SIRO-5000") =>
        new(number, VideoContentKind.Amateur, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        [SearchUrl] = """
            <html><body>
              <a href="/product/detail/5000/">SIRO-5000 素人個人撮影テスト</a>
              <a href="/product/detail/5001/">SIRO-5001 别的条目</a>
            </body></html>
            """,
        [DetailUrl] = """
            <html>
            <head><meta property="og:image" content="https://image.mgstage.com/content/siro/5000/main.jpg" /></head>
            <body>
              <h1 class="tag">SIRO-5000 素人個人撮影テスト</h1>
              <img id="CenterPhotoImg" src="https://image.mgstage.com/content/siro/5000/main.jpg" />
              <table class="detail_data">
                <tr><th>品番：</th><td>SIRO-5000</td></tr>
                <tr><th>配信開始日：</th><td>2024-05-10</td></tr>
                <tr><th>収録時間：</th><td>130分</td></tr>
                <tr><th>出演：</th><td><a href="/search/product/?keyword=1000">花子</a> <a href="/search/product/?keyword=1001">-</a></td></tr>
                <tr><th>シリーズ：</th><td>素人個人撮影</td></tr>
                <tr><th>メーカー：</th><td><a href="#">SOD素人</a></td></tr>
                <tr><th>レーベル：</th><td><a href="#">SIR</a></td></tr>
                <tr><th>ジャンル：</th><td><a href="#">素人</a> <a href="#">巨乳</a></td></tr>
              </table>
              <div id="sample-photo"><img src="https://image.mgstage.com/content/siro/5000/jl-1.jpg" /></div>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_MatchingCard_ReturnsSuccessWithMetadata()
    {
        var source = new MgstageSource(new FakeFetcher(Pages()));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("mgstage", result.SourceId);
        Assert.Equal("素人個人撮影テスト", meta.Title);
        Assert.Equal("https://image.mgstage.com/content/siro/5000/main.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2024, 5, 10), meta.ReleaseDate);
        Assert.Equal(130, meta.RuntimeMinutes);
        Assert.Contains("花子", meta.Actors);
        Assert.DoesNotContain("-", meta.Actors);
        Assert.Equal("素人個人撮影", meta.Series);
        Assert.Equal("SOD素人", meta.Studio);
        Assert.Equal("SIR", meta.Publisher);
        Assert.Contains("素人", meta.Tags);
        Assert.Contains("巨乳", meta.Tags);
        Assert.Contains("https://image.mgstage.com/content/siro/5000/jl-1.jpg", meta.PreviewImageUrls);
        Assert.True(meta.SourceUrls.ContainsKey("mgstage"));
    }

    [Fact]
    public async Task FetchAsync_NoMatchingCard_ReturnsNoMatch()
    {
        // 搜索结果只有别的番号（SIRO-5001）→ 精确校验不通过 → NoMatch
        var pages = Pages();
        pages[SearchUrl] = "<html><body><a href='/product/detail/5001/'>SIRO-5001 别的条目</a></body></html>";
        var source = new MgstageSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_AgeGatePage_ReturnsNoMatch()
    {
        // adc=1 cookie 未生效时搜索被重定向到年龄确认页（无商品链接）→ NoMatch
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div class='age_gate'><a href='/agecheck/'>18歳以上ですか？</a></div></body></html>";
        var source = new MgstageSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new MgstageSource(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        var source = new MgstageSource(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
