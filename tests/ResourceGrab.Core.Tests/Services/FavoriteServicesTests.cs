using ResourceGrab.Core.Services;
using Xunit;

namespace ResourceGrab.Core.Tests.Services;

public class FavoriteServicesTests
{
    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), "rg-favtests", Guid.NewGuid() + "-" + name);

    [Fact]
    public void ActorFavorites_Toggle_Persists()
    {
        var path = TempPath("actors.json");
        var svc = new ActorFavoriteService(path);
        Assert.False(svc.IsFavorite("  "));

        Assert.True(svc.Toggle("Alice"));
        Assert.True(svc.IsFavorite("alice")); // 大小写不敏感
        Assert.False(svc.Toggle("ALICE"));
        Assert.False(svc.IsFavorite("Alice"));

        svc.Toggle("Bob");
        var reloaded = new ActorFavoriteService(path);
        Assert.True(reloaded.IsFavorite("bob"));
        Assert.Single(reloaded.GetAll());
    }

    [Fact]
    public void ActorFavorites_Enrichment_Roundtrips()
    {
        var path = TempPath("actors2.json");
        var svc = new ActorFavoriteService(path);
        svc.UpdateEnrichment("Alice", null, "生日 2000-01-01");
        var entry = svc.Get("alice");
        Assert.NotNull(entry);
        Assert.Equal("生日 2000-01-01", entry!.Bio);
        Assert.Null(entry.AvatarPath);
    }

    [Fact]
    public void OnlineFavorites_Toggle_LinkByNumber()
    {
        var path = TempPath("online.json");
        var svc = new OnlineVideoFavoriteService(path);
        Assert.False(svc.IsFavorite("missav", ""));

        Assert.True(svc.Toggle("missav", "abc123", "SSIS-960", "标题", "http://cover"));
        Assert.True(svc.IsFavorite("missav", "abc123"));
        Assert.Single(svc.FindByNumber("ssis-960")); // 大小写不敏感
        Assert.Empty(svc.FindByNumber("SSIS-961"));

        Assert.Equal(1, svc.RemoveByNumber("SSIS-960"));
        Assert.False(svc.IsFavorite("missav", "abc123"));

        svc.Toggle("missav", "x1", "SSIS-960", "t", "");
        var reloaded = new OnlineVideoFavoriteService(path);
        Assert.True(reloaded.IsFavorite("missav", "x1"));
    }
}
