using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Sources.LoveCutes;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

// lovecutes 写真源解析测试。
// 夹具是**按真实站点结构手写的合成 HTML/JSON-LD**（不含站点实际内容），
// 结构依据 2026-10 联网冒烟核实：
//   · 列表卡片 article.excerpt[data-track-content-id][data-content-kind] + a.imgbox-link + img.imgbox-img
//   · 详情页 JSON-LD ImageGallery（每页 10 张 + pagination.totalPages），标题在 h1
//   · 维度索引 JSON-LD CollectionPage.mainEntity.itemListElement
// 关键回归点：
//   1) 详情页每页只含 10 张，完整图集需按 /page/{n}/ 拼接；
//   2) 文件名序号未补零（0-、1-、10-…），站点顺序即 position 顺序，**不得按数字重排**。

public class LoveCutesSourceTests
{
    // ====================== 列表 / 搜索 ======================

    private const string ListHtml = """
        <html><body>
        <div class="excerpts image-container">
          <article class="excerpt excerpt-c4" data-track-content-id="1001" data-content-kind="album">
            <div class="thumbnail">
              <a href="/type/9/" class="imgbox-a" title="大尺度美女">大尺度美女</a>
              <a data-content-link class="imgbox imgbox-link" href="/article/1001/" title="[Studio] Model A - Set One 12P">
                <div class="imgbox imgbox-container" data-id="1001">
                  <img class="imgbox-img" src="/static/images/2026/10/01/demo-set-one/__cover.jpg?v=1" alt="[Studio] Model A - Set One 12P">
                </div>
              </a>
            </div>
          </article>
          <article class="excerpt excerpt-c4" data-track-content-id="1002" data-content-kind="video">
            <div class="thumbnail">
              <a class="imgbox imgbox-link" href="/article/1002/" title="视频位，应被跳过">
                <img class="imgbox-img" src="/static/images/2026/10/01/demo-video/__cover.jpg" alt="视频位">
              </a>
            </div>
          </article>
        </div>
        <ul class="pagination">
          <li><a href="/type/9/" data-page="1">1</a></li>
          <li><a href="/type/9/page/2/" data-page="2">2</a></li>
          <li class="disabled ellipsis"><span>...</span></li>
          <li><a href="/type/9/page/7/" data-page="7">7</a></li>
        </ul>
        </body></html>
        """;

    [Fact]
    public void ParseArticleList_SkipsNonAlbumCards_AndReadsCardFields()
    {
        var result = LoveCutesParser.ParseArticleList(ListHtml);

        var item = Assert.Single(result.Items);
        Assert.Equal("1001", item.Id);
        Assert.Equal("[Studio] Model A - Set One 12P", item.Title);
        // 相对路径需补成绝对地址
        Assert.Equal("https://www.lovecutes.com/static/images/2026/10/01/demo-set-one/__cover.jpg?v=1", item.CoverUrl);
    }

    [Fact]
    public void ParseArticleList_TakesMaxPageFromPagination()
    {
        var result = LoveCutesParser.ParseArticleList(ListHtml);
        Assert.Equal(7, result.TotalPages);
    }

    [Fact]
    public void ParseArticleList_WhenNoDataPageAttr_FallsBackToPageLinks()
    {
        const string html = """
            <html><body>
            <a href="/sort/new/page/2/">2</a><a href="/sort/new/page/5/">5</a>
            </body></html>
            """;
        Assert.Equal(5, LoveCutesParser.ParseArticleList(html).TotalPages);
    }

    [Fact]
    public void ParseArticleList_WhenNoPagination_ReturnsSinglePage()
    {
        Assert.Equal(1, LoveCutesParser.ParseArticleList("<html><body></body></html>").TotalPages);
    }

    // ====================== 图集详情 ======================

    private static string GalleryHtml(string h1 = "Sample Set") => $$"""
        <html><head>
        <meta property="og:image" content="https://www.lovecutes.com/static/images/2026/10/01/demo/0-cover.jpg" />
        <script type="application/ld+json">
        {"@context":"https://schema.org","@type":"ImageGallery",
         "name":"Sample Set - 大尺度美女 - 爱妹子","description":"合成夹具：仅供解析测试。",
         "keywords":"tagA, tagB, tagC","numberOfItems":26,
         "pagination":{"@type":"Pagination","currentPage":1,"totalPages":3},
         "itemListElement":[
           {"@type":"ImageObject","position":1,"contentUrl":"https://www.lovecutes.com/static/images/2026/10/01/demo/0-cover.jpg"},
           {"@type":"ImageObject","position":2,"contentUrl":"https://www.lovecutes.com/static/images/2026/10/01/demo/1-cover.jpg"},
           {"@type":"ImageObject","position":3,"contentUrl":"https://www.lovecutes.com/static/images/2026/10/01/demo/10-cover.jpg"}]}
        </script>
        <script type="application/ld+json">
        {"@context":"https://schema.org","@type":"BreadcrumbList","itemListElement":[
          {"@type":"ListItem","position":1,"name":"首页","item":"https://www.lovecutes.com/"},
          {"@type":"ListItem","position":2,"name":"大尺度美女","item":"https://www.lovecutes.com/type/9/"}]}
        </script>
        </head><body><h1>{{h1}}</h1></body></html>
        """;

    [Fact]
    public void ParseGallery_ReadsJsonLdAndHeading()
    {
        var g = LoveCutesParser.ParseGallery(GalleryHtml());

        Assert.Equal("Sample Set", g.Title);          // 优先取 h1，而非带后缀的 JSON-LD name
        Assert.Equal("合成夹具：仅供解析测试。", g.Description);
        Assert.Equal(new[] { "tagA", "tagB", "tagC" }, g.Keywords);
        Assert.Equal(26, g.ImageCount);               // 图集总数，非本页张数
        Assert.Equal(3, g.TotalPages);
        Assert.Equal(1, g.CurrentPage);
        Assert.Equal("9", g.CategoryId);              // 来自面包屑 /type/9/
        Assert.Equal("https://www.lovecutes.com/static/images/2026/10/01/demo/0-cover.jpg", g.CoverUrl);
    }

    [Fact]
    public void ParseGallery_PreservesSiteOrder_WithoutNumericResort()
    {
        var g = LoveCutesParser.ParseGallery(GalleryHtml());

        // 文件名序号未补零，站点顺序即 position 顺序：0 → 1 → 10。
        // 若按数字重排会变成 0 → 1 → 10 看着一样，但 2 与 10 之间就能暴露；此处断言"不排序"。
        Assert.Collection(g.ImageUrls,
            u => Assert.EndsWith("/0-cover.jpg", u),
            u => Assert.EndsWith("/1-cover.jpg", u),
            u => Assert.EndsWith("/10-cover.jpg", u));
    }

    [Fact]
    public void ParseGallery_WithoutH1_StripsCategoryAndSiteSuffix()
    {
        var g = LoveCutesParser.ParseGallery(GalleryHtml(h1: ""));
        Assert.Equal("Sample Set", g.Title);
    }

    [Fact]
    public void ParseGallery_PrefersH1OverJsonLdName()
    {
        // h1 是干净标题；JSON-LD name 带 " - 分类 - 站名" 后缀
        var g = LoveCutesParser.ParseGallery(GalleryHtml(h1: "H1 Clean Title"));
        Assert.Equal("H1 Clean Title", g.Title);
    }

    [Fact]
    public void ParseGallery_RelativeContentUrl_IsAbsolutized()
    {
        const string html = """
            <html><head><script type="application/ld+json">
            {"@type":"ImageGallery","itemListElement":[{"contentUrl":"/static/images/a.jpg"}]}
            </script></head><body></body></html>
            """;
        var g = LoveCutesParser.ParseGallery(html);
        Assert.Equal("https://www.lovecutes.com/static/images/a.jpg", Assert.Single(g.ImageUrls));
        // 无 numberOfItems / pagination 时走默认值
        Assert.Equal(1, g.TotalPages);
        Assert.Equal(0, g.ImageCount);
    }

    // ====================== 维度索引 ======================

    [Fact]
    public void ParseCategoryNav_DedupsAndExtractsIdAndName()
    {
        const string html = """
            <html><body>
            <a href="/type/1/">性感美女</a>
            <a href="/type/9/">大尺度美女</a>
            <a href="/type/9/">大尺度美女</a>
            <a href="/type/">无效</a>
            </body></html>
            """;
        var categories = LoveCutesParser.ParseCategoryNav(html);

        Assert.Equal(2, categories.Count);
        Assert.Equal("1", categories[0].Id);
        Assert.Equal("性感美女", categories[0].Name);
        Assert.Equal("9", categories[1].Id);
    }

    [Fact]
    public void ParseTaxonomyIndex_ReadsCollectionPageMainEntity()
    {
        const string html = """
            <html><head><script type="application/ld+json">
            {"@type":"CollectionPage","mainEntity":{"@type":"ItemList","numberOfItems":503,
             "itemListElement":[
               {"@type":"ListItem","position":1,"name":"大凤","url":"https://www.lovecutes.com/entity/character/da-feng/"},
               {"@type":"ListItem","position":2,"name":"光辉","url":"https://www.lovecutes.com/entity/character/guang-hui/"}]}}
            </script></head><body></body></html>
            """;
        var index = LoveCutesParser.ParseTaxonomyIndex(html);

        Assert.Equal(503, index.Total);
        Assert.Equal(2, index.Items.Count);
        // 实体需保留子路径，以区分 creator / performer / character / franchise
        Assert.Equal("character/da-feng", index.Items[0].Id);
        Assert.Equal("大凤", index.Items[0].Name);
    }

    [Theory]
    [InlineData("https://www.lovecutes.com/tag/1503/", "1503")]
    [InlineData("/tag/1/", "1")]
    [InlineData("/type/9/", "9")]
    [InlineData("/topic/black-silk/", "black-silk")]
    [InlineData("https://www.lovecutes.com/entity/creator/djawa/", "creator/djawa")]
    [InlineData("/entity/franchise/bi-lan-hang-xian/", "franchise/bi-lan-hang-xian")]
    [InlineData("/entities/creators/", "")]
    public void ParseTaxonomyIndex_NormalizesItemIds(string url, string expectedId)
    {
        // 用 $$$ 定界：JSON 结尾的 "}]}}" 含连续右大括号，$$ 会与之冲突
        var html = $$$"""
            <html><head><script type="application/ld+json">
            {"@type":"CollectionPage","mainEntity":{"@type":"ItemList","numberOfItems":1,
             "itemListElement":[{"@type":"ListItem","name":"X","url":"{{{url}}}"}]}}
            </script></head><body></body></html>
            """;
        var index = LoveCutesParser.ParseTaxonomyIndex(html);

        if (expectedId.Length == 0)
        {
            Assert.Empty(index.Items);   // 索引页自身不是可浏览条目
        }
        else
        {
            Assert.Equal(expectedId, Assert.Single(index.Items).Id);
        }
    }

    // ====================== 源级行为（多页拼接 / 章节约定） ======================

    private sealed class FakeLoveCutesClient(Dictionary<string, string> pages) : LoveCutesHttpClient("www.lovecutes.com")
    {
        public List<string> Requests { get; } = [];

        public override Task<string> GetHtmlAsync(string path, CancellationToken ct = default)
        {
            Requests.Add(path);
            if (pages.TryGetValue(path, out var html)) return Task.FromResult(html);
            throw new JmException($"测试未预置该路径的响应：{path}");
        }
    }

    private static string GalleryPage(long current, long total, params string[] files)
    {
        var items = string.Join(",", files.Select((f, i) =>
            $$"""{"@type":"ImageObject","position":{{i + 1}},"contentUrl":"https://www.lovecutes.com/static/images/2026/10/01/demo/{{f}}"}"""));
        return $$"""
            <html><head><script type="application/ld+json">
            {"@type":"ImageGallery","name":"Sample Set","numberOfItems":5,
             "pagination":{"currentPage":{{current}},"totalPages":{{total}}},
             "itemListElement":[{{items}}]}
            </script></head><body><h1>Sample Set</h1></body></html>
            """;
    }

    private static LoveCutesSource CreateSource(out FakeLoveCutesClient client)
    {
        var pages = new Dictionary<string, string>
        {
            ["/article/100/"] = GalleryPage(1, 3, "0-a.jpg", "1-a.jpg"),
            ["/article/100/page/2/"] = GalleryPage(2, 3, "10-a.jpg", "2-a.jpg"),
            ["/article/100/page/3/"] = GalleryPage(3, 3, "3-a.jpg"),
        };
        client = new FakeLoveCutesClient(pages);
        return new LoveCutesSource(client);
    }

    [Fact]
    public async Task GetChapterImagesAsync_ConcatenatesAllPages_InSiteOrder()
    {
        var source = CreateSource(out var client);
        var chapter = new Chapter { Id = "100", NumericId = 100, Title = "全一册", ComicId = "100", SourceId = "lovecutes" };

        var images = await source.GetChapterImagesAsync(chapter);

        Assert.Equal(3, client.Requests.Count);
        Assert.Equal("/article/100/", client.Requests[0]);
        Assert.Equal("/article/100/page/2/", client.Requests[1]);
        Assert.Equal("/article/100/page/3/", client.Requests[2]);

        // 拼接顺序 = 站点顺序；若按数字重排会变成 0,1,2,3,10
        Assert.Collection(images,
            i => Assert.EndsWith("/0-a.jpg", i.Url),
            i => Assert.EndsWith("/1-a.jpg", i.Url),
            i => Assert.EndsWith("/10-a.jpg", i.Url),
            i => Assert.EndsWith("/2-a.jpg", i.Url),
            i => Assert.EndsWith("/3-a.jpg", i.Url));

        // 下载需带 Referer 防盗链头
        Assert.Equal("https://www.lovecutes.com/", images[0].Headers["Referer"]);
        Assert.Equal(0u, images[0].BlockNum);
    }

    [Fact]
    public async Task GetChapterImagesAsync_WhenSinglePage_DoesNotRequestMore()
    {
        var client = new FakeLoveCutesClient(new Dictionary<string, string>
        {
            ["/article/7/"] = GalleryPage(1, 1, "0-a.jpg"),
        });
        var source = new LoveCutesSource(client);

        var images = await source.GetChapterImagesAsync(new Chapter { Id = "7", NumericId = 7 });

        Assert.Single(images);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task GetComicAsync_BuildsSingleChapterWithQuanyiVolumeTitle()
    {
        var client = new FakeLoveCutesClient(new Dictionary<string, string>
        {
            ["/article/100/"] = GalleryHtml(),
        });
        var source = new LoveCutesSource(client);

        var comic = await source.GetComicAsync("100");

        Assert.Equal("100", comic.Id);
        Assert.Equal("Sample Set", comic.Title);
        Assert.Equal(new[] { "tagA", "tagB", "tagC" }, comic.Tags);

        // 单章节约定：目录名固定为「全一册」，避免 {图集名}/{图集名}/ 双层同名目录
        var chapter = Assert.Single(comic.Chapters);
        Assert.Equal("全一册", chapter.Title);
        Assert.Equal("100", chapter.ComicId);
        Assert.Equal(100, chapter.NumericId);
        Assert.Equal("lovecutes", chapter.SourceId);
        Assert.Equal("Sample Set", chapter.ComicTitle);
    }

    [Fact]
    public async Task GetComicAsync_ExtractsAuthorFromBracketedPrefix()
    {
        var client = new FakeLoveCutesClient(new Dictionary<string, string>
        {
            ["/article/100/"] = GalleryHtml(h1: "[DJAWA] Ye Eun - Naughty Trekking 287P"),
        });
        var source = new LoveCutesSource(client);

        var comic = await source.GetComicAsync("100");

        Assert.Equal(new[] { "DJAWA" }, comic.Authors);
        Assert.Equal("[DJAWA] Ye Eun - Naughty Trekking 287P", comic.Chapters[0].ComicTitle);
    }

    // ====================== 维度浏览路径 ======================

    [Theory]
    [InlineData(TaxonomyKind.Category, "9", "/type/9/")]
    [InlineData(TaxonomyKind.Tag, "1503", "/tag/1503/")]
    [InlineData(TaxonomyKind.Coser, "creator/djawa", "/entity/creator/djawa/")]
    [InlineData(TaxonomyKind.Coser, "performer/yang-chen-chen-sugar", "/entity/performer/yang-chen-chen-sugar/")]
    [InlineData(TaxonomyKind.Franchise, "franchise/bi-lan-hang-xian", "/entity/franchise/bi-lan-hang-xian/")]
    [InlineData(TaxonomyKind.Character, "character/guang-hui", "/entity/character/guang-hui/")]
    [InlineData(TaxonomyKind.Topic, "black-silk-stockings", "/topic/black-silk-stockings/")]
    public async Task BrowseByTaxonomyAsync_BuildsExpectedPath(
        TaxonomyKind kind, string itemId, string expectedPath)
    {
        var client = new FakeLoveCutesClient(new Dictionary<string, string>
        {
            [expectedPath] = ListHtml,
        });
        var source = new LoveCutesSource(client);

        var result = await source.BrowseByTaxonomyAsync(kind, itemId, page: 1);

        Assert.Equal(expectedPath, Assert.Single(client.Requests));
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task BrowseByTaxonomyAsync_PaginatesPath()
    {
        var client = new FakeLoveCutesClient(new Dictionary<string, string>
        {
            ["/type/9/page/3/"] = ListHtml,
        });
        var source = new LoveCutesSource(client);

        await source.BrowseByTaxonomyAsync(TaxonomyKind.Category, "9", page: 3);

        Assert.Equal("/type/9/page/3/", Assert.Single(client.Requests));
    }

    [Fact]
    public async Task GetTaxonomyItemsAsync_CoserMergesCreatorsAndPerformers()
    {
        var creatorsPage1 = """
            <html><head><script type="application/ld+json">
            {"@type":"CollectionPage","mainEntity":{"@type":"ItemList","numberOfItems":2,
             "itemListElement":[
               {"@type":"ListItem","name":"DJAWA","url":"https://www.lovecutes.com/entity/creator/djawa/"},
               {"@type":"ListItem","name":"JellyBelly","url":"https://www.lovecutes.com/entity/creator/jellybelly/"}]}}
            </script></head><body></body></html>
            """;
        // 总数 1 页（2 条 / 每页 16），故不会继续翻页
        var performersPage1 = """
            <html><head><script type="application/ld+json">
            {"@type":"CollectionPage","mainEntity":{"@type":"ItemList","numberOfItems":1,
             "itemListElement":[
               {"@type":"ListItem","name":"杨晨晨sugar","url":"https://www.lovecutes.com/entity/performer/yang-chen-chen-sugar/"}]}}
            </script></head><body></body></html>
            """;
        var client = new FakeLoveCutesClient(new Dictionary<string, string>
        {
            ["/entities/creators/"] = creatorsPage1,
            ["/entities/performers/"] = performersPage1,
        });
        var source = new LoveCutesSource(client);

        var items = await source.GetTaxonomyItemsAsync(TaxonomyKind.Coser);

        Assert.Equal(3, items.Count);
        Assert.Equal("creator/djawa", items[0].Id);
        Assert.Equal("performer/yang-chen-chen-sugar", items[2].Id);
    }

    [Fact]
    public async Task GetTaxonomyItemsAsync_EnumeratesPaginatedIndexUntilReportedTotal()
    {
        // 总数 20 → 每页 16 → 2 页
        var page1 = """
            <html><head><script type="application/ld+json">
            {"@type":"CollectionPage","mainEntity":{"@type":"ItemList","numberOfItems":20,
             "itemListElement":[{"@type":"ListItem","name":"A","url":"/entity/character/a/"}]}}
            </script></head><body></body></html>
            """;
        var page2 = """
            <html><head><script type="application/ld+json">
            {"@type":"CollectionPage","mainEntity":{"@type":"ItemList","numberOfItems":20,
             "itemListElement":[{"@type":"ListItem","name":"B","url":"/entity/character/b/"}]}}
            </script></head><body></body></html>
            """;
        var client = new FakeLoveCutesClient(new Dictionary<string, string>
        {
            ["/entities/characters/"] = page1,
            ["/entities/characters/page/2/"] = page2,
        });
        var source = new LoveCutesSource(client);

        var items = await source.GetTaxonomyItemsAsync(TaxonomyKind.Character);

        Assert.Equal(2, items.Count);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task GetTaxonomyItemsAsync_CategoryReadsNavFromTypePage()
    {
        var client = new FakeLoveCutesClient(new Dictionary<string, string>
        {
            ["/type/1/"] = """<html><body><a href="/type/1/">性感美女</a><a href="/type/6/">Cosplay</a></body></html>""",
        });
        var source = new LoveCutesSource(client);

        var items = await source.GetTaxonomyItemsAsync(TaxonomyKind.Category);

        Assert.Equal("/type/1/", Assert.Single(client.Requests));
        Assert.Equal(2, items.Count);
        Assert.Equal("Cosplay", items[1].Name);
    }

    // ====================== 摘要字段 / 搜索上限 ======================

    [Fact]
    public void ParseArticleList_PopulatesCategoryAndAuthorFromCard()
    {
        var item = Assert.Single(LoveCutesParser.ParseArticleList(ListHtml).Items);

        // 卡片内的 a[href^='/type/'] 文本即分类名
        Assert.Equal("大尺度美女", item.Category);
        // 标题前缀 "[Studio]" 即作者/社团
        Assert.Equal("Studio", item.Author);
    }

    [Fact]
    public async Task SearchAsync_ClampsReportedTotalToSiteCap()
    {
        const string html = """
            <html><head><script type="application/ld+json">
            {"@type":"SearchResultsPage","mainEntity":{"@type":"ItemList","numberOfItems":5000,
             "itemListElement":[]}}
            </script></head><body>
            <article class="excerpt excerpt-c4" data-track-content-id="1" data-content-kind="album">
              <a class="imgbox imgbox-link" href="/article/1/" title="T"><img class="imgbox-img" src="/x.jpg"/></a>
            </article>
            </body></html>
            """;
        var client = new FakeLoveCutesClient(new Dictionary<string, string> { ["/search/?s=abc"] = html });
        var source = new LoveCutesSource(client);

        var result = await source.SearchAsync("abc", page: 1);

        // 站点搜索封顶 1000：即使报告 5000 也按上限截断
        Assert.Equal(LoveCutesConstants.SearchResultCap, result.Total);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task BrowseByTaxonomyAsync_KeepsReportedTotal_WithoutSearchCap()
    {
        const string html = """
            <html><head><script type="application/ld+json">
            {"@type":"ItemList","numberOfItems":27150,"itemListElement":[]}
            </script></head><body>
            <article class="excerpt excerpt-c4" data-track-content-id="1" data-content-kind="album">
              <a class="imgbox imgbox-link" href="/article/1/" title="T"><img class="imgbox-img" src="/x.jpg"/></a>
            </article>
            </body></html>
            """;
        var client = new FakeLoveCutesClient(new Dictionary<string, string> { ["/type/9/"] = html });
        var source = new LoveCutesSource(client);

        var result = await source.BrowseByTaxonomyAsync(TaxonomyKind.Category, "9", page: 1);

        // 浏览页报告的是真实总数（实测 /sort/new/ 达 2.7 万部），不套用搜索封顶
        Assert.Equal(27150, result.Total);
    }

    // ====================== 源元信息 ======================

    [Fact]
    public void Info_DeclaresPhotobookKindAndAllSixDimensions()
    {
        var source = new LoveCutesSource(new FakeLoveCutesClient(new Dictionary<string, string>()));

        Assert.Equal(ResourceKind.Photobook, source.Info.Kind);
        Assert.Equal("lovecutes", source.Info.Id);
        Assert.False(source.Info.RequiresLogin);
        Assert.Equal(
            new[]
            {
                TaxonomyKind.Category, TaxonomyKind.Tag, TaxonomyKind.Coser,
                TaxonomyKind.Franchise, TaxonomyKind.Character, TaxonomyKind.Topic,
            },
            source.Info.Taxonomies);
    }
}
