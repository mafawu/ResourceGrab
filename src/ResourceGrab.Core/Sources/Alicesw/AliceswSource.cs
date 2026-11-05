using ResourceGrab.Core.Sources.Novel;

namespace ResourceGrab.Core.Sources.Alicesw;

/// <summary>
/// 爱丽丝书屋书源（alicesw.com / alicesw.tw）。
/// 站点是服务端渲染的模板站，正文为纯 HTML 段落，无签名、无加密、无需登录。
/// </summary>
public class AliceswSource : INovelSource
{
    private readonly AliceswHttpClient _client;

    public AliceswSource(AliceswHttpClient client)
    {
        _client = client;
    }

    public ComicSourceInfo Info { get; } = new()
    {
        Kind = ResourceKind.Novel,
        Id = AliceswConstants.SourceId,
        DisplayName = "爱丽丝书屋",
        RequiresLogin = false,
        SupportsSearchSort = false,
        SupportsCategories = true,
        SupportsRank = false,
        SupportsFavorites = false,
        CoverHeaders = new Dictionary<string, string>
        {
            ["Referer"] = "https://" + AliceswConstants.DefaultDomain + "/",
        },
    };

    public async Task<NovelSearchResult> SearchAsync(
        string keyword, int page, NovelSearchField field = NovelSearchField.All, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return new NovelSearchResult { Page = page, TotalPages = 1 };

        var html = await _client.GetHtmlAsync(AliceswConstants.SearchPath(keyword, page, field), ct);
        return AliceswParser.ParseSearch(html, Math.Max(1, page));
    }

    public async Task<NovelDetail> GetDetailAsync(string novelId, CancellationToken ct = default)
    {
        var html = await _client.GetHtmlAsync(AliceswConstants.DetailPath(novelId), ct);
        var detail = AliceswParser.ParseDetail(html, novelId);
        var chapters = await GetChaptersAsync(novelId, ct);
        return new NovelDetail
        {
            Id = detail.Id,
            Title = detail.Title,
            Author = detail.Author,
            CoverUrl = ResolveUrl(detail.CoverUrl),
            Intro = detail.Intro,
            Category = detail.Category,
            Status = detail.Status,
            WordCountText = detail.WordCountText,
            ChapterCount = detail.ChapterCount > 0 ? detail.ChapterCount : chapters.Count,
            Tags = detail.Tags,
            LatestChapterTitle = detail.LatestChapterTitle,
            Chapters = chapters,
        };
    }

    public async Task<IReadOnlyList<NovelChapter>> GetChaptersAsync(string novelId, CancellationToken ct = default)
    {
        var html = await _client.GetHtmlAsync(AliceswConstants.ChaptersPath(novelId), ct);
        return AliceswParser.ParseChapters(html);
    }

    /// <summary>封面可能是相对路径，统一补成绝对地址。</summary>
    private string ResolveUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
        if (url.StartsWith("//", StringComparison.Ordinal)) return "https:" + url;
        return _client.BaseUrl + (url.StartsWith('/') ? url : "/" + url);
    }

    public Task<string> GetChapterTextAsync(NovelChapter chapter, CancellationToken ct = default)
    {
        var path = string.IsNullOrEmpty(chapter.BookId) || string.IsNullOrEmpty(chapter.Key)
            ? chapter.Url
            : AliceswConstants.ChapterPath(chapter.BookId, chapter.Key);
        return GetTextCoreAsync(path, ct);
    }

    private async Task<string> GetTextCoreAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new JmException("章节地址为空");
        var html = await _client.GetHtmlAsync(path, ct);
        var text = AliceswParser.ParseChapterText(html);
        if (string.IsNullOrWhiteSpace(text))
            throw new JmException("这一章没取到正文（站点可能改了结构或该章需登录）");
        return text;
    }

    // ------------------------------------------------------------ 分类 / 排行

    private IReadOnlyList<NovelCategory>? _categories;

    public async Task<IReadOnlyList<NovelCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        if (_categories is { Count: > 0 }) return _categories;

        var html = await _client.GetHtmlAsync("/", ct);
        var parsed = AliceswParser.ParseCategories(html);
        if (parsed.Count > 0) _categories = parsed;
        return parsed;
    }

    public async Task<NovelSearchResult> GetCategoryAsync(
        string categoryId, int page, NovelListSort sort = NovelListSort.Update, CancellationToken ct = default)
    {
        var html = await _client.GetHtmlAsync(AliceswConstants.CategoryPath(categoryId, page, sort), ct);
        return AliceswParser.ParseList(html, Math.Max(1, page));
    }

    public async Task<NovelSearchResult> GetRankAsync(NovelRankPeriod period, CancellationToken ct = default)
    {
        var html = await _client.GetHtmlAsync(AliceswConstants.RankPath(period), ct);
        return AliceswParser.ParseList(html, 1);
    }
}
