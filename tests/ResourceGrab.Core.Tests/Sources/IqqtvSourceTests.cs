using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class IqqtvSourceTests
{
    private const string SearchUrl = "https://iqqtv.com/vsearch?keyword=SNOS-001";
    private const string DetailUrl = "https://iqqtv.com/video/snos001";

    private static VideoSourceRequest Request(string number = "SNOS-001", string? language = null) =>
        new(number, VideoContentKind.Chinese, language, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        [SearchUrl] = """
            <html><body><div class="list">
              <a href="/video/snos001"><h4>SNOS-001 中文字幕 熱夜</h4></a>
              <a href="/video/snos002"><h4>SNOS-002 タイトル2</h4></a>
            </div></body></html>
            """,
        [DetailUrl] = """
            <html><body>
              <h4 class="title">SNOS-001 中文字幕 熱夜</h4>
              <div class="video-cover"><img src="" data-src="//img.example.com/snos001.jpg" /></div>
              <p><strong>發行日期:</strong> 2024-03-15</p>
              <p><strong>片長:</strong> 120 分鐘</p>
              <p><strong>演員:</strong> <a href="#">佐藤花子</a> <a href="#">鈴木一郎</a></p>
              <p><strong>標籤:</strong> <a href="#">巨乳</a> <a href="#">中文字幕</a></p>
              <p><strong>片商:</strong> <a href="#">SUNMEDIA</a></p>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_MatchingNumber_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new IqqtvSource(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("iqqtv", result.SourceId);
        Assert.Equal("SNOS-001 中文字幕 熱夜", meta.Title);
        Assert.Equal("https://img.example.com/snos001.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2024, 3, 15), meta.ReleaseDate);
        Assert.Equal(120, meta.RuntimeMinutes);
        Assert.Equal("SUNMEDIA", meta.Studio);
        Assert.Contains("佐藤花子", meta.Actors);
        Assert.Contains("巨乳", meta.Tags);
        Assert.True(meta.HasChineseSubtitle);   // 标签含"中文字幕"
        Assert.True(meta.SourceUrls.ContainsKey("iqqtv"));
    }

    [Fact]
    public async Task FetchAsync_LanguageBuildsPathPrefix()
    {
        // request.Language 非空 → /zh_cn/ 前缀拼进搜索路径
        var fetcher = new FakeFetcher(Pages());
        var source = new IqqtvSource(fetcher);
        var searchUrl = "https://iqqtv.com/zh_cn/vsearch?keyword=SNOS-001";

        await source.FetchAsync(Request(language: "zh-CN"), CancellationToken.None);

        Assert.Equal(searchUrl, fetcher.Requested[0]);
    }

    [Fact]
    public async Task FetchAsync_NoMatchingCard_ReturnsNoMatch()
    {
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div class='list'></div></body></html>";
        var source = new IqqtvSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_WrongNumberInCard_ReturnsNoMatch()
    {
        // 搜索结果只命中 SNOS-002，目标 SNOS-001 不应被误匹配
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div class='list'><a href='/video/snos002'><h4>SNOS-002 タイトル</h4></a></div></body></html>";
        var source = new IqqtvSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_NoChineseSubtitleMarker_HasChineseSubtitleFalse()
    {
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div class='list'><a href='/video/snos001'><h4>SNOS-001 無字幕</h4></a></div></body></html>";
        pages[DetailUrl] = """
            <html><body>
              <h4 class="title">SNOS-001 無字幕</h4>
              <div class="video-cover"><img src="/c/snos001.jpg" /></div>
              <p><strong>標籤:</strong> <a href="#">巨乳</a></p>
            </body></html>
            """;
        var source = new IqqtvSource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        Assert.False(result.Metadata!.HasChineseSubtitle);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new IqqtvSource(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        var source = new IqqtvSource(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
