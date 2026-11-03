using ResourceGrab.Core.Sources.ShortDrama;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

// 短剧（西瓜短剧）解析器夹具测试。
// 夹具取自 2026-11-03 站点真实页面；站点改版时会先在这里炸掉，而不是等到用户看见空列表。
//
// 关键回归点（都用真实 HTML 验证过）：
// 1) 频道页把「热播/高分/收藏」三个排序面板内嵌成 <template>，同一批剧出现 3~4 次
//    （实测 80 张卡 / 60 个唯一 ID）——必须优先取 .hg-card-grid.is-active 并按 ID 去重；
// 2) 卡片封面在 src 上是占位图，真图在 data-src；
// 3) 更新态要优先读 data-ep-base，否则会带出 "3小时前" 前缀；
// 4) 播放帧第 1 个 slide 才是当前集，后面 8 个是「猜你喜欢」推荐位，取错会播成别的剧。

public class XiguaDramaParserTests
{
    private const string BaseUrl = "https://xiguaai.net";

    private static string Load(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    [Fact]
    public void ParseCards_Newest_ReturnsTwentyUniqueItems()
    {
        var items = XiguaDramaParser.ParseCards(Load("xigua-list-newest.html"), BaseUrl);

        Assert.Equal(20, items.Count);
        Assert.Equal(20, items.Select(i => i.Id).Distinct().Count());

        var first = items[0];
        Assert.Equal("6482", first.Id);
        Assert.Equal("苗疆少女", first.Title);
        Assert.Equal("7.8", first.ScoreText);
        Assert.Equal("更新至2集", first.EpisodeText);
        Assert.Equal("新上", first.Badge);
        Assert.Equal("AI成人短剧", first.ChannelName);
        Assert.StartsWith("https://pic.drleao.cn/", first.CoverUrl);
        Assert.Equal($"{BaseUrl}/detail/6482/", first.DetailUrl);
        Assert.NotEmpty(first.Tags);
    }

    [Fact]
    public void ParseCards_ChannelPage_ScopesToActiveGridAndDeduplicates()
    {
        // 整页有 80 张卡（含 hot/score/collect 三个 template 面板），唯一 ID 只有 60 个。
        var items = XiguaDramaParser.ParseCards(Load("xigua-channel-ai-duanju.html"), BaseUrl);

        Assert.Equal(20, items.Count);
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct().Count());
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.DetailUrl)));
    }

    [Fact]
    public void ParseCards_UpdateText_DropsRelativeTimePrefix()
    {
        var items = XiguaDramaParser.ParseCards(Load("xigua-list-newest.html"), BaseUrl);
        // 卡片上的 "3小时前" 在 <i> 里，不能混进更新态
        Assert.DoesNotContain(items, i => i.EpisodeText.Contains('前'));
        Assert.Contains(items, i => i.EpisodeText.StartsWith("全", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseTotalPages_Newest_ReadsPagerTemplate()
    {
        var doc = XiguaDramaParser.ParseDocument(Load("xigua-list-newest.html"));
        var pages = XiguaDramaParser.ParseTotalPages(doc);
        Assert.NotNull(pages);
        Assert.True(pages > 10, $"应解析出多页，实际 {pages}");
    }

    [Fact]
    public void ParseDetail_5033_ReturnsFullEpisodeList()
    {
        var detail = XiguaDramaParser.ParseDetail(Load("xigua-detail-5033.html"), BaseUrl);

        Assert.NotNull(detail);
        Assert.Equal("5033", detail!.Id);
        Assert.Equal("妈妈和姐姐用了我的系统都说好", detail.Title);
        Assert.Equal("8.4", detail.ScoreText);
        Assert.Equal(261, detail.DurationSeconds);
        Assert.Equal(6, detail.TotalEpisodes);
        Assert.Equal(6, detail.Episodes.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6], detail.Episodes.Select(e => e.Number).ToArray());
        Assert.Equal("全6集", detail.EpisodeText);
        Assert.Equal("2026-10-29", detail.ReleaseText);
        Assert.Equal("AI成人短剧", detail.ChannelName);
        Assert.Contains("乱伦", detail.Tags);
        Assert.StartsWith("https://pic.drleao.cn/", detail.CoverUrl);
        Assert.False(string.IsNullOrWhiteSpace(detail.Description));
        // 详情解析不应产生任何流地址（取流是独立调用）
        Assert.All(detail.Episodes, e => Assert.Null(e.StreamUrl));
    }

    [Fact]
    public void ParsePlayFrame_Ep6_ReturnsThisEpisodeStreamOnly()
    {
        var frame = XiguaDramaParser.ParsePlayFrame(
            Load("xigua-playframe-5033-ep6.html"), "5033", 6);

        Assert.NotNull(frame);
        Assert.Equal("5033", frame!.DramaId);
        Assert.Equal(6, frame.Episode);
        Assert.Contains(".m3u8", frame.StreamUrl);
        // AngleSharp 已把 &amp; 解码成 &，不能再残留实体
        Assert.DoesNotContain("&amp;", frame.StreamUrl);
        Assert.Contains("auth_key=", frame.StreamUrl);
        Assert.Equal("4:21", frame.DurationText);
        // 第 6 集是最后一集：没有下一集，但有上一集
        Assert.True(string.IsNullOrEmpty(frame.NextEpisodePath));
        Assert.Contains("ep=5", frame.PrevEpisodePath);
    }

    [Fact]
    public void ParseRank_Hot_ReturnsTwentyRowsWithHeat()
    {
        var items = XiguaDramaParser.ParseRank(Load("xigua-rank-hot.html"), BaseUrl);

        Assert.Equal(20, items.Count);
        Assert.Equal(1, items[0].Rank);
        Assert.Equal("5033", items[0].Id);
        Assert.True(int.TryParse(items[0].HeatText, out var heat) && heat > 0,
            $"热度应可解析为数字，实际「{items[0].HeatText}」");
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Title)));
    }

    [Fact]
    public void ParseTags_ReturnsDistinctSlugs()
    {
        var tags = XiguaDramaParser.ParseTags(Load("xigua-tags.html"), BaseUrl);

        Assert.True(tags.Count >= 20, $"标签数量异常：{tags.Count}");
        Assert.Equal(tags.Count, tags.Select(t => t.Slug).Distinct().Count());
        Assert.Contains(tags, t => t.Slug == "luanlun");
    }

    [Fact]
    public void ParseHosts_GoHome_ListsMirrorDomains()
    {
        var hosts = XiguaDramaParser.ParseHosts(Load("xigua-go-home.html"));

        Assert.NotEmpty(hosts);
        Assert.Contains(hosts, h => h.Contains("xiguaai.net", StringComparison.OrdinalIgnoreCase));
        Assert.All(hosts, h => Assert.StartsWith("https://", h));
    }

    [Theory]
    [InlineData(DramaFeedKind.Recommend, 1, null, "/recommend")]
    [InlineData(DramaFeedKind.Recommend, 3, null, "/recommend/3/")]
    [InlineData(DramaFeedKind.Newest, 2, null, "/newest/2/")]
    [InlineData(DramaFeedKind.Channel, 1, "ai-duanju", "/ai-duanju/")]
    [InlineData(DramaFeedKind.Channel, 4, "ai-manju", "/ai-manju/4/")]
    // 标签分页是 /tag/{slug}/page/{n}/，与频道 /{slug}/{n}/ 不同，这条专防写错模板
    [InlineData(DramaFeedKind.Tag, 1, "luanlun", "/tag/luanlun/")]
    [InlineData(DramaFeedKind.Tag, 2, "luanlun", "/tag/luanlun/page/2/")]
    public void FeedPath_MatchesSiteUrlScheme(DramaFeedKind kind, int page, string? scope, string expected)
    {
        Assert.Equal(expected, XiguaDramaConstants.FeedPath(kind, page, scope));
    }

    [Fact]
    public void PlayFramePath_AlwaysCarriesEpisode()
    {
        Assert.Equal("/video/5033/play-frame/?ep=3", XiguaDramaConstants.PlayFramePath("5033", 3));
        Assert.Equal("/video/5033/play-frame/?ep=1", XiguaDramaConstants.PlayFramePath("5033", 0));
    }
}
