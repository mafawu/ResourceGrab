using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape;
using Xunit;

namespace ResourceGrab.Core.Tests.Services.VideoScrape;

// M4: 磁盘 JSON 快照缓存测试

public class SnapshotCacheTests : IDisposable
{
    private readonly string _tempDir;

    public SnapshotCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cache_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_ReturnsSnapshot()
    {
        var cache = new DiskJsonSnapshotCache(_tempDir);
        var meta = new VideoScrapeMetadata { Source = "javbus", Title = "Test Title" };
        var snapshot = new VideoSourceSnapshot
        {
            Number = "SSIS-405",
            SourceId = "javbus",
            SchemaVersion = "v1",
            Metadata = meta
        };

        await cache.SetAsync(snapshot);
        var result = await cache.GetAsync("SSIS-405", "javbus");

        Assert.NotNull(result);
        Assert.Equal("javbus", result!.SourceId);
        Assert.Equal("Test Title", result.Metadata.Title);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotExists()
    {
        var cache = new DiskJsonSnapshotCache(_tempDir);

        var result = await cache.GetAsync("SSIS-999", "javbus");

        Assert.Null(result);
    }

    [Fact]
    public async Task HasAsync_ReturnsTrue_AfterSet()
    {
        var cache = new DiskJsonSnapshotCache(_tempDir);
        var snapshot = new VideoSourceSnapshot
        {
            Number = "SSIS-405",
            SourceId = "javbus",
            SchemaVersion = "v1",
            Metadata = new VideoScrapeMetadata { Source = "javbus" }
        };

        await cache.SetAsync(snapshot);

        Assert.True(await cache.HasAsync("SSIS-405", "javbus"));
        Assert.False(await cache.HasAsync("SSIS-405", "javdb"));
    }

    [Fact]
    public async Task ClearAsync_RemovesAllForNumber()
    {
        var cache = new DiskJsonSnapshotCache(_tempDir);
        await cache.SetAsync(new VideoSourceSnapshot
        {
            Number = "SSIS-405", SourceId = "javbus", SchemaVersion = "v1",
            Metadata = new VideoScrapeMetadata { Source = "javbus" }
        });
        await cache.SetAsync(new VideoSourceSnapshot
        {
            Number = "SSIS-405", SourceId = "javdb", SchemaVersion = "v1",
            Metadata = new VideoScrapeMetadata { Source = "javdb" }
        });

        await cache.ClearAsync("SSIS-405");

        Assert.False(await cache.HasAsync("SSIS-405", "javbus"));
        Assert.False(await cache.HasAsync("SSIS-405", "javdb"));
    }

    [Fact]
    public async Task ClearAllAsync_RemovesEverything()
    {
        var cache = new DiskJsonSnapshotCache(_tempDir);
        await cache.SetAsync(new VideoSourceSnapshot
        {
            Number = "SSIS-405", SourceId = "javbus", SchemaVersion = "v1",
            Metadata = new VideoScrapeMetadata { Source = "javbus" }
        });

        await cache.ClearAllAsync();

        Assert.False(await cache.HasAsync("SSIS-405", "javbus"));
    }

    [Fact]
    public async Task GetCachedSourceIdsAsync_ReturnsSourceIds()
    {
        var cache = new DiskJsonSnapshotCache(_tempDir);
        await cache.SetAsync(new VideoSourceSnapshot
        {
            Number = "SSIS-405", SourceId = "javbus", SchemaVersion = "v1",
            Metadata = new VideoScrapeMetadata { Source = "javbus" }
        });
        await cache.SetAsync(new VideoSourceSnapshot
        {
            Number = "SSIS-405", SourceId = "javdb", SchemaVersion = "v1",
            Metadata = new VideoScrapeMetadata { Source = "javdb" }
        });

        var ids = await cache.GetCachedSourceIdsAsync("SSIS-405");

        Assert.Equal(2, ids.Count);
        Assert.Contains("javbus", ids);
        Assert.Contains("javdb", ids);
    }

    [Fact]
    public async Task SetAsync_WithLanguage_UsesLanguageSuffix()
    {
        var cache = new DiskJsonSnapshotCache(_tempDir);
        var snapshot = new VideoSourceSnapshot
        {
            Number = "SSIS-405",
            SourceId = "iqqtv",
            Language = "zh-CN",
            SchemaVersion = "v1",
            Metadata = new VideoScrapeMetadata { Source = "iqqtv" }
        };

        await cache.SetAsync(snapshot);
        var result = await cache.GetAsync("SSIS-405", "iqqtv", "zh-CN");

        Assert.NotNull(result);
        Assert.Equal("zh-CN", result!.Language);
    }
}
