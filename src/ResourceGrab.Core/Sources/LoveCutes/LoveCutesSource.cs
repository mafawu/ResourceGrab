using ResourceGrab.Core.Utils;

namespace ResourceGrab.Core.Sources.LoveCutes;

/// <summary>
/// 爱妹子（lovecutes.com）写真图集源。
/// <para>
/// 内容模型是**图集 → 图片序列**，无章节层级：整册建模为单个 <see cref="Chapter"/>。
/// 数据通道以详情页 JSON-LD <c>ImageGallery</c> 为主（图片直链 + 分页信息一次给全），
/// 列表 / 索引页走 HTML 卡片与 JSON-LD <c>ItemList</c>。
/// </para>
/// <para>
/// 与漫画源的关键差异：详情页每页只含 10 张图，完整图集必须按 <c>/page/{n}/</c> 逐页拼接；
/// 且文件名序号未补零（<c>0-</c>、<c>1-</c>、<c>10-</c>、<c>100-</c>…），站点自身的顺序即
/// JSON-LD <c>position</c> 顺序，**不可按数字重排**，照拼接顺序落盘即可。
/// </para>
/// </summary>
public class LoveCutesSource : IComicSource, ITaxonomySource
{
    private readonly LoveCutesHttpClient _client;

    public LoveCutesSource(LoveCutesHttpClient client) => _client = client;

    public ComicSourceInfo Info { get; } = new()
    {
        Kind = ResourceKind.Photobook,
        Id = "lovecutes",
        DisplayName = "爱妹子",
        RequiresLogin = false,
        SupportsSearchSort = false,
        Taxonomies =
        [
            TaxonomyKind.Category, TaxonomyKind.Tag, TaxonomyKind.Coser,
            TaxonomyKind.Franchise, TaxonomyKind.Character, TaxonomyKind.Topic,
        ],
        // 站点 CDN 不校验 Referer，但 Cloudflare 会拒空 UA（403）。
        // 封面统一由 App 侧 Common/ImageLoader 加载（其 HttpClient 自带浏览器 UA），
        // 故此处无需附加请求头；留空字典以便站点日后加防盗链时集中补。
        CoverHeaders = new Dictionary<string, string>(),
        MaxImageConcurrency = 16,
        MaxChapterConcurrency = 3,
        MaxUrlFetchConcurrency = 4,
    };

    // ====================== IComicSource ======================

    public async Task<SearchResult> SearchAsync(string keyword, int page, CancellationToken ct = default)
    {
        // 空关键词 = 写真首页：走站点的「最新」列表。
        // 不能落到 /search/?s=（空关键词会得到"找到 0 个结果"的空页），故先分流。
        if (string.IsNullOrWhiteSpace(keyword))
        {
            var latest = await _client.GetHtmlAsync(LoveCutesConstants.Latest(page), ct);
            // 浏览类列表报告真实总数，不套用搜索上限
            return ToSearchResult(LoveCutesParser.ParseArticleList(latest));
        }

        var html = await _client.GetHtmlAsync(LoveCutesConstants.Search(keyword, page), ct);
        // 搜索命中由站点封顶 1000，报告值超过时按上限截断
        return ToSearchResult(LoveCutesParser.ParseArticleList(html), LoveCutesConstants.SearchResultCap);
    }

    public async Task<ComicDetail> GetComicAsync(string comicId, CancellationToken ct = default)
    {
        var html = await _client.GetHtmlAsync(LoveCutesConstants.Article(comicId), ct);
        var gallery = LoveCutesParser.ParseGallery(html);
        var title = gallery.Title.Length > 0 ? gallery.Title : comicId;

        return new ComicDetail
        {
            Id = comicId,
            Title = title,
            CoverUrl = gallery.CoverUrl,
            Description = gallery.Description,
            Authors = ExtractAuthor(title),
            Tags = gallery.Keywords,
            // 图集无章节层级：整册即一个章节。Title 是下载目录的末级目录名，
            // 沿用既有源对单册内容的约定字面量「全一册」，否则会出现
            // {图集名}/{图集名}/ 的双层同名目录（见 DownloadManager 的落盘规则）。
            Chapters =
            [
                new Chapter
                {
                    Id = comicId,
                    NumericId = long.TryParse(comicId, out var numeric) ? numeric : null,
                    Title = "全一册",
                    ComicId = comicId,
                    ComicTitle = FilenameFilter.Filter(title),
                    SourceId = "lovecutes",
                },
            ],
        };
    }

    public async Task<IReadOnlyList<ImagePage>> GetChapterImagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        var id = chapter.NumericId?.ToString() ?? chapter.Id;
        var headers = new Dictionary<string, string>
        {
            ["Referer"] = _client.BaseUrl + "/",
            ["User-Agent"] = LoveCutesConstants.UserAgent,
        };

        var first = LoveCutesParser.ParseGallery(
            await _client.GetHtmlAsync(LoveCutesConstants.Article(id), ct));
        var urls = new List<string>(first.ImageUrls);

        // 详情页每页仅 10 张，完整图集必须逐页拼接；顺序保持站点 order，不做数字重排
        var lastPage = Math.Min(first.TotalPages, LoveCutesConstants.MaxGalleryPages);
        for (var page = 2; page <= lastPage; page++)
        {
            var html = await _client.GetHtmlAsync(LoveCutesConstants.ArticlePage(id, (int)page), ct);
            urls.AddRange(LoveCutesParser.ParseGallery(html).ImageUrls);
        }

        return urls
            .Where(u => u.Length > 0)
            .Select(u => new ImagePage { Url = u, Headers = headers })
            .ToList();
    }

    // ====================== ITaxonomySource ======================

    public async Task<IReadOnlyList<TaxonomyItem>> GetTaxonomyItemsAsync(TaxonomyKind kind, CancellationToken ct = default)
    {
        switch (kind)
        {
            case TaxonomyKind.Category:
            {
                var html = await _client.GetHtmlAsync(LoveCutesConstants.CategoryNavPath, ct);
                return LoveCutesParser.ParseCategoryNav(html)
                    .Select(c => new TaxonomyItem(c.Id, c.Name))
                    .ToList();
            }

            case TaxonomyKind.Tag:
            {
                var html = await _client.GetHtmlAsync(LoveCutesConstants.TagIndexPath, ct);
                return LoveCutesParser.ParseTaxonomyIndex(html).Items;
            }

            case TaxonomyKind.Coser:
            {
                // 站点的"人"分两类：creator（创作者/社团，106）与 performer（出演模特，2166）。
                // 合并为一个 Coser 维度，条目 id 用 "creator/xxx" / "performer/xxx" 区分。
                var people = new List<TaxonomyItem>();
                people.AddRange(await EnumerateIndexAsync(p => LoveCutesConstants.EntityIndex("creators", p), ct));
                people.AddRange(await EnumerateIndexAsync(p => LoveCutesConstants.EntityIndex("performers", p), ct));
                return people;
            }

            case TaxonomyKind.Franchise:
                return await EnumerateIndexAsync(p => LoveCutesConstants.EntityIndex("franchises", p), ct);

            case TaxonomyKind.Character:
                return await EnumerateIndexAsync(p => LoveCutesConstants.EntityIndex("characters", p), ct);

            case TaxonomyKind.Topic:
                return await EnumerateIndexAsync(LoveCutesConstants.TopicsIndexPage, ct);

            default:
                return [];
        }
    }

    public async Task<SearchResult> BrowseByTaxonomyAsync(
        TaxonomyKind kind, string itemId, int page, CancellationToken ct = default)
    {
        var path = kind switch
        {
            TaxonomyKind.Category => LoveCutesConstants.TypeBrowse(itemId, page),
            TaxonomyKind.Tag => LoveCutesConstants.TagBrowse(itemId, page),
            TaxonomyKind.Coser or TaxonomyKind.Franchise or TaxonomyKind.Character
                => LoveCutesConstants.EntityBrowse(itemId, page),
            TaxonomyKind.Topic => LoveCutesConstants.TopicBrowse(itemId, page),
            _ => throw new JmException($"不支持的写真维度：{kind}"),
        };

        var html = await _client.GetHtmlAsync(path, ct);
        // 浏览页报告的是真实总数（如 /sort/new/ 达 2.7 万部），不套用搜索上限
        return ToSearchResult(LoveCutesParser.ParseArticleList(html));
    }

    /// <summary>按 JSON-LD 报告的总数逐页枚举某个索引（如全部角色、全部专题）。</summary>
    private async Task<List<TaxonomyItem>> EnumerateIndexAsync(Func<int, string> pathForPage, CancellationToken ct)
    {
        var all = new List<TaxonomyItem>();

        for (var page = 1; page <= LoveCutesConstants.MaxTaxonomyIndexPages; page++)
        {
            var html = await _client.GetHtmlAsync(pathForPage(page), ct);
            var index = LoveCutesParser.ParseTaxonomyIndex(html);
            if (index.Items.Count == 0) break;

            all.AddRange(index.Items);

            var totalPages = index.Total > 0
                ? (long)Math.Ceiling(index.Total / (double)LoveCutesConstants.PageSize)
                : 1;
            if (page >= totalPages) break;
        }

        return all;
    }

    // ====================== 内部工具 ======================

    /// <param name="cap">总数上限；搜索用站点封顶，浏览页传 null（报告真实总数）。</param>
    private static SearchResult ToSearchResult(LoveCutesParser.ListParseResult result, long? cap = null) => new()
    {
        Items = result.Items,
        // 站点未报告总数时按每页 16 估算（仅供 UI 显示）
        Total = Math.Min(
            result.Total > 0 ? result.Total : result.TotalPages * LoveCutesConstants.PageSize,
            cap ?? long.MaxValue),
        TotalPages = result.TotalPages,
    };

    /// <summary>从标题开头的 <c>[DJAWA]</c> / <c>【社团】</c> 标记提取作者（Coser / 社团）。</summary>
    internal static List<string> ExtractAuthor(string title)
    {
        var author = LoveCutesParser.ExtractAuthorPrefix(title);
        return author.Length > 0 ? [author] : [];
    }
}
