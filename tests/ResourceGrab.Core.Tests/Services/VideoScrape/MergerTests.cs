using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape;
using Xunit;

namespace ResourceGrab.Core.Tests.Services.VideoScrape;

public class MergerTests
{
    private static VideoMetadataMerger CreateMerger(VideoScrapeAdvancedSettings? config = null) =>
        new VideoMetadataMerger(config ?? new VideoScrapeAdvancedSettings());

    private static VideoSourceFetchResult SuccessResult(string sourceId, Action<VideoScrapeMetadata> configure)
    {
        var meta = new VideoScrapeMetadata { Source = sourceId };
        configure(meta);
        return VideoSourceFetchResult.Success(sourceId, meta);
    }

    [Fact]
    public void Merge_ScalarField_PicksFirstNonEmpty()
    {
        var merger = CreateMerger();
        var results = new List<VideoSourceFetchResult>
        {
            SuccessResult("airav", m => { m.Title = "中文标题"; m.Description = "中文简介"; }),
            SuccessResult("javbus", m => { m.Title = "日文タイトル"; m.CoverUrl = "https://javbus.com/cover.jpg"; }),
        };
        var aggregate = merger.Merge("TEST-001", VideoContentKind.Chinese, results);
        Assert.Equal("中文标题", aggregate.Metadata.Title);
        Assert.Equal("airav", aggregate.FieldSources["title"]);
    }

    [Fact]
    public void Merge_ScalarField_FallsBackToNextSource()
    {
        var merger = CreateMerger();
        var results = new List<VideoSourceFetchResult>
        {
            SuccessResult("airav", m => { m.Title = ""; }),
            SuccessResult("javdb", m => { m.Title = "JavDB标题"; }),
        };
        var aggregate = merger.Merge("TEST-001", VideoContentKind.Chinese, results);
        Assert.Equal("JavDB标题", aggregate.Metadata.Title);
        Assert.Equal("javdb", aggregate.FieldSources["title"]);
    }

    [Fact]
    public void Merge_CollectionField_UnionDistinct()
    {
        var merger = CreateMerger();
        var results = new List<VideoSourceFetchResult>
        {
            SuccessResult("javbus", m => { m.Actors = ["A", "B"]; m.Tags = ["tag1"]; }),
            SuccessResult("javdb", m => { m.Actors = ["B", "C"]; m.Tags = ["tag2"]; }),
        };
        var aggregate = merger.Merge("TEST-001", VideoContentKind.Censored, results);
        Assert.Equal(3, aggregate.Metadata.Actors.Count);
        Assert.Contains("A", aggregate.Metadata.Actors);
        Assert.Contains("B", aggregate.Metadata.Actors);
        Assert.Contains("C", aggregate.Metadata.Actors);
        Assert.Equal(2, aggregate.Metadata.Tags.Count);
        Assert.Contains("javbus+javdb", aggregate.FieldSources["actors"]);
    }

    [Fact]
    public void Merge_BoolField_Or()
    {
        var merger = CreateMerger();
        var results = new List<VideoSourceFetchResult>
        {
            SuccessResult("javbus", m => { m.HasMagnet = false; }),
            SuccessResult("javdb", m => { m.HasMagnet = true; m.HasChineseSubtitle = true; }),
        };
        var aggregate = merger.Merge("TEST-001", VideoContentKind.Censored, results);
        Assert.True(aggregate.Metadata.HasMagnet);
        Assert.True(aggregate.Metadata.HasChineseSubtitle);
    }

    [Fact]
    public void Merge_FieldSources_TracksOrigin()
    {
        var merger = CreateMerger();
        var results = new List<VideoSourceFetchResult>
        {
            SuccessResult("javbus", m =>
            {
                m.Title = "JavBus标题";
                m.CoverUrl = "https://javbus.com/cover.jpg";
                m.Score = 0;
            }),
            SuccessResult("javdb", m =>
            {
                m.Title = "JavDB中文标题";
                m.Score = 8.5;
            }),
        };
        var aggregate = merger.Merge("TEST-001", VideoContentKind.Censored, results);
        var fsDump = string.Join(", ", aggregate.FieldSources.Select(kv => $"{kv.Key}={kv.Value}"));
        Assert.True(aggregate.FieldSources.ContainsKey("title"), $"FieldSources=[{fsDump}]");
        Assert.True(aggregate.FieldSources.ContainsKey("cover_url"), $"FieldSources=[{fsDump}]");
        Assert.True(aggregate.FieldSources.ContainsKey("score"), $"FieldSources=[{fsDump}]");
    }

    [Fact]
    public void Merge_AllFailed_ReturnsEmptyMetadata()
    {
        var merger = CreateMerger();
        var results = new List<VideoSourceFetchResult>
        {
            VideoSourceFetchResult.NoMatch("javbus"),
            VideoSourceFetchResult.Failure("javdb", VideoSourceOutcome.Blocked, "cloudflare"),
        };
        var aggregate = merger.Merge("TEST-001", VideoContentKind.Censored, results);
        Assert.Equal("", aggregate.Metadata.Title);
        Assert.Empty(aggregate.FieldSources);
        Assert.Equal(2, aggregate.Attempts.Count);
    }

    [Fact]
    public void Merge_TitleSpecial_ChineseTitlePreservesOriginal()
    {
        var config = new VideoScrapeAdvancedSettings
        {
            FieldPriorities =
            [
                new() { Field = "title", DefaultSources = ["airav", "javdb", "javbus"] },
                new() { Field = "originalTitle", DefaultSources = ["dmm"] },
            ]
        };
        var merger = CreateMerger(config);
        var results = new List<VideoSourceFetchResult>
        {
            SuccessResult("javbus", m => { m.Title = "はたらく兄弟"; }),
            SuccessResult("airav", m => { m.Title = "中文标题"; }),
        };
        var aggregate = merger.Merge("TEST-001", VideoContentKind.Censored, results);
        Assert.Equal("中文标题", aggregate.Metadata.Title);
    }

    [Fact]
    public void Merge_SourceUrls_CollectsAllSources()
    {
        var merger = CreateMerger();
        var results = new List<VideoSourceFetchResult>
        {
            SuccessResult("javbus", m =>
            {
                m.Title = "Test";
                m.SourceUrls["javbus"] = "https://javbus.com/test";
            }),
            SuccessResult("javdb", m =>
            {
                m.Title = "Test";
                m.SourceUrls["javdb"] = "https://javdb.com/test";
            }),
        };
        var aggregate = merger.Merge("TEST-001", VideoContentKind.Censored, results);
        Assert.Equal(2, aggregate.Metadata.SourceUrls.Count);
        Assert.Equal("https://javbus.com/test", aggregate.Metadata.SourceUrls["javbus"]);
        Assert.Equal("https://javdb.com/test", aggregate.Metadata.SourceUrls["javdb"]);
    }

    [Theory]
    [InlineData("はたらくお兄さん", true)]
    [InlineData("ハイキュー!!", true)]
    [InlineData("SSIS-405", false)]
    [InlineData("三上悠亚", false)]
    public void LooksJapanese_DetectsJapanese(string input, bool expected)
    {
        Assert.Equal(expected, VideoMetadataMerger.LooksJapanese(input));
    }

    [Fact]
    public void Merge_Score_PicksFirstValid()
    {
        var merger = CreateMerger();
        var results = new List<VideoSourceFetchResult>
        {
            SuccessResult("javbus", m => { m.Score = 0; }),
            SuccessResult("javdb", m => { m.Score = 8.2; m.ScoreVotes = 42; }),
        };
        var aggregate = merger.Merge("TEST-001", VideoContentKind.Censored, results);
        Assert.Equal(8.2, aggregate.Metadata.Score);
        Assert.Equal(42, aggregate.Metadata.ScoreVotes);
        Assert.Equal("javdb", aggregate.FieldSources["score"]);
    }

    [Fact]
    public void Merge_PreviewImages_UnionDistinct()
    {
        var merger = CreateMerger();
        var results = new List<VideoSourceFetchResult>
        {
            SuccessResult("javbus", m =>
            {
                m.Title = "T";
                m.PreviewImageUrls = ["https://a.jpg", "https://b.jpg"];
            }),
            SuccessResult("javdb", m =>
            {
                m.Title = "T";
                m.PreviewImageUrls = ["https://b.jpg", "https://c.jpg"];
            }),
        };
        var aggregate = merger.Merge("TEST-001", VideoContentKind.Censored, results);
        Assert.Equal(3, aggregate.Metadata.PreviewImageUrls.Count);
        Assert.Contains("https://c.jpg", aggregate.Metadata.PreviewImageUrls);
    }
}

