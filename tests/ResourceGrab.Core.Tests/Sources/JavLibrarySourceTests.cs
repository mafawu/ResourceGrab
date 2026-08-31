using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class JavLibrarySourceTests
{
    private const string SearchUrl = "https://www.javlibrary.com/cn/search.php?keyword=SNOS-001&f=all";
    private const string DetailUrl = "https://www.javlibrary.com/cn/?v=javmeabcde1";

    private static VideoSourceRequest Request(string number = "SNOS-001") =>
        new(number, VideoContentKind.Censored, null, null, new VideoSourceFetchOptions());

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        [SearchUrl] = """
            <html><body><div class="videos">
              <div class="video"><a href="./?v=javmeabcde1"><div class="id">SNOS-001</div><div class="title">SNOS-001 サンプル</div></a></div>
              <div class="video"><a href="./?v=javmeabcde2"><div class="id">SNOS-002</div><div class="title">SNOS-002 タイトル2</div></a></div>
            </div></body></html>
            """,
        [DetailUrl] = """
            <html><body>
            <div id="video_title"><h3><a id="video_title_a">SNOS-001 サンプルタイトルテスト</a></h3></div>
            <div id="video_jacket"><img src="//pics.example.com/snos001pl.jpg" /></div>
            <div id="video_info" class="info">
              <div id="video_id" class="item"><table><tr><td class="header">ID:</td><td class="text">SNOS-001</td></tr></table></div>
              <div id="video_date" class="item"><table><tr><td class="header">発売日:</td><td class="text">2024-03-15</td></tr></table></div>
              <div id="video_length" class="item"><table><tr><td class="header">長さ:</td><td class="text"><span class="text">120</span>分鐘</td></tr></table></div>
              <div id="video_director" class="item"><table><tr><td class="header">導演:</td><td class="text"><a href="#">田中太郎</a></td></tr></table></div>
              <div id="video_maker" class="item"><table><tr><td class="header">製作商:</td><td class="text"><a href="#">SUNMEDIA</a></td></tr></table></div>
              <div id="video_label" class="item"><table><tr><td class="header">發行商:</td><td class="text"><a href="#">SNOS</a></td></tr></table></div>
              <div id="video_genres" class="item"><table><tr><td class="header">類別:</td><td class="text"><a href="#">巨乳</a> <a href="#">中文字幕</a></td></tr></table></div>
              <div id="video_cast" class="item"><table><tr><td class="header">女優:</td><td class="text"><a href="#">佐藤花子</a> <a href="#">鈴木一郎</a></td></tr></table></div>
            </div>
            <div id="video_review" class="item"><table><tr><td class="header">用戶評分</td>
              <td><span class="score"><span class="value">4.63</span><a href="#">(123)</a></span></td></tr></table></div>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_MatchingNumber_ReturnsSuccessWithMetadata()
    {
        var fetcher = new FakeFetcher(Pages());
        var source = new JavLibrarySource(fetcher);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("javlibrary", result.SourceId);
        Assert.Equal("サンプルタイトルテスト", meta.Title);
        Assert.Equal("https://pics.example.com/snos001pl.jpg", meta.CoverUrl);
        Assert.Equal(new DateTime(2024, 3, 15), meta.ReleaseDate);
        Assert.Equal(120, meta.RuntimeMinutes);
        Assert.Equal("田中太郎", meta.Director);
        Assert.Equal("SUNMEDIA", meta.Studio);
        Assert.Equal("SNOS", meta.Publisher);
        Assert.Contains("巨乳", meta.Tags);
        Assert.Contains("佐藤花子", meta.Actors);
        Assert.Equal(9.26, meta.Score, precision: 2);   // 4.63 * 2
        Assert.Equal(123, meta.ScoreVotes);
        Assert.True(meta.SourceUrls.ContainsKey("javlibrary"));
    }

    [Fact]
    public async Task FetchAsync_NoMatchingCard_ReturnsNoMatch()
    {
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div class='videos'></div></body></html>";
        var source = new JavLibrarySource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_WrongNumberInCard_ReturnsNoMatch()
    {
        // 搜索结果只命中 SNOS-002，目标 SNOS-001 不应被误匹配
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div class='videos'><div class='video'><a href='./?v=x'><div class='id'>SNOS-002</div></a></div></div></body></html>";
        var source = new JavLibrarySource(new FakeFetcher(pages));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_CloudflareChallenge_ReturnsBlocked()
    {
        var source = new JavLibrarySource(new FakeFetcher([], forceStatus: 403));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_MissingPage_ReturnsHttpError()
    {
        var source = new JavLibrarySource(new FakeFetcher([]));

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}

public class HtmlScrapeSourceBaseTests
{
    [Theory]
    [InlineData("SNOS-001 サンプルタイトル", "SNOS-001", true)]
    [InlineData("snos001 サンプル", "SNOS-001", true)]       // 分隔符可省略
    [InlineData("SNOS-1 予告編", "SNOS-001", true)]          // 前导零可省略
    [InlineData("SNOS-123 タイトル", "SNOS-1", false)]        // 数字不能前缀误匹配
    [InlineData("ASNOS-001 タイトル", "SNOS-001", false)]     // 前缀前不能有字母
    [InlineData("", "SNOS-001", false)]
    public void MatchesNumber_ExactRules(string text, string number, bool expected)
    {
        Assert.Equal(expected, HtmlScrapeSourceBase.MatchesNumber(text, number));
    }

    [Theory]
    [InlineData("SNOS-001", "SNOS", "1")]
    [InlineData("FC2-PPV-3175496", "FC2-PPV", "3175496")]
    [InlineData("MIDV00123", "MIDV", "123")]
    public void SplitNumber_NormalizesPrefixAndDigits(string number, string prefix, string digits)
    {
        var (p, d) = HtmlScrapeSourceBase.SplitNumber(number);
        Assert.Equal(prefix, p);
        Assert.Equal(digits, d);
    }
}
