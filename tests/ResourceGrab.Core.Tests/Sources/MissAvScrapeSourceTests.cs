using System.Net;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Fetching;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using ResourceGrab.Core.Sources.VideoSources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

// MissAV 刮削源测试：用 StubHttpHandler 提供搜索页/详情页 HTML，全程离线。
// 注意：不测试 Blocked 路径 —— MissAvSource 在 403/拦截时会触发真实 curl 子进程兜底。

public class MissAvScrapeSourceTests
{
    private const string SearchUrl = "https://missav.ws/search/SNOS-001";
    private const string DetailUrl = "https://missav.ws/snos-001";

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _pages;
        public StubHttpHandler(Dictionary<string, string> pages) => _pages = pages;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            return Task.FromResult(_pages.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static VideoSourceRequest Request(string number = "SNOS-001") =>
        new(number, VideoContentKind.Censored, null, null, new VideoSourceFetchOptions());

    private static MissAvScrapeSource CreateSource(Dictionary<string, string> pages)
    {
        var http = new HttpClient(new StubHttpHandler(pages));
        var online = new MissAvSource(http, NullLogger.Instance);
        return new MissAvScrapeSource(new StubFetcher(), online);
    }

    /// <summary>刮削源不直接用 fetcher（走 MissAvSource 自己的 HttpClient），给个空实现即可。</summary>
    private sealed class StubFetcher : IResilientFetcher
    {
        public Task<FetchResult> GetAsync(string url, FetchOptions? options, CancellationToken ct)
            => Task.FromResult(new FetchResult(404, "", FetchTier.HttpClient));
        public Task<FetchResult> PostAsync(string url, string contentType, string body, FetchOptions? options, CancellationToken ct)
            => Task.FromResult(new FetchResult(404, "", FetchTier.HttpClient));
    }

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();
        public string LogDirectory => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "missav-test-logs");
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private static Dictionary<string, string> Pages() => new(StringComparer.Ordinal)
    {
        [SearchUrl] = """
            <html><body>
              <div class="thumbnail"><a href="/snos-001"><img src="//ss.example.com/snos-001.jpg" alt="SNOS-001 サンプル動画"></a></div>
              <div class="thumbnail"><a href="/snos-002"><img src="//ss.example.com/snos-002.jpg" alt="SNOS-002 別の動画"></a></div>
            </body></html>
            """,
        [DetailUrl] = """
            <html><head>
              <meta property="og:title" content="SNOS-001 サンプルタイトル | MissAV" />
              <meta property="og:image" content="https://ss.example.com/snos-001-cover.jpg" />
              <meta property="og:description" content="テスト説明文" />
              <script type="application/ld+json">{"@type":"VideoObject","duration":"PT1H2M3S","uploadDate":"2024-03-15T00:00:00+09:00"}</script>
            </head><body>
              <a href="/actresses/sato-hanako">俳優リンク</a>
              <a href="/tags/big">タグリンク</a>
            </body></html>
            """,
    };

    [Fact]
    public async Task FetchAsync_SearchHit_ParsesDetailMetadata()
    {
        var source = CreateSource(Pages());

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.Success, result.Outcome);
        var meta = result.Metadata!;
        Assert.Equal("missav", result.SourceId);
        Assert.Equal("SNOS-001 サンプルタイトル", meta.Title);   // | MissAV 后缀已剥除
        Assert.Equal("https://ss.example.com/snos-001-cover.jpg", meta.CoverUrl);
        // 离线词典繁转简：説明文 → 说明文
        Assert.Equal("テスト说明文", meta.Description);
        Assert.Contains("sato hanako", meta.Actors);
        Assert.Contains("big", meta.Tags);
        Assert.Equal(62, meta.RuntimeMinutes);                   // PT1H2M3S
        Assert.Equal(new DateTime(2024, 3, 15), meta.ReleaseDate);
        Assert.True(meta.SourceUrls.ContainsKey("missav"));
    }

    [Fact]
    public async Task FetchAsync_NoMatchingCard_ReturnsNoMatch()
    {
        var pages = Pages();
        pages[SearchUrl] = "<html><body><div class='thumbnail'><a href='/snos-002'><img alt='SNOS-002 別の動画'></a></div></body></html>";
        var source = CreateSource(pages);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(VideoSourceOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task FetchAsync_SearchPageMissing_HttpError()
    {
        var source = CreateSource([]);

        var result = await source.FetchAsync(Request(), CancellationToken.None);

        // 所有镜像均 404：MissAvSource 内部兜底到最近更新页也 404，异常翻译为 HttpError
        Assert.Equal(VideoSourceOutcome.HttpError, result.Outcome);
    }
}
