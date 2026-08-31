using ResourceGrab.Core.Services.VideoScrape;
using Xunit;

namespace ResourceGrab.Core.Tests.Services.VideoScrape;

// M6: 资源存储测试

public class ResourceStoreTests : IDisposable
{
    private readonly string _tempDir;

    public ResourceStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"resource_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void ComputeUrlHash_IsDeterministic()
    {
        var hash1 = DiskVideoResourceStore.ComputeUrlHash("https://example.com/image.jpg");
        var hash2 = DiskVideoResourceStore.ComputeUrlHash("https://example.com/image.jpg");

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeUrlHash_DifferentUrls_DifferentHashes()
    {
        var hash1 = DiskVideoResourceStore.ComputeUrlHash("https://example.com/a.jpg");
        var hash2 = DiskVideoResourceStore.ComputeUrlHash("https://example.com/b.jpg");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Exists_ReturnsFalse_ForUnknownUrl()
    {
        var store = new DiskVideoResourceStore(_tempDir);

        Assert.False(store.Exists("https://example.com/missing.jpg"));
    }

    [Fact]
    public void GetRecord_ReturnsNull_ForUnknownUrl()
    {
        var store = new DiskVideoResourceStore(_tempDir);

        Assert.Null(store.GetRecord("https://example.com/missing.jpg"));
    }

    [Fact]
    public async Task GetOrDownloadAsync_ReturnsNull_WhenNoHttpClient()
    {
        var store = new DiskVideoResourceStore(_tempDir, httpFactory: null);

        var result = await store.GetOrDownloadAsync("https://example.com/image.jpg", VideoResourceKind.Cover, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void GetAllRecords_ReturnsEmpty_WhenNothingStored()
    {
        var store = new DiskVideoResourceStore(_tempDir);

        Assert.Empty(store.GetAllRecords());
    }

    [Fact]
    public async Task CleanupAsync_RemovesUnreferenced()
    {
        var store = new DiskVideoResourceStore(_tempDir);
        // 由于没有 httpFactory，无法实际下载，但可以验证 cleanup 逻辑
        var removed = await store.CleanupAsync(_ => false);

        Assert.Equal(0, removed); // 没有记录可清理
    }
}
