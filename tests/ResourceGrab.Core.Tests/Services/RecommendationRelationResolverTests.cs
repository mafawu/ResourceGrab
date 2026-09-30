using System.Text.Json;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using Xunit;

namespace ResourceGrab.Core.Tests.Services;

// 本地推荐关系解析测试：作品分组（Works → 「作品:」标签回退）、作者分组、相关推荐聚合与本地命中。

public class RecommendationRelationResolverTests
{
    private static LocalComic Comic(string path, string name = "", long? albumId = null,
        string sourceId = "", List<string>? works = null, List<string>? authors = null,
        List<string>? tags = null, DateTime? modifiedAt = null)
        => new()
        {
            Path = path,
            Name = name.Length > 0 ? name : path,
            AlbumId = albumId,
            SourceId = sourceId,
            Works = works ?? new List<string>(),
            Author = authors ?? new List<string>(),
            Tags = tags ?? new List<string>(),
            ModifiedAt = modifiedAt ?? DateTime.Now,
        };

    [Fact]
    public void BuildWorkGroups_GroupsByWorks_AndDropsSingletons()
    {
        var comics = new List<LocalComic>
        {
            Comic("a", works: new() { "原神" }),
            Comic("b", works: new() { "原神" }),
            Comic("c", works: new() { "崩坏" }),
        };

        var groups = RecommendationRelationResolver.BuildWorkGroups(comics);

        var group = Assert.Single(groups);
        Assert.Equal("原神", group.Key);
        Assert.Equal(2, group.Members.Count);
    }

    [Fact]
    public void BuildWorkGroups_FallsBackToWorkTag_WhenWorksEmpty()
    {
        var comics = new List<LocalComic>
        {
            Comic("a", tags: new() { "作品:某个系列", "其他:xxx" }),
            Comic("b", tags: new() { "作品:某个系列" }),
        };

        var groups = RecommendationRelationResolver.BuildWorkGroups(comics);

        var group = Assert.Single(groups);
        Assert.Equal("某个系列", group.Key);
    }

    [Fact]
    public void BuildWorkGroups_PrefersWorksOverTag_WhenBothPresent()
    {
        var comics = new List<LocalComic>
        {
            Comic("a", works: new() { "精确作品" }, tags: new() { "作品:旧标签" }),
            Comic("b", works: new() { "精确作品" }, tags: new() { "作品:旧标签" }),
        };

        var groups = RecommendationRelationResolver.BuildWorkGroups(comics);

        var group = Assert.Single(groups);
        Assert.Equal("精确作品", group.Key);
    }

    [Fact]
    public void BuildAuthorGroups_GroupsByAuthor()
    {
        var comics = new List<LocalComic>
        {
            Comic("a", authors: new() { "作者甲" }),
            Comic("b", authors: new() { "作者甲" }),
            Comic("c", authors: new() { "作者乙" }),
        };

        var groups = RecommendationRelationResolver.BuildAuthorGroups(comics);

        var group = Assert.Single(groups);
        Assert.Equal("作者甲", group.Key);
        Assert.Equal(2, group.Members.Count);
    }

    [Fact]
    public void BuildRelated_AggregatesByFrequency_AndMatchesLocal()
    {
        var comics = new List<LocalComic>
        {
            Comic("a", albumId: 100, modifiedAt: new DateTime(2024, 1, 3)),
            Comic("b", albumId: 200, modifiedAt: new DateTime(2024, 1, 2)),
        };
        var relations = new MangaRelationsCache();
        relations.Entries["a"] = new ComicRelation
        {
            Related =
            {
                new RelatedEntry { Id = "200", Name = "已在本地" },
                new RelatedEntry { Id = "300", Name = "在线项" },
            },
        };
        relations.Entries["b"] = new ComicRelation
        {
            Related =
            {
                new RelatedEntry { Id = "300", Name = "在线项" },
            },
        };

        var result = RecommendationRelationResolver.BuildRelated(comics, relations);

        Assert.Equal(2, result.Count);
        Assert.Equal("300", result[0].Entry.Id); // 出现两次，权重最高
        Assert.Equal(2, result[0].Weight);
        Assert.Null(result[0].LocalMatch);
        Assert.Equal("200", result[1].Entry.Id);
        Assert.NotNull(result[1].LocalMatch);
        Assert.Equal("b", result[1].LocalMatch!.Path);
    }

    [Fact]
    public void BuildRelated_EmptyWhenNoRelations()
    {
        var comics = new List<LocalComic> { Comic("a", albumId: 1) };
        Assert.Empty(RecommendationRelationResolver.BuildRelated(comics, new MangaRelationsCache()));
        Assert.Empty(RecommendationRelationResolver.BuildRelated(new List<LocalComic>(), new MangaRelationsCache()));
    }
}

// MangaRelationsService 增量构建：只对新增/变更条目读 album.json，并回填 Works。
public class MangaRelationsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rg-rel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string WriteAlbum(string dirName, string works, string related)
    {
        var dir = Path.Combine(_root, dirName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "album.json"),
            $$"""{"id":1,"name":"n","works":[{{works}}],"related_list":[{{related}}]}""");
        return dir;
    }

    [Fact]
    public void Build_ExtractsWorksAndRelated_AndBackfillsComic()
    {
        var dir = WriteAlbum("a", "\"原神\"",
            """{"id":"300","name":"相关","author":"甲","image":"http://x/1.jpg"}""");
        var local = new LocalLibraryService(new ConfigService(Path.Combine(_root, "config.json")));
        var service = new MangaRelationsService(local);
        var comic = new LocalComic
        {
            Path = dir,
            HasMetadata = true,
            MetadataStamp = File.GetLastWriteTime(Path.Combine(dir, "album.json")),
        };

        var cache = new MangaRelationsCache();
        var changed = service.Build(cache, new[] { comic });

        Assert.True(changed);
        Assert.Equal(new[] { "原神" }, comic.Works);
        var entry = cache.Entries[dir];
        Assert.Equal(new[] { "原神" }, entry.Works);
        var related = Assert.Single(entry.Related);
        Assert.Equal("300", related.Id);
        Assert.Equal("http://x/1.jpg", related.Image);
    }

    [Fact]
    public void IsFresh_TrueAfterBuild_FalseWhenStampChanged()
    {
        var dir = WriteAlbum("b", "\"作品名\"", "");
        var local = new LocalLibraryService(new ConfigService(Path.Combine(_root, "config.json")));
        var service = new MangaRelationsService(local);
        var comic = new LocalComic
        {
            Path = dir,
            HasMetadata = true,
            MetadataStamp = File.GetLastWriteTime(Path.Combine(dir, "album.json")),
        };
        var cache = new MangaRelationsCache();
        var originalStamp = comic.MetadataStamp;
        service.Build(cache, new[] { comic });

        Assert.True(MangaRelationsService.IsFresh(cache, new[] { comic }));

        // 无元数据的漫画不参与新鲜度判断，不影响结果
        var noMeta = new LocalComic { Path = Path.Combine(_root, "x"), HasMetadata = false };
        Assert.True(MangaRelationsService.IsFresh(cache, new[] { comic, noMeta }));

        // 元数据时间戳变化后视为过期
        comic.MetadataStamp = (originalStamp ?? DateTime.Now).AddMinutes(1);
        Assert.False(MangaRelationsService.IsFresh(cache, new[] { comic }));
    }
}
