using System.Text.Json;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Sources.Mzt;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

// 妹子图（mzt.111404.xyz）写真源解析测试。
// 夹具是**按真实接口结构手写的合成 JSON**（不含站点实际内容），字段依据 2026-10 联网冒烟核实：
//   · 列表 / 搜索 / 随机：{ items:[{id,title,urls[],created_at,updated_at}], page, pageSize, total }
//   · 详情：{ item:{ ...与列表项同构... } }
//   · 标签：{ items:[{id,tag_name,count}], total }
// 关键回归点：
//   1) 列表项自带的 urls 数组就是**完整图片序列**（无需再进详情页逐页拼接）；
//   2) 标签条目的 Id 取**标签名**（站点没有标签筛选端点，前端点击标签也是跳 /?q={标签名}）；
//   3) totalPages 由 total / pageSize 计算，缺失 total 时下限为 1。

public class MztSourceTests
{
    private const string ListJson = """
        {
          "items": [
            {
              "id": 1001,
              "title": "Demo Album One",
              "urls": [
                "https://i.postimg.cc/aaa/1-(1000).jpg",
                "https://i.postimg.cc/bbb/1-(1001).jpg",
                "https://i.postimg.cc/ccc/1-(1002).jpg"
              ],
              "created_at": "2026-10-01 12:00:00",
              "updated_at": "2026-10-01 12:00:00"
            },
            {
              "id": 1002,
              "title": "Demo Album Two",
              "urls": ["https://i.postimg.cc/ddd/2-(2000).jpg"],
              "created_at": "2026-10-02 12:00:00",
              "updated_at": "2026-10-02 12:00:00"
            }
          ],
          "page": 1,
          "pageSize": 20,
          "total": 43758
        }
        """;

    // ====================== 列表 / 搜索 / 随机 ======================

    [Fact]
    public void ParseList_ReadsItemsAndTakesCoverFromFirstImage()
    {
        using var doc = JsonDocument.Parse(ListJson);
        var result = MztParser.ParseList(doc);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("1001", result.Items[0].Id);
        Assert.Equal("Demo Album One", result.Items[0].Title);
        // 封面取 urls 首图（站点不在列表项里单独给封面字段）
        Assert.Equal("https://i.postimg.cc/aaa/1-(1000).jpg", result.Items[0].CoverUrl);
    }

    [Fact]
    public void ParseList_ComputesTotalPagesFromTotalAndPageSize()
    {
        using var doc = JsonDocument.Parse(ListJson);
        var result = MztParser.ParseList(doc);

        Assert.Equal(43758L, result.Total);
        // ceil(43758 / 20) = 2188
        Assert.Equal(2188L, result.TotalPages);
    }

    [Fact]
    public void ParseList_TreatsNumericIdAsString()
    {
        using var doc = JsonDocument.Parse(ListJson);
        Assert.Equal("1002", MztParser.ParseList(doc).Items[1].Id);
    }

    [Fact]
    public void ParseList_WhenNoTotal_TotalPagesFallsBackToOne()
    {
        using var doc = JsonDocument.Parse("""{"items":[],"page":1,"pageSize":20}""");
        var result = MztParser.ParseList(doc);

        Assert.Empty(result.Items);
        Assert.Equal(0L, result.Total);
        Assert.Equal(1L, result.TotalPages);
    }

    [Fact]
    public void ParseList_SkipsItemsWithoutId()
    {
        using var doc = JsonDocument.Parse("""
            {"items":[{"title":"no id","urls":[]},{"id":7,"title":"ok","urls":[]}],"total":2,"pageSize":20}
            """);
        var result = MztParser.ParseList(doc);

        var item = Assert.Single(result.Items);
        Assert.Equal("7", item.Id);
    }

    // ====================== 详情 ======================

    [Fact]
    public void ParseDetail_ReturnsFullImageListInSiteOrder()
    {
        using var doc = JsonDocument.Parse("""
            {"item":{"id":1001,"title":"Demo Album One",
              "urls":["https://i.postimg.cc/a/1.jpg","https://i.postimg.cc/b/2.jpg","https://i.postimg.cc/c/3.jpg"]}}
            """);
        var item = MztParser.ParseDetail(doc);

        Assert.NotNull(item);
        Assert.Equal("1001", item!.Id);
        Assert.Equal(3, item.ImageUrls.Count);
        // 数组顺序即站点顺序，**不得按数字重排**
        Assert.Equal("https://i.postimg.cc/a/1.jpg", item.ImageUrls[0]);
        Assert.Equal("https://i.postimg.cc/c/3.jpg", item.ImageUrls[2]);
    }

    [Fact]
    public void ParseDetail_WithoutItemNode_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{"message":"not found"}""");
        Assert.Null(MztParser.ParseDetail(doc));
    }

    [Fact]
    public void ParseDetail_DropsBlankImageEntries()
    {
        using var doc = JsonDocument.Parse("""
            {"item":{"id":5,"title":"t","urls":["https://x/1.jpg","",null,"https://x/2.jpg"]}}
            """);
        var item = MztParser.ParseDetail(doc);

        Assert.NotNull(item);
        Assert.Equal(2, item!.ImageUrls.Count);
    }

    // ====================== 标签 ======================

    [Fact]
    public void ParseTags_UsesTagNameAsItemId()
    {
        using var doc = JsonDocument.Parse("""
            {"items":[{"id":94,"tag_name":"DEMO-SET","count":2136},{"id":4,"tag_name":"Another-Sample","count":12}],"total":2}
            """);
        var tags = MztParser.ParseTags(doc);

        Assert.Equal(2, tags.Count);
        // Id 取标签名而非数字 id：站点没有标签筛选端点，浏览时直接拼 q={标签名}
        Assert.Equal("DEMO-SET", tags[0].Id);
        Assert.Equal("DEMO-SET", tags[0].Name);
        Assert.Equal(2136, tags[0].Count);
    }

    [Fact]
    public void ParseTags_WithoutCount_LeavesCountNull()
    {
        using var doc = JsonDocument.Parse("""{"items":[{"id":1,"tag_name":"X"}]}""");
        var tags = MztParser.ParseTags(doc);

        Assert.Null(Assert.Single(tags).Count);
    }

    [Fact]
    public void ParseTags_SkipsEntriesWithoutName()
    {
        using var doc = JsonDocument.Parse("""{"items":[{"id":1},{"id":2,"tag_name":"Y"}]}""");
        var tags = MztParser.ParseTags(doc);

        Assert.Equal("Y", Assert.Single(tags).Name);
    }

    // ====================== 源能力声明 ======================

    [Fact]
    public void Source_DeclaresPhotobookKindWithTagOnlyAndRandomSupport()
    {
        var source = new MztSource(new MztHttpClient());

        Assert.Equal(ResourceKind.Photobook, source.Info.Kind);
        Assert.Equal("mzt", source.Info.Id);
        // 站点只有标签一个维度（无分类 / Coser / 系列 / 角色 / 专题）
        Assert.Equal(TaxonomyKind.Tag, Assert.Single(source.Info.Taxonomies));
        // 实现随机能力 → 写真导航会多出「随机」项
        Assert.IsAssignableFrom<IRandomPhotobookSource>(source);
        Assert.IsAssignableFrom<ITaxonomySource>(source);
    }

    [Fact]
    public async Task Source_UnsupportedTaxonomyDimension_ReturnsEmptyWithoutRequest()
    {
        // 非标签维度不该发请求，直接返回空
        var source = new MztSource(new MztHttpClient());

        Assert.Empty(await source.GetTaxonomyItemsAsync(TaxonomyKind.Coser));

        var browsed = await source.BrowseByTaxonomyAsync(TaxonomyKind.Topic, "x", 1);
        Assert.Empty(browsed.Items);
    }

    // ====================== URL 构造 ======================

    [Fact]
    public void Constants_BuildsListUrlWithOrWithoutKeyword()
    {
        Assert.Equal(
            "https://mzt.111404.xyz/urls?page=2&pageSize=20",
            MztConstants.List(2));
        Assert.Equal(
            "https://mzt.111404.xyz/urls?page=1&pageSize=20&q=DEMO%20SET",
            MztConstants.List(1, MztConstants.PageSize, "DEMO SET"));
    }

    [Fact]
    public void Constants_ClampsPageToAtLeastOne()
    {
        Assert.Contains("page=1", MztConstants.List(0));
        Assert.Contains("page=1", MztConstants.List(-5));
    }
}
