using ResourceGrab.Core.Utils;
using Xunit;

namespace ResourceGrab.Core.Tests.Utils;

// 本地漫画标题 ↔ 在线候选 的匹配打分测试：归一化、相似度、作者加分、自动采纳判定、禁漫 id 提取。

public class ComicTitleMatcherTests
{
    [Theory]
    [InlineData("【A汉化组】[作者] タイトル (C97)", "作者タイトル")] // 汉化组与展会标记剔除，其余仅去标点
    [InlineData("(中国翻訳) タイトル", "タイトル")]
    [InlineData("[DL版] タイトル", "dl版タイトル")] // 非翻译标记保留，仅去标点
    public void NormalizeTitle_RemovesTranslationMarksAndJunk(string input, string expected)
    {
        Assert.Equal(expected, ComicTitleMatcher.NormalizeTitle(input));
    }

    [Fact]
    public void NormalizeTitle_KeepsFullWidthAndHalfWidthEquivalent()
    {
        Assert.Equal(ComicTitleMatcher.NormalizeTitle("ＴＩＴＬＥ　Ｘ"), ComicTitleMatcher.NormalizeTitle("TITLE X!"));
    }

    [Fact]
    public void Score_ReorderedBrackets_AfterNormalization_ReturnsOne()
    {
        var score = ComicTitleMatcher.Score("(C97) おっぱいドラゴン", "おっぱいドラゴン (C97)");
        Assert.Equal(1.0, score);
    }

    [Fact]
    public void Score_AuthorPrefixDifference_StillCountsAsContainment()
    {
        // 真实链路：本地标题已经 MangaFilenameParser 清洗，候选标题常带「作者 前缀」
        var score = ComicTitleMatcher.Score("おっぱいドラゴン", "[作者] おっぱいドラゴン (中国翻訳)");
        Assert.True(score >= 0.9, $"actual {score}");
    }

    [Fact]
    public void Score_Containment_ReturnsHighScore()
    {
        var score = ComicTitleMatcher.Score("おっぱいドラゴン", "おっぱいドラゴン 2");
        Assert.True(score >= 0.9, $"actual {score}");
    }

    [Fact]
    public void Score_DifferentTitles_ReturnsLowScore()
    {
        var score = ComicTitleMatcher.Score("おっぱいドラゴン", "ちっちゃな雪うさぎ");
        Assert.True(score < 0.5, $"actual {score}");
    }

    [Fact]
    public void Score_AuthorMatch_AddsBonus()
    {
        var without = ComicTitleMatcher.Score("タイトル", "タイトル 別作者版");
        var with = ComicTitleMatcher.Score("タイトル", "タイトル 別作者版", new[] { "ほげ作者" }, "ほげ作者");
        Assert.True(with > without, $"with {with} should exceed without {without}");
    }

    [Fact]
    public void AcceptsAutoMatch_RequiresThresholdAndMargin()
    {
        Assert.True(ComicTitleMatcher.AcceptsAutoMatch(new[] { 0.95, 0.6 }));
        Assert.True(ComicTitleMatcher.AcceptsAutoMatch(new[] { 0.9 }));
        Assert.False(ComicTitleMatcher.AcceptsAutoMatch(new[] { 0.95, 0.9 })); // 前两名咬得太近
        Assert.False(ComicTitleMatcher.AcceptsAutoMatch(new[] { 0.7, 0.2 }));  // 最高分不达标
        Assert.False(ComicTitleMatcher.AcceptsAutoMatch(Array.Empty<double>()));
    }

    [Theory]
    [InlineData("[作者] タイトル (JM422466)", "422466")]
    [InlineData("放置少女 4661234567円", "")] // 位数不符/普通数字不提取
    [InlineData("(C97) タイトル", "")] // 展会短数字不提取
    [InlineData("48P 短页数", "")]
    public void ExtractJmAlbumIds_FindsStandaloneIdLikeNumbers(string folderName, string expected)
    {
        var ids = ComicTitleMatcher.ExtractJmAlbumIds(folderName);
        if (expected.Length == 0)
        {
            Assert.Empty(ids);
        }
        else
        {
            Assert.Equal(new[] { expected }, ids);
        }
    }

    [Fact]
    public void ExtractJmAlbumIds_IgnoresNumbersInsideLongerRuns()
    {
        // 8 位日期式长数字不应被截成 5~7 位 id
        Assert.Empty(ComicTitleMatcher.ExtractJmAlbumIds("20220101 イベント"));
    }
}
