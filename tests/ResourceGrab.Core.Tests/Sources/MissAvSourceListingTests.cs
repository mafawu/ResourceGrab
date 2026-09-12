using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Sources.VideoSources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

// MissAV 榜单（在线推荐）测试：夹具取自 missav.live 真实"今日热门"列表页。
// 榜单页与搜索页同为 thumbnail 卡片网格，ParseSearchCards 应无需改动即可解析。

public class MissAvSourceListingTests
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
    public void ParseSearchCards_OnTodayHotListing_ExtractsVideoCards()
    {
        var html = File.ReadAllText(Path.Combine("Fixtures", "missav-today-hot.html"));
        var items = CreateSource().ParseSearchCards(html);

        // 今日热门每天变化，只断言结构性特征：条数足够、番号/封面/标题齐全
        Assert.True(items.Count >= 5, $"今日热门应解析出多条卡片，实际 {items.Count} 条");
        Assert.All(items, i =>
        {
            Assert.Matches(@"-\d{2,}", i.Id);
            Assert.StartsWith("http", i.CoverUrl);
            Assert.NotEmpty(i.Title);
        });
    }

    [Fact]
    public async Task GetListingAsync_UnknownKind_ReturnsNull()
    {
        var result = await CreateSource().GetListingAsync((VideoListingKind)999, 1);
        Assert.Null(result);
    }
}
