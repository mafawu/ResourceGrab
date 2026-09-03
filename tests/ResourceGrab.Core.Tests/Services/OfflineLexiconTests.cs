using ResourceGrab.Core.Services;
using Xunit;

namespace ResourceGrab.Core.Tests.Services;

// 离线词典测试：加载的是内嵌真实数据文件（mapping_actor.xml / mapping_info.xml / zhcdict.json），
// 关键回归：资源内嵌正确、词典解析正确、刮削元数据后处理不破坏数据。

public class OfflineLexiconTests
{
    [Fact]
    public void TranslateActor_MapsJapaneseAliasToChinese()
    {
        Assert.Equal("苍井空", OfflineLexicon.TranslateActor("蒼井そら"));
        Assert.Equal("苍井空", OfflineLexicon.TranslateActor("苍井空")); // 简体名幂等
    }

    [Fact]
    public void TranslateActor_UnknownName_ReturnsOriginal()
    {
        Assert.Equal("桃果あかり", OfflineLexicon.TranslateActor("桃果あかり"));
        Assert.Equal("", OfflineLexicon.TranslateActor(""));
        Assert.Equal("", OfflineLexicon.TranslateActor(null));
    }

    [Fact]
    public void ToSimplified_ConvertsTraditionalText()
    {
        Assert.Equal("简体", OfflineLexicon.ToSimplified("簡體"));
        Assert.Equal("高清线上观看", OfflineLexicon.ToSimplified("高清線上觀看"));
        // 已是简体/西文：原样返回
        Assert.Equal("苍井空 SSIS-960", OfflineLexicon.ToSimplified("苍井空 SSIS-960"));
    }

    [Fact]
    public void NormalizeTag_DropsMarkedKeywords()
    {
        // mapping_info 中 zh_cn="删除" 的关键词应被丢弃
        Assert.Null(OfflineLexicon.NormalizeTag("成人奖"));
    }

    [Fact]
    public void NormalizeTag_MapsAliasToUnifiedName()
    {
        Assert.Equal("16小时+", OfflineLexicon.NormalizeTag("16小時以上作品"));
        Assert.Equal("16小时+", OfflineLexicon.NormalizeTag("16時間以上作品"));
    }

    [Fact]
    public void ProcessMetadata_ConvertsTitleDescriptionAndActors()
    {
        var meta = new ResourceGrab.Core.Models.VideoScrapeMetadata
        {
            Title = "高清線上獨家",
            OriginalTitle = "高清線上獨家（原文）",
            Description = "這是測試簡介",
            Actors = ["蒼井そら", "蒼井そら", ""],
            Tags = ["成人奖", "16小時以上作品"],
        };

        OfflineLexicon.ProcessMetadata(meta);

        Assert.Equal("高清线上独家", meta.Title);
        Assert.Equal("这是测试简介", meta.Description);
        // OriginalTitle 保留原样
        Assert.Equal("高清線上獨家（原文）", meta.OriginalTitle);
        // 演员去重、译名、去空
        Assert.Equal(["苍井空"], meta.Actors);
        // 标签：删除项被丢弃、别名归一
        Assert.Equal(["16小时+"], meta.Tags);
    }
}
