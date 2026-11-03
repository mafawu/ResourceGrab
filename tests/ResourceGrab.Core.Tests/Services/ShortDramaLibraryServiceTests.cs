using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.ShortDrama;
using ResourceGrab.Core.Sources.ShortDrama;
using Xunit;

namespace ResourceGrab.Core.Tests.Services;

// 短剧本地库：drama.json 是唯一真相来源，所以"合并详情不擦掉已下载记录"和
// "文件被手动删掉后自动降级为未下载"这两条是这个功能的地基，必须有测试兜住。
public class ShortDramaLibraryServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ShortDramaLibraryService _library;

    public ShortDramaLibraryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rg-drama-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _library = new ShortDramaLibraryService(() => new ShortDramaSettings { DownloadDir = _root });
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private static ShortDramaDetail MakeDetail(string title = "测试短剧", int episodes = 3, string id = "9001")
        => new()
        {
            Id = id,
            Title = title,
            Description = "简介",
            ScoreText = "8.4",
            ChannelName = "AI成人短剧",
            EpisodeText = $"全{episodes}集",
            ReleaseText = "2026-10-29",
            TotalEpisodes = episodes,
            Tags = ["乱伦", "熟女"],
            Episodes = Enumerable.Range(1, episodes)
                .Select(n => new ShortDramaEpisode { Number = n, DurationText = "4:21" })
                .ToList(),
        };

    [Fact]
    public void RootDir_FallsBackToDefaultWhenUnset()
    {
        var lib = new ShortDramaLibraryService(() => new ShortDramaSettings());
        Assert.Equal(ShortDramaSettings.ResolveDownloadDir(null), lib.RootDir);
        Assert.Contains("短剧下载", lib.RootDir);
    }

    [Fact]
    public void MergeDetail_PreservesDownloadedEpisodes()
    {
        var detail = MakeDetail();
        var folder = _library.EnsureFolder(detail);

        var meta = _library.MergeDetail(detail, folder);
        meta.Episodes[0].File = "测试短剧 第01集.mp4";
        meta.Episodes[0].SizeBytes = 1234;
        _library.Save(folder, meta);

        // 再次打开详情（站点数据重新抓一遍），已下载记录必须还在
        var merged = _library.MergeDetail(MakeDetail(), folder);

        Assert.Equal(3, merged.Episodes.Count);
        Assert.Equal("测试短剧 第01集.mp4", merged.Episodes[0].File);
        Assert.Equal(1234, merged.Episodes[0].SizeBytes);
        Assert.Null(merged.Episodes[1].File);
        Assert.Equal(1, merged.DownloadedCount);
    }

    [Fact]
    public void Load_DowngradesEpisodeWhenFileWasDeletedManually()
    {
        var detail = MakeDetail();
        var folder = _library.EnsureFolder(detail);
        var meta = _library.MergeDetail(detail, folder);
        var file = Path.Combine(folder, "测试短剧 第02集.mp4");
        File.WriteAllText(file, "fake");
        meta.Episodes[1].File = Path.GetFileName(file);
        meta.Episodes[1].SizeBytes = 4;
        _library.Save(folder, meta);

        Assert.Single(_library.Load());
        Assert.Equal(1, _library.Load()[0].DownloadedCount);

        File.Delete(file);
        var reloaded = _library.Load();
        Assert.Single(reloaded);
        Assert.Equal(0, reloaded[0].DownloadedCount);
        Assert.Null(reloaded[0].ResolveEpisodeFile(2));
    }

    [Fact]
    public void RecordEpisodeDone_WritesBackFileAndSize()
    {
        var detail = MakeDetail();
        var folder = _library.EnsureFolder(detail);
        _library.Save(folder, _library.MergeDetail(detail, folder));

        var output = Path.Combine(folder, "测试短剧 第03集.mp4");
        File.WriteAllText(output, "hello");

        Assert.True(_library.RecordEpisodeDone(output, 3, 5));

        var local = _library.Load().Single();
        Assert.Equal(1, local.DownloadedCount);
        Assert.Equal("测试短剧 第03集.mp4", local.Metadata.Episodes.Single(e => e.Number == 3).File);
        Assert.Equal(5, local.Metadata.Episodes.Single(e => e.Number == 3).SizeBytes);
    }

    [Fact]
    public void DeleteDrama_RefusesFolderOutsideRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), "rg-drama-outside-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        try
        {
            var local = new LocalDrama { FolderPath = outside, Metadata = new DramaMetadata { Title = "外部" } };
            Assert.False(_library.DeleteDrama(local));
            Assert.True(Directory.Exists(outside), "库目录之外的路径不能被删");
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public void SanitizeFolderName_ReplacesInvalidChars()
    {
        var name = ShortDramaLibraryService.SanitizeFolderName("剧名:第一季/合集?");
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('?', name);
        Assert.Equal("未命名短剧", ShortDramaLibraryService.SanitizeFolderName("   "));
    }

    

    [Fact]
    public void FolderFor_IsStableForSameTitle()
    {
        Assert.Equal(_library.FolderFor("苗疆少女"), _library.FolderFor("苗疆少女"));
        Assert.StartsWith(_root, _library.FolderFor("苗疆少女"));
    }
}
