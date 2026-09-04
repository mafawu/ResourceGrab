using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources.VideoSources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

// JavDB 在线源搜索卡片 / 磁力页解析测试。
// 夹具结构取自 javdb.com 真实页面：搜索结果 div.movie-list > div.item > a.box，
// 卡片内 .uid（番号）、.video-title（标题）、.score .value（评分 5 分制）。

public class JavDbSourceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();
        public string LogDirectory => Path.Combine(Path.GetTempPath(), "javdb-test-logs");
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    /// <summary>临时目录建 ConfigService（文件不存在时自动生成默认配置），避免污染测试后清理。</summary>
    private static JavDbSource CreateSource(string? cookie = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "javdb-test-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigService(Path.Combine(dir, "config.json"));
        if (!string.IsNullOrEmpty(cookie))
            config.Current.VideoScraping!.JavDbCookie = cookie;
        return new JavDbSource(new HttpClient(new StubHandler()), config, NullLogger.Instance);
    }

    [Fact]
    public void ParseSearchCards_ExtractsNumberTitleCoverAndScore()
    {
        // javdb.com 真实搜索页结构：.movie-list .item > a.box，内含 .uid / .video-title / img / .score .value
        const string html = """
            <html><body>
            <div class="movie-list">
              <div class="item">
                <a class="box" href="/v/abc123">
                  <div class="video-title">SSIS-405 中出し解禁</div>
                  <div class="uid">SSIS-405</div>
                  <img src="https://c0.jdbstatic.com/covers/405.jpg" />
                  <div class="score"><span class="value">4.3</span></div>
                </a>
              </div>
              <div class="item">
                <a class="box" href="/v/def456">
                  <div class="video-title">OTHER-999 测试</div>
                  <div class="uid">OTHER-999</div>
                  <img src="https://c0.jdbstatic.com/covers/999.jpg" />
                  <div class="score"><span class="value">3.9</span></div>
                </a>
              </div>
            </div>
            </body></html>
            """;

        var items = CreateSource().ParseSearchCards(html);

        Assert.Collection(items,
            item =>
            {
                Assert.Equal("/v/abc123", item.Id);
                Assert.Equal("SSIS-405", item.Number);
                Assert.Equal("SSIS-405 中出し解禁", item.Title);
                Assert.Equal("https://c0.jdbstatic.com/covers/405.jpg", item.CoverUrl);
                // JavDB 5 分制 → 转 10 分制展示（4.3 × 2 = 8.6）
                Assert.Equal("8.6", item.RatingText);
            },
            item =>
            {
                Assert.Equal("/v/def456", item.Id);
                Assert.Equal("OTHER-999", item.Number);
                Assert.Equal("7.8", item.RatingText);
            });
    }

    [Fact]
    public void ParseSearchCards_FallsBackToLegacyItemStructure()
    {
        // 兼容历史版 div.item a.box（无 .uid / .score 时仅取标题+链接）
        const string html = """
            <html><body>
            <div class="item"><a class="box" href="/v/405"><span class="video-title">SSIS-405 中出し解禁</span></a></div>
            <div class="item"><a class="box" href="/v/999"><span class="video-title">OTHER-999</span></a></div>
            </body></html>
            """;

        var items = CreateSource().ParseSearchCards(html);

        Assert.Equal(2, items.Count);
        Assert.Equal("/v/405", items[0].Id);
        Assert.Equal("SSIS-405 中出し解禁", items[0].Title);
        Assert.Equal("", items[0].Number);
        Assert.Equal("", items[0].RatingText);
    }

    [Fact]
    public void ParseSearchCards_DeduplicatesByNumber()
    {
        const string html = """
            <html><body>
            <div class="movie-list">
              <div class="item"><a class="box" href="/v/a1"><div class="uid">SSIS-405</div><div class="video-title">A</div></a></div>
              <div class="item"><a class="box" href="/v/a2"><div class="uid">SSIS-405</div><div class="video-title">B</div></a></div>
              <div class="item"><a class="box" href="/v/c1"><div class="uid">OTHER-999</div><div class="video-title">C</div></a></div>
            </div>
            </body></html>
            """;

        var items = CreateSource().ParseSearchCards(html);

        Assert.Equal(2, items.Count);
        Assert.Equal("SSIS-405", items[0].Number);
        Assert.Equal("OTHER-999", items[1].Number);
    }

    [Fact]
    public void ParseMagnets_ExtractsNameSizeDateUrl()
    {
        // JavDB /magnets 页表格：td.name + 磁力链接（a[href^="magnet:"]）+ 体积 + 日期
        const string html = """
            <html><body>
            <table><tbody>
              <tr>
                <td class="name">S1 NO.1 STYLE - SSIS-405.mkv 1080p</td>
                <td>2.5 GB</td>
                <td>2024-01-15</td>
                <td><a href="magnet:?xt=urn:btih:AAA111" class="copy">复制</a></td>
              </tr>
              <tr>
                <td class="name">SSIS-405.mp4 720p</td>
                <td>1.2 GB</td>
                <td>2024-01-16</td>
                <td><a href="magnet:?xt=urn:btih:BBB222" class="copy">复制</a></td>
              </tr>
            </tbody></table>
            </body></html>
            """;

        var magnets = CreateSource().ParseMagnets(html);

        Assert.Collection(magnets,
            m =>
            {
                Assert.Equal("S1 NO.1 STYLE - SSIS-405.mkv 1080p", m.Name);
                Assert.Equal("2.5 GB", m.Size);
                Assert.Equal("2024-01-15", m.Date);
                Assert.Equal("magnet:?xt=urn:btih:AAA111", m.Url);
            },
            m =>
            {
                Assert.Equal("SSIS-405.mp4 720p", m.Name);
                Assert.Equal("magnet:?xt=urn:btih:BBB222", m.Url);
            });
    }
}
