using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Services;

public class AuthorWorksServiceTests
{
    [Theory]
    [InlineData("Alice", "Alice", true)]
    [InlineData("alice", "ALICE", true)]
    [InlineData("Alice", "Alice、Bob", true)]
    [InlineData(" Alice ", "alice", true)]
    [InlineData("Alice", "Bob", false)]
    [InlineData("Alice", "Alicia", false)]
    [InlineData("", "Alice", false)]
    [InlineData("Alice", "", false)]
    public void IsAuthorMatch_MatchesNormalized(string query, string field, bool expected)
    {
        Assert.Equal(expected, AuthorWorksService.IsAuthorMatch(query, field));
    }

    [Theory]
    [InlineData("[汉化组] 标题", true)]
    [InlineData("标题（中文）", true)]
    [InlineData("纯中文标题测试", true)]
    [InlineData("日本語タイトル", false)]
    [InlineData("漢字とひらがな混合", false)]
    [InlineData("English Title", false)]
    [InlineData("", false)]
    public void HasChineseEdition_DetectsChinese(string title, bool expected)
    {
        Assert.Equal(expected, AuthorWorksService.HasChineseEdition(title));
    }

    [Fact]
    public async Task SearchAuthorAsync_MergesAndDedups()
    {
        var jm = new FakeComicSource("jm", "禁漫天堂", new[]
        {
            ("1", "Alice的本 [汉化]", "Alice"),
            ("2", "Alice的本 [汉化]", "Alice"), // 站内重复（不同 id 同标题）
            ("3", "Bob的本", "Bob"), // 非目标作者，应过滤
        });
        var wnacg = new FakeComicSource("wnacg", "绅士漫画", new[]
        {
            ("10", "Alice的本", "Alice"), // 跨源同名，去重（禁漫优先）
            ("11", "Alice短篇集", "Alice"),
        });
        var service = new AuthorWorksService([jm, wnacg]);

        var result = await service.SearchAuthorAsync("Alice");

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, i => Assert.Equal("Alice", i.Author));
        Assert.Equal("jm", result.Items[0].SourceId); // 禁漫优先
        Assert.Equal(2, result.DroppedDuplicates); // jm 站内 1 + 跨源 1
        Assert.True(result.Items.All(i => i.HasChinese));
        Assert.Empty(result.Errors);
    }

    private sealed class FakeComicSource(
        string id, string displayName, IEnumerable<(string Id, string Title, string Author)> items)
        : IComicSource
    {
        private readonly List<ComicSummary> _items = items
            .Select(t => new ComicSummary { Id = t.Id, Title = t.Title, Author = t.Author })
            .ToList();

        public ComicSourceInfo Info { get; } = new() { Id = id, DisplayName = displayName };

        public Task<SearchResult> SearchAsync(string keyword, int page, CancellationToken ct = default)
            => Task.FromResult(new SearchResult { Items = page == 1 ? _items : [], Total = _items.Count, TotalPages = 1 });

        public Task<ComicDetail> GetComicAsync(string comicId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ImagePage>> GetChapterImagesAsync(Chapter chapter, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
