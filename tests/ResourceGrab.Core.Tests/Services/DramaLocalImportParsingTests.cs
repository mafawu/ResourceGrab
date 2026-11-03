using ResourceGrab.Core.Services.ShortDrama;
using Xunit;

namespace ResourceGrab.Core.Tests.Services;

// 本地短剧导入：全靠文件名/目录名猜，所以这几条解析规则必须锁住。
public class DramaLocalImportParsingTests
{
    [Theory]
    [InlineData("第01集", 1)]
    [InlineData("第 12 集", 12)]
    [InlineData("EP03", 3)]
    [InlineData("E07", 7)]
    [InlineData("苗疆少女-05", 5)]
    [InlineData("苗疆少女 05", 5)]
    [InlineData("苗疆少女05", 5)]
    [InlineData("12", 12)]
    public void ParseEpisodeNumber_RecognizesCommonForms(string name, int expected)
        => Assert.Equal(expected, DramaLocalImportService.ParseEpisodeNumber(name));

    [Theory]
    [InlineData("苗疆少女")]
    [InlineData("EP")]
    public void ParseEpisodeNumber_ReturnsNullWhenNoNumber(string name)
        => Assert.Null(DramaLocalImportService.ParseEpisodeNumber(name));

    [Theory]
    [InlineData("【无码】苗疆少女 全12集", "苗疆少女")]
    [InlineData("[4K] 金牌御医 更新至8集", "金牌御医")]
    [InlineData("小树林里的约会 (1080P)", "小树林里的约会")]
    public void CleanTitle_StripsTagsAndEpisodeHints(string raw, string expected)
        => Assert.Equal(expected, DramaLocalImportService.CleanTitle(raw));

    [Fact]
    public void Normalize_KeepsOnlyLettersAndDigits()
        => Assert.Equal("苗疆少女2", DramaLocalImportService.Normalize("苗疆·少女 2！"));

    [Fact]
    public void Dice_ScoresSimilarTitlesHigher()
    {
        var exact = DramaLocalImportService.Dice("苗疆少女", "苗疆少女");
        var near = DramaLocalImportService.Dice("苗疆少女", "苗疆少女第二季");
        var far = DramaLocalImportService.Dice("苗疆少女", "金牌御医");

        Assert.Equal(1.0, exact);
        Assert.True(near > far);
        Assert.True(far < 0.3);
    }
}
