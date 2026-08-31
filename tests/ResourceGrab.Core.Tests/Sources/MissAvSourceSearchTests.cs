using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Sources.VideoSources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

// MissAV 在线源搜索卡片解析测试：夹具取自 missav.live 真实搜索页结构。
// 关键回归：2026-08 起视频详情 URL 带 /dmXX/ 路径前缀（如 /dm118/ssis-971），
// 旧版按 "/dm数字" 排除会把全部结果过滤成 0 条。

public class MissAvSourceSearchTests
{
    private static MissAvSource CreateSource() =>
        new(new HttpClient(new StubHandler()), NullLogger.Instance);

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();
        public string LogDirectory => Path.Combine(Path.GetTempPath(), "missav-test-logs");
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    [Fact]
    public void ParseSearchCards_WithDmPrefixUrls_ReturnsVideoCards()
    {
        var html = File.ReadAllText(Path.Combine("Fixtures", "missav-search.html"));
        var items = CreateSource().ParseSearchCards(html);

        Assert.Collection(items,
            item =>
            {
                Assert.Equal("ssis-960-uncensored-leak", item.Id);
                Assert.Equal("SSIS-960", item.Number);
                Assert.Equal("無碼影片", item.KindLabel);
                Assert.Equal("https://fourhoi.com/ssis-960-uncensored-leak/cover-t.jpg", item.CoverUrl);
                Assert.Equal("1:58:00", item.DurationText);
                // 标题链接末段 " - " 后的日文演员名应进标签
                Assert.Contains("桃果あかり", item.Tags);
            },
            item =>
            {
                Assert.Equal("ssis-971", item.Id);
                Assert.Equal("SSIS-971", item.Number);
                Assert.Equal("有碼影片", item.KindLabel);
            });
    }

    [Fact]
    public void ParseSearchCards_ExcludesNonVideoPromoLinks()
    {
        var html = File.ReadAllText(Path.Combine("Fixtures", "missav-search.html"));
        var items = CreateSource().ParseSearchCards(html);

        // /dm1004/maan 是推广位：末段无番号形式，不应混进结果
        Assert.DoesNotContain(items, i => i.Id.Equals("maan", StringComparison.OrdinalIgnoreCase));
        Assert.All(items, i => Assert.Matches(@"-\d{2,}", i.Id));
    }

    [Theory]
    [InlineData("https://missav.live/dm118/ssis-971", true)]
    [InlineData("https://missav.live/dm63/ssis-960-uncensored-leak", true)]
    [InlineData("https://missav.live/dm1004/maan", false)]
    [InlineData("https://missav.live/cn/search/SSIS", false)]
    [InlineData("https://missav.live/dm539/cn/new", false)]
    public void SearchPageHrefClassification_MatchesVideoIdPattern(string href, bool isVideo)
    {
        var segment = href.Split('/').Last();
        Assert.Equal(isVideo, System.Text.RegularExpressions.Regex.IsMatch(segment, @"-\d{2,}"));
    }
}
