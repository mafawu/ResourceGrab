using ResourceGrab.Core.Services.VideoScrape;
using Xunit;

namespace ResourceGrab.Core.Tests.Services.VideoScrape;

// M12: 插件加载器测试

public class PluginTests : IDisposable
{
    private readonly string _tempDir;

    public PluginTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"plugin_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void LoadAll_ReturnsZero_WhenEmpty()
    {
        var loader = new PluginLoader(_tempDir);

        var count = loader.LoadAll();

        Assert.Equal(0, count);
        Assert.Empty(loader.Plugins);
    }

    [Fact]
    public void LoadPlugin_RejectsId_WithoutNamespace()
    {
        var loader = new PluginLoader(_tempDir);
        var manifest = new VideoScrapePluginManifest
        {
            Id = "nonspace",
            Name = "Test",
            EntryPoint = "test.dll",
            TypeName = "Test.TestSource"
        };

        var result = loader.LoadPlugin(manifest, _tempDir);

        Assert.False(result);
    }

    [Fact]
    public void LoadPlugin_AcceptsId_WithNamespace()
    {
        var loader = new PluginLoader(_tempDir);
        var manifest = new VideoScrapePluginManifest
        {
            Id = "example.custom-source",
            Name = "Test",
            EntryPoint = "nonexistent.dll",
            TypeName = "Test.TestSource"
        };

        // 由于 DLL 不存在，会失败但接受 ID 格式
        var result = loader.LoadPlugin(manifest, _tempDir);

        Assert.False(result);
        Assert.Equal(PluginLoadStatus.Failed, loader.Plugins["example.custom-source"].Status);
    }

    [Fact]
    public void LoadPlugin_SkipsDisabled()
    {
        var loader = new PluginLoader(_tempDir);
        var manifest = new VideoScrapePluginManifest
        {
            Id = "example.disabled",
            Name = "Disabled",
            EntryPoint = "test.dll",
            TypeName = "Test.TestSource",
            Enabled = false
        };

        var result = loader.LoadPlugin(manifest, _tempDir);

        Assert.False(result);
        Assert.Equal(PluginLoadStatus.Disabled, loader.Plugins["example.disabled"].Status);
    }

    [Fact]
    public void Unload_RemovesPlugin()
    {
        var loader = new PluginLoader(_tempDir);
        var manifest = new VideoScrapePluginManifest
        {
            Id = "example.unload",
            Name = "Unload",
            EntryPoint = "test.dll",
            TypeName = "Test.TestSource"
        };
        loader.LoadPlugin(manifest, _tempDir);

        var result = loader.Unload("example.unload");

        Assert.True(result);
        Assert.Equal(PluginLoadStatus.Disabled, loader.Plugins["example.unload"].Status);
    }

    [Fact]
    public void Unload_ReturnsFalse_ForUnknown()
    {
        var loader = new PluginLoader(_tempDir);

        Assert.False(loader.Unload("nonexistent"));
    }

    [Fact]
    public void GetSources_ReturnsOnlyLoaded()
    {
        var loader = new PluginLoader(_tempDir);
        loader.LoadPlugin(new VideoScrapePluginManifest
        {
            Id = "example.a", Name = "A", EntryPoint = "a.dll", TypeName = "A", Enabled = false
        }, _tempDir);
        loader.LoadPlugin(new VideoScrapePluginManifest
        {
            Id = "example.b", Name = "B", EntryPoint = "b.dll", TypeName = "B"
        }, _tempDir);

        var sources = loader.GetSources();

        // 没有实际 DLL，所有都会加载失败
        Assert.Empty(sources);
    }
}
