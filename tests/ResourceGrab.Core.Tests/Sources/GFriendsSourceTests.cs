using ResourceGrab.Core.Services.VideoScrape.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

public class GFriendsSourceTests
{
    [Fact]
    public void ParseIndex_FlattensCompaniesAndAliases()
    {
        const string json = """
            {
              "Content": {
                "z-ラグジュTV": {
                  "黒木澪.jpg": "AI-Fix-黒木澪.jpg?t=1607433809",
                  "Kaname Yuuna.jpg": "AI-Fix-黒川さりな.jpg?t=1607433809",
                  "别名.jpg": ""
                },
                "人工存储 HandStorage": {
                  "佐藤花子.png": "sato.jpg"
                }
              }
            }
            """;

        var index = GFriendsSource.ParseIndex(json);

        Assert.Equal(3, index.Count);
        // 键剥掉 .jpg 后缀
        Assert.True(index.ContainsKey("黒木澪"));
        // 值剥掉 ?t= 缓存戳
        Assert.Equal(("z-ラグジュTV", "AI-Fix-黒木澪.jpg"), index["黒木澪"]);
        // 别名键映射到实际文件（别名归一）
        Assert.Equal(("z-ラグジュTV", "AI-Fix-黒川さりな.jpg"), index["Kaname Yuuna"]);
        // 值为空的条目跳过
        Assert.False(index.ContainsKey("别名"));
        // 其他公司正常收录（非 .jpg 后缀的文件名也保留）
        Assert.Equal(("人工存储 HandStorage", "sato.jpg"), index["佐藤花子"]);
    }

    [Fact]
    public void ParseIndex_EmptyOrMissingContent_ReturnsEmpty()
    {
        Assert.Empty(GFriendsSource.ParseIndex("{}"));
        Assert.Empty(GFriendsSource.ParseIndex("""{"Other": 1}"""));
    }

    [Fact]
    public void AvatarFileName_IsStableHash()
    {
        var a = GFriendsSource.AvatarFileName("黒川さりな");
        Assert.Equal(16, a.Length);
        Assert.Equal(a, GFriendsSource.AvatarFileName("黒川さりな"));
        Assert.NotEqual(a, GFriendsSource.AvatarFileName("別人"));
    }
}
