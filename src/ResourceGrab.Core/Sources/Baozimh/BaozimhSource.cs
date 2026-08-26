using ResourceGrab.Core.Utils;

namespace ResourceGrab.Core.Sources.Baozimh;

public class BaozimhSource : IComicSource
{
    private readonly BaozimhHttpClient _client;

    public BaozimhSource(BaozimhHttpClient client)
    {
        _client = client;
    }

    public ComicSourceInfo Info { get; } = new()
    {
        Id = "baozimh",
        DisplayName = "包子漫画",
        RequiresLogin = false,
        SupportsSearchSort = false,
        SupportsCategories = false,
        SupportsRank = false,
        CoverHeaders = new Dictionary<string, string>
        {
            ["Referer"] = BaozimhConstants.Referer,
        },
        MaxImageConcurrency = 8,
        MaxChapterConcurrency = 2,
        MaxUrlFetchConcurrency = 2,
    };

    public async Task<SearchResult> SearchAsync(string keyword, int page, CancellationToken ct = default)
    {
        // 站点搜索为 /search?q=xxx，暂未暴露 page 参数；page>1 时尝试追加 &page=
        var path = "/search?q=" + Uri.EscapeDataString(keyword);
        if (page > 1) path += "&page=" + page;

        var html = await _client.GetHtmlAsync(path, ct);
        var result = BaozimhHtmlParser.ParseSearchResults(html, _client.BaseUrl);
        return new SearchResult
        {
            Items = result.Items,
            Total = result.Items.Count,
            TotalPages = result.TotalPages,
        };
    }

    public async Task<ComicDetail> GetComicAsync(string comicId, CancellationToken ct = default)
    {
        var html = await _client.GetHtmlAsync("/comic/" + Uri.EscapeDataString(comicId), ct);
        var detail = BaozimhHtmlParser.ParseComicDetail(html);
        var title = detail.Title.Length > 0 ? detail.Title : comicId;
        var chapters = new List<Chapter>();
        for (var i = 0; i < detail.Chapters.Count; i++)
        {
            var (href, t) = detail.Chapters[i];
            var chapterId = BaozimhHtmlParser.Abs(_client.BaseUrl, href);
            // href 已是 HtmlDecode 后的绝对或相对地址，Abs 保证为绝对 URL
            chapters.Add(new Chapter
            {
                Id = chapterId,
                Title = t,
                ComicId = comicId,
                ComicTitle = FilenameFilter.Filter(title),
                SourceId = "baozimh",
                OrderValue = i,
            });
        }

        return new ComicDetail
        {
            Id = comicId,
            Title = title,
            CoverUrl = detail.Cover,
            Description = detail.Intro,
            Authors = new List<string>(),
            Tags = new List<string>(),
            Chapters = chapters,
        };
    }

    public async Task<IReadOnlyList<ImagePage>> GetChapterImagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        // chapter.Id 即为详情页解析出的完整章节 URL（含 /user/page_direct 跳转地址）
        // BaozimhHttpClient.AllowAutoRedirect=true 会自动跟随 302 到真实章节页
        var html = await _client.GetHtmlAsync(chapter.Id, ct);
        var urls = BaozimhHtmlParser.ParseChapterImages(html, _client.BaseUrl);
        if (urls.Count == 0)
            throw new JmException("包子漫画: 章节无图片");

        var headers = new Dictionary<string, string>
        {
            ["Referer"] = BaozimhConstants.Referer,
            ["User-Agent"] = BaozimhConstants.UserAgent,
        };
        return urls.Select(u => new ImagePage { Url = u, Headers = headers }).ToList();
    }
}
