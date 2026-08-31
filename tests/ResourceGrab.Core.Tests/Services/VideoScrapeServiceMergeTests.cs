using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using Xunit;

namespace ResourceGrab.Core.Tests.Services;

/// <summary>
/// M0: 为现有 VideoScrapeService 中的合并逻辑补充单元测试。
/// 验证 MergeJavDb、MergeAirav、LooksJapanese 等静态方法的行为。
/// </summary>
public class VideoScrapeServiceMergeTests
{
    // -------------------------------------------------------------------
    // LooksJapanese
    // -------------------------------------------------------------------

    [Theory]
    [InlineData("はたらくお兄さん", true)]
    [InlineData("ハイキュー!!", true)]
    [InlineData("AVOP-123", false)]
    [InlineData("SSIS-405", false)]
    [InlineData("三上悠亚", false)]
    [InlineData("", false)]
    public void LooksJapanese_DetectsHiraganaAndKatakana(string input, bool expected)
    {
        Assert.Equal(expected, VideoScrapeService.LooksJapanese(input));
    }

    // -------------------------------------------------------------------
    // MergeJavDb
    // -------------------------------------------------------------------

    [Fact]
    public void MergeJavDb_ChineseTitle_ReplacesJapaneseTitle()
    {
        var primary = new VideoScrapeMetadata
        {
            Source = "javbus",
            Title = "AVOP-123 日文タイトル",
        };
        var javDb = new VideoScrapeMetadata
        {
            Source = "javdb",
            Title = "中文标题",
            Score = 8.5,
            ScoreVotes = 42,
        };

        VideoScrapeService.MergeJavDb("AVOP-123", primary, javDb);

        // 中文标题应替换日文标题，原日文标题写入 OriginalTitle
        Assert.Equal("中文标题", primary.Title);
        Assert.Equal("AVOP-123 日文タイトル", primary.OriginalTitle);
        Assert.Equal(8.5, primary.Score);
        Assert.Equal(42, primary.ScoreVotes);
    }

    [Fact]
    public void MergeJavDb_JapaneseTitle_DoesNotReplaceExistingChineseTitle()
    {
        var primary = new VideoScrapeMetadata
        {
            Source = "javbus",
            Title = "中文已有标题",
        };
        var javDb = new VideoScrapeMetadata
        {
            Source = "javdb",
            Title = "はたらく兄弟", // 日文标题
        };

        VideoScrapeService.MergeJavDb("TEST-001", primary, javDb);

        // 日文标题不应覆盖已有中文标题
        Assert.Equal("中文已有标题", primary.Title);
    }

    [Fact]
    public void MergeJavDb_MergesPreviewImages_Union()
    {
        var primary = new VideoScrapeMetadata
        {
            Source = "javbus",
            PreviewImageUrls = ["https://example.com/a.jpg"],
        };
        var javDb = new VideoScrapeMetadata
        {
            Source = "javdb",
            PreviewImageUrls = ["https://example.com/a.jpg", "https://example.com/b.jpg"],
        };

        VideoScrapeService.MergeJavDb("TEST-001", primary, javDb);

        Assert.Equal(2, primary.PreviewImageUrls.Count);
        Assert.Contains("https://example.com/b.jpg", primary.PreviewImageUrls);
    }

    [Fact]
    public void MergeJavDb_FieldsNotOverwritten_WhenPrimaryHasValues()
    {
        var primary = new VideoScrapeMetadata
        {
            Source = "javbus",
            Director = "已知导演",
            Studio = "已知片商",
            ReleaseDate = new DateTime(2020, 1, 1),
            RuntimeMinutes = 90,
        };
        var javDb = new VideoScrapeMetadata
        {
            Source = "javdb",
            Director = "新导演",
            Studio = "新片商",
            ReleaseDate = new DateTime(2021, 6, 1),
            RuntimeMinutes = 120,
        };

        VideoScrapeService.MergeJavDb("TEST-001", primary, javDb);

        // 不应覆盖已有值
        Assert.Equal("已知导演", primary.Director);
        Assert.Equal("已知片商", primary.Studio);
        Assert.Equal(new DateTime(2020, 1, 1), primary.ReleaseDate);
        Assert.Equal(90, primary.RuntimeMinutes);
    }

    [Fact]
    public void MergeJavDb_FillsMissingFields_FromJavDb()
    {
        var primary = new VideoScrapeMetadata { Source = "javbus" };
        var javDb = new VideoScrapeMetadata
        {
            Source = "javdb",
            Director = "导演X",
            Studio = "片商Y",
            ReleaseDate = new DateTime(2021, 3, 15),
            RuntimeMinutes = 100,
            HasMagnet = true,
            HasChineseSubtitle = true,
        };

        VideoScrapeService.MergeJavDb("TEST-001", primary, javDb);

        Assert.Equal("导演X", primary.Director);
        Assert.Equal("片商Y", primary.Studio);
        Assert.Equal(new DateTime(2021, 3, 15), primary.ReleaseDate);
        Assert.Equal(100, primary.RuntimeMinutes);
        Assert.True(primary.HasMagnet);
        Assert.True(primary.HasChineseSubtitle);
    }

    // -------------------------------------------------------------------
    // MergeAirav
    // -------------------------------------------------------------------

    [Fact]
    public void MergeAirav_ChineseTitle_ReplacesJapaneseTitle()
    {
        var primary = new VideoScrapeMetadata
        {
            Source = "javbus",
            Title = "はたらく兄弟",
        };
        var airav = new VideoScrapeMetadata
        {
            Source = "airav",
            Title = "中文标题从AirAv",
            Description = "这是中文简介",
        };

        VideoScrapeService.MergeAirav(primary, airav);

        Assert.Equal("中文标题从AirAv", primary.Title);
        Assert.Equal("はたらく兄弟", primary.OriginalTitle);
        Assert.Equal("这是中文简介", primary.Description);
    }

    [Fact]
    public void MergeAirav_FillsDescription_WhenEmpty()
    {
        var primary = new VideoScrapeMetadata { Source = "javbus", Description = "" };
        var airav = new VideoScrapeMetadata { Source = "airav", Description = "中文简介内容" };

        VideoScrapeService.MergeAirav(primary, airav);

        Assert.Equal("中文简介内容", primary.Description);
    }

    [Fact]
    public void MergeAirav_DoesNotOverwrite_ExistingDescription()
    {
        var primary = new VideoScrapeMetadata { Source = "javbus", Description = "已有简介" };
        var airav = new VideoScrapeMetadata { Source = "airav", Description = "新简介" };

        VideoScrapeService.MergeAirav(primary, airav);

        Assert.Equal("已有简介", primary.Description);
    }

    [Fact]
    public void MergeAirav_CoverUrl_FillsWhenEmpty()
    {
        var primary = new VideoScrapeMetadata { Source = "javbus", CoverUrl = "" };
        var airav = new VideoScrapeMetadata { Source = "airav", CoverUrl = "https://airav.io/cover.jpg" };

        VideoScrapeService.MergeAirav(primary, airav);

        Assert.Equal("https://airav.io/cover.jpg", primary.CoverUrl);
    }

    [Fact]
    public void MergeAirav_CoverUrl_DoesNotOverwrite()
    {
        var primary = new VideoScrapeMetadata { Source = "javbus", CoverUrl = "https://javbus.com/cover.jpg" };
        var airav = new VideoScrapeMetadata { Source = "airav", CoverUrl = "https://airav.io/cover.jpg" };

        VideoScrapeService.MergeAirav(primary, airav);

        Assert.Equal("https://javbus.com/cover.jpg", primary.CoverUrl);
    }

    // -------------------------------------------------------------------
    // AiravScraper.SelectSearchResult
    // -------------------------------------------------------------------

    [Fact]
    public void SelectSearchResult_MatchesCorrectEntry()
    {
        var html = File.ReadAllText(Path.Combine("Fixtures", "airav-search.html"));
        var doc = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        var result = AiravScraper.SelectSearchResult(doc, "SSIS-405");

        Assert.NotNull(result);
        Assert.Contains("405", result!);
    }

    [Fact]
    public void SelectSearchResult_ReturnsNull_WhenNoMatch()
    {
        var html = File.ReadAllText(Path.Combine("Fixtures", "airav-search.html"));
        var doc = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

        var result = AiravScraper.SelectSearchResult(doc, "XXXX-9999");

        Assert.Null(result);
    }

    // -------------------------------------------------------------------
    // AiravScraper.ParseDetail
    // -------------------------------------------------------------------

    [Fact]
    public void ParseDetail_ExtractsMetadata()
    {
        var html = File.ReadAllText(Path.Combine("Fixtures", "airav-detail.html"));

        var metadata = AiravScraper.ParseDetail(html, "SSIS-405");

        Assert.NotNull(metadata);
        Assert.Equal("airav", metadata!.Source);
        Assert.Contains("中出し解禁", metadata.Title);
        Assert.Equal("这是中文简介内容，描述了本片的剧情。", metadata.Description);
        Assert.Equal(2, metadata.Actors.Count);
        Assert.Contains("三上悠亚", metadata.Actors);
        Assert.Contains("高桥圣子", metadata.Actors);
        Assert.Equal(3, metadata.Tags.Count);
        Assert.Contains("高清", metadata.Tags);
        Assert.Equal("S1", metadata.Studio);
        Assert.Equal("S1 NO.1 STYLE", metadata.Series);
        Assert.Equal("https://airav.io/covers/405.jpg", metadata.CoverUrl);
    }

    [Fact]
    public void ParseDetail_ReturnsNull_WhenTitleIsEmpty()
    {
        var html = "<html><body><div class=\"video-title my-3\"><h1></h1></div></body></html>";

        var metadata = AiravScraper.ParseDetail(html, "SSIS-405");

        Assert.Null(metadata);
    }

    // -------------------------------------------------------------------
    // JavBusScraper.ResolveUrl
    // -------------------------------------------------------------------

    [Theory]
    [InlineData("https://www.javbus.com", "/pics/cover.jpg", "https://www.javbus.com/pics/cover.jpg")]
    [InlineData("https://www.javbus.com", "https://cdn.example.com/img.jpg", "https://cdn.example.com/img.jpg")]
    [InlineData("https://www.javbus.com", "//cdn.example.com/img.jpg", "https://cdn.example.com/img.jpg")]
    [InlineData("https://www.javbus.com", "pics/cover.jpg", "https://www.javbus.com/pics/cover.jpg")]
    public void ResolveUrl_HandlesVariousFormats(string baseUrl, string url, string expected)
    {
        Assert.Equal(expected, JavBusScraper.ResolveUrl(baseUrl, url));
    }

    [Fact]
    public void ResolveUrl_EmptyUrl_ReturnsEmpty()
    {
        Assert.Equal("", JavBusScraper.ResolveUrl("https://example.com", ""));
    }
}
