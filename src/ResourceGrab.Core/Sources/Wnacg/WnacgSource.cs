using ResourceGrab.Core.Utils;

namespace ResourceGrab.Core.Sources.Wnacg;

/// <summary>
/// 绅士漫画（wnacg.com）内容源：纯 HTML 抓取，无需登录。
/// 搜索 / 详情 / 图片列表 / 分类均已适配到 IComicSource 通用接口。
/// </summary>
public class WnacgSource : IComicSource, ICategorySource, IRankSource
{
    private readonly WnacgHttpClient _client;

    public WnacgSource(WnacgHttpClient client)
    {
        _client = client;
    }

    public ComicSourceInfo Info { get; } = new()
    {
        Id = "wnacg",
        DisplayName = "绅士漫画",
        RequiresLogin = false,
        SupportsSearchSort = false,
        SupportsCategories = true,
        SupportsRank = true,
        CoverHeaders = new Dictionary<string, string>
        {
            ["Referer"] = "https://www.wnacg.com/",
        },
        // wnacg 无登录态，图片并发调低以避免触发站点 IP 限流（429）
        MaxImageConcurrency = 8,
        MaxChapterConcurrency = 2,
        MaxUrlFetchConcurrency = 2,
    };

    public async Task<SearchResult> SearchAsync(string keyword, int page, CancellationToken ct = default)
    {
        var path = $"/search/index.php?q={Uri.EscapeDataString(keyword)}&syn=yes&f=_all&s=create_time_DESC&p={page}";
        var html = await _client.GetHtmlAsync(path, ct);
        var result = WnacgHtmlParser.ParseSearchResults(html);
        return ToSearchResult(result);
    }

    public async Task<ComicDetail> GetComicAsync(string comicId, CancellationToken ct = default)
    {
        var html = await _client.GetHtmlAsync($"/photos-index-aid-{comicId}.html", ct);
        var detail = WnacgHtmlParser.ParseComicDetail(html);
        var id = string.IsNullOrEmpty(detail.Id) ? comicId : detail.Id;
        var title = detail.Title.Length > 0 ? detail.Title : id;

        return new ComicDetail
        {
            Id = id,
            Title = title,
            CoverUrl = detail.Cover,
            Description = detail.Intro,
            Authors = detail.Category.Length > 0 ? new List<string> { detail.Category } : new List<string>(),
            Tags = detail.Tags,
            // wnacg 无"章节"概念：整册建模为单个章节
            Chapters = new List<Chapter>
            {
                new()
                {
                    Id = id,
                    NumericId = long.TryParse(id, out var numeric) ? numeric : null,
                    Title = "全一册",
                    ComicId = id,
                    ComicTitle = FilenameFilter.Filter(title),
                    SourceId = "wnacg",
                },
            },
        };
    }

    public async Task<IReadOnlyList<ImagePage>> GetChapterImagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        var id = chapter.NumericId?.ToString() ?? chapter.Id;
        var html = await _client.GetHtmlAsync($"/photos-gallery-aid-{id}.html", ct);
        var urls = WnacgHtmlParser.ParseImgList(html);

        var headers = new Dictionary<string, string>
        {
            ["Referer"] = _client.BaseUrl + "/",
            ["User-Agent"] = WnacgConstants.UserAgent,
        };
        return urls.Select(url => new ImagePage
        {
            Url = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https:" + url,
            Headers = headers,
        }).ToList();
    }

    // ====================== ICategorySource ======================

    public async Task<IReadOnlyList<ComicCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var html = await _client.GetHtmlAsync("/", ct);
        return WnacgHtmlParser.ParseCategories(html);
    }

    // ====================== IRankSource ======================

    private static readonly IReadOnlyList<RankPeriodInfo> RankPeriods = new List<RankPeriodInfo>
    {
        new() { Id = "day", Name = "今日" },
        new() { Id = "week", Name = "本週" },
        new() { Id = "month", Name = "本月" },
        new() { Id = "year", Name = "本年" },
    };

    public IReadOnlyList<RankPeriodInfo> GetRankPeriods() => RankPeriods;

    public async Task<SearchResult> GetRankAsync(string periodId, int page, CancellationToken ct = default)
    {
        // 收藏排行榜：第一页走 type-{period}-cate 历史路径，翻页走 page-{n}-type-{period}
        var path = page <= 1
            ? $"/albums-favorite_ranking-type-{periodId}-cate.html"
            : $"/albums-favorite_ranking-page-{page}-type-{periodId}.html";
        var html = await _client.GetHtmlAsync(path, ct);
        return ToSearchResult(WnacgHtmlParser.ParseSearchResults(html));
    }

    public async Task<SearchResult> GetCategoryComicsAsync(string categoryId, int page, CancellationToken ct = default)
    {
        var html = await _client.GetHtmlAsync($"/albums-index-page-{page}-cate-{categoryId}.html", ct);
        var result = WnacgHtmlParser.ParseSearchResults(html);
        return ToSearchResult(result);
    }

    private static SearchResult ToSearchResult(WnacgHtmlParser.SearchParseResult result) => new()
    {
        Items = result.Items,
        // 精确总命中数未知，按 每页24条 估算供 UI 显示
        Total = result.TotalPages * 24,
        TotalPages = result.TotalPages,
    };
}

