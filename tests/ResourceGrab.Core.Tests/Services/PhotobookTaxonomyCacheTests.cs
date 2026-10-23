using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Services;

// 写真维度条目缓存测试：落盘往返、按「源 + 维度」隔离、
// 以及「没有文件 / 内容损坏 / schema 版本不符」一律退化为「没有缓存」而不是抛异常。

public sealed class PhotobookTaxonomyCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rg-taxonomy-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private static List<TaxonomyItem> Sample() =>
    [
        new("123", "标签甲", 45),
        new("456", "标签乙"),
    ];

    [Fact]
    public async Task SaveThenLoad_RoundTripsItems()
    {
        var cache = new PhotobookTaxonomyCache(_dir);

        await cache.SaveAsync("mzt", TaxonomyKind.Tag, Sample());
        var snapshot = await cache.LoadAsync("mzt", TaxonomyKind.Tag);

        Assert.NotNull(snapshot);
        Assert.Equal("mzt", snapshot!.SourceId);
        Assert.Equal(TaxonomyKind.Tag, snapshot.Kind);
        Assert.Equal(2, snapshot.Items.Count);
        Assert.Equal("123", snapshot.Items[0].Id);
        Assert.Equal("标签甲", snapshot.Items[0].Name);
        Assert.Equal(45, snapshot.Items[0].Count!.Value);
        Assert.Null(snapshot.Items[1].Count);
        Assert.Equal("456", snapshot.Items[1].Id);
        Assert.True(DateTimeOffset.Now - snapshot.SavedAt < TimeSpan.FromMinutes(5),
            "写入时间应当是刚刚");
    }

    [Fact]
    public async Task LoadAsync_WithoutCacheFile_ReturnsNull()
    {
        var cache = new PhotobookTaxonomyCache(_dir);

        Assert.Null(await cache.LoadAsync("mzt", TaxonomyKind.Tag));
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_ReturnsNullInsteadOfThrowing()
    {
        var cache = new PhotobookTaxonomyCache(_dir);
        await cache.SaveAsync("mzt", TaxonomyKind.Tag, Sample());

        var path = Directory.GetFiles(_dir, "*.json").Single();
        File.WriteAllText(path, "{ 这不是合法 JSON");

        Assert.Null(await cache.LoadAsync("mzt", TaxonomyKind.Tag));
    }

    [Fact]
    public async Task LoadAsync_SchemaMismatch_ReturnsNull()
    {
        var cache = new PhotobookTaxonomyCache(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mzt_tag.json"),
            """{"schema":"v0","sourceId":"mzt","kind":"Tag","savedAt":"2026-01-01T00:00:00+08:00","items":[]}""");

        Assert.Null(await cache.LoadAsync("mzt", TaxonomyKind.Tag));
    }

    [Fact]
    public async Task Save_IsolatesBySourceAndKind()
    {
        var cache = new PhotobookTaxonomyCache(_dir);
        await cache.SaveAsync("mzt", TaxonomyKind.Tag, Sample());

        Assert.Null(await cache.LoadAsync("lovecutes", TaxonomyKind.Tag));
        Assert.Null(await cache.LoadAsync("mzt", TaxonomyKind.Category));
        Assert.NotNull(await cache.LoadAsync("mzt", TaxonomyKind.Tag));
    }

    [Fact]
    public async Task Save_OverwritesPreviousContent_AndLeavesNoTempFile()
    {
        var cache = new PhotobookTaxonomyCache(_dir);
        await cache.SaveAsync("mzt", TaxonomyKind.Tag, Sample());

        var updated = new List<TaxonomyItem> { new("999", "新标签", 3) };
        await cache.SaveAsync("mzt", TaxonomyKind.Tag, updated);

        var snapshot = await cache.LoadAsync("mzt", TaxonomyKind.Tag);
        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Items);
        Assert.Equal("999", snapshot.Items[0].Id);

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task SaveThenLoad_WithEmptyList_StillReturnsSnapshot()
    {
        var cache = new PhotobookTaxonomyCache(_dir);

        await cache.SaveAsync("mzt", TaxonomyKind.Tag, []);
        var snapshot = await cache.LoadAsync("mzt", TaxonomyKind.Tag);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot!.Items);
    }

    [Fact]
    public async Task SaveThenLoad_WithUnusualSourceId_RoundTrips()
    {
        var cache = new PhotobookTaxonomyCache(_dir);

        // 源 Id 含非法文件名字符时也要能正确读回（内部做了文件名净化）
        await cache.SaveAsync("we/ird:id", TaxonomyKind.Coser, Sample());
        var snapshot = await cache.LoadAsync("we/ird:id", TaxonomyKind.Coser);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Items.Count);
    }
}
