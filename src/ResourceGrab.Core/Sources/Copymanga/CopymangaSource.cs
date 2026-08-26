using ResourceGrab.Core.Utils;

namespace ResourceGrab.Core.Sources.Copymanga;

/// <summary>
/// 拷贝漫画（copymanga / mangacopy）内容源：常规中文漫画（国漫/日漫汉化）。
/// 访问方式对齐 copymanga-downloader（lanyeeee）：
///  - 详情 /api/v3/comic2/，章节按分组分页拉取；
///  - 章节图片接口需登录 token；图片 URL 打乱，用 words[] 索引还原顺序。
/// </summary>
public class CopymangaSource : IComicSource
{
    private readonly CopymangaHttpClient _client;
    private string _comicTitleCache = "";

    public CopymangaSource(CopymangaHttpClient client)
    {
        _client = client;
    }

    public ComicSourceInfo Info { get; } = new()
    {
        Id = "copymanga",
        DisplayName = "拷贝漫画",
        RequiresLogin = false,
        SupportsSearchSort = false,
        SupportsCategories = false,
        SupportsRank = false,
        SupportsWeekly = false,
        SupportsFavorites = false,
        CoverHeaders = new Dictionary<string, string>(),
        MaxImageConcurrency = 16,
        MaxChapterConcurrency = 3,
        MaxUrlFetchConcurrency = 4,
    };

    /// <summary>是否已登录（章节图片接口需要 token）。</summary>
    public bool IsLoggedIn => _client.Token.Length > 0;

    // ====================== 登录 ======================

    /// <summary>
    /// 登录并保存 token。密码按参考项目规则编码：base64("{password}-1729")。
    /// </summary>
    public async Task LoginAsync(string username, string password, CancellationToken ct = default)
    {
        const int salt = 1729;
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{password}-{salt}"));
        var result = await _client.LoginAsync(username, encoded, salt, ct);
        _client.SetToken(result.Token);
    }

    // ====================== IComicSource ======================

    public async Task<SearchResult> SearchAsync(string keyword, int page, CancellationToken ct = default)
    {
        var offset = (page - 1) * CopymangaConstants.SearchPageSize;
        var path = $"/api/v3/search/comic?limit={CopymangaConstants.SearchPageSize}&offset={offset}" +
                   $"&q={Uri.EscapeDataString(keyword)}&q_type=&platform=1";
        var data = await _client.GetAsync<CopySearchResults>(path, ct: ct);

        var items = data.List
            .Where(r => !string.IsNullOrEmpty(r.PathWord))
            .Select(ToSummary)
            .ToList();

        var totalPages = data.Total <= 0 ? 1 : (data.Total + CopymangaConstants.SearchPageSize - 1) / CopymangaConstants.SearchPageSize;
        return new SearchResult
        {
            Items = items,
            Total = data.Total,
            TotalPages = totalPages,
        };
    }

    public async Task<ComicDetail> GetComicAsync(string comicId, CancellationToken ct = default)
    {
        // 1) 详情（含 groups 字典）
        var detail = await _client.GetAsync<CopyComicDetail>($"/api/v3/comic2/{Uri.EscapeDataString(comicId)}?platform=1", ct: ct);
        var meta = detail.Comic;
        var title = string.IsNullOrEmpty(meta.Name) ? comicId : meta.Name;
        _comicTitleCache = FilenameFilter.Filter(title);

        // 2) 按分组并发拉取全部章节
        var chapters = new List<Chapter>();
        foreach (var (groupPathWord, group) in detail.Groups)
        {
            var groupChapters = await FetchGroupChaptersAsync(comicId, groupPathWord, group.Name, ct);
            chapters.AddRange(groupChapters);
        }

        // 3) 按 order（ordered/10）排序：正序在前，番外等在后
        chapters.Sort((a, b) => a.OrderValue.CompareTo(b.OrderValue));

        return new ComicDetail
        {
            Id = comicId,
            Title = title,
            CoverUrl = meta.Cover,
            Description = meta.Brief,
            Authors = meta.Author.Select(a => a.Name).Where(n => n.Length > 0).ToList(),
            Tags = meta.Theme.Select(t => t.Name).Where(n => n.Length > 0).ToList(),
            Chapters = chapters,
        };
    }

    public async Task<IReadOnlyList<ImagePage>> GetChapterImagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        var path = $"/api/v3/comic/{Uri.EscapeDataString(chapter.ComicId)}/chapter2/{Uri.EscapeDataString(chapter.Id)}?platform=1";
        var data = await _client.GetAsync<CopyChapterDetail>(path, requireAuth: true, ct: ct);

        var urls = data.Chapter.Contents
            .Select(c => c.Url)
            .Where(u => u.Length > 0)
            .ToList();

        // words[] 是真实顺序索引：contents[i] 应放在 words[i] 位置；
        // 参考项目把 contents 按 words 索引重排后下载，这里直接在内存重排。
        // words 长度不匹配时（容错）保持原序。
        var ordered = new string[urls.Count];
        var hasValidWords = data.Chapter.Words.Count == urls.Count;
        for (var i = 0; i < urls.Count; i++)
        {
            var position = hasValidWords ? (int)data.Chapter.Words[i] : i;
            if (position >= 0 && position < ordered.Length)
            {
                ordered[position] = urls[i];
            }
            else
            {
                ordered[i] = urls[i];
            }
        }

        var headers = new Dictionary<string, string>
        {
            ["Referer"] = "https://www.mangacopy.com/",
            ["User-Agent"] = CopymangaConstants.UserAgent,
        };
        return ordered
            .Where(u => u is { Length: > 0 })
            .Select(u => new ImagePage
            {
                // 图片 URL 用更高分辨率版本（参考项目：.c800x. → .c1500x.）
                Url = u.Replace(".c800x.", ".c1500x."),
                Headers = headers,
            })
            .ToList();
    }

    // ====================== 工具 ======================

    private async Task<List<Chapter>> FetchGroupChaptersAsync(
        string comicId, string groupPathWord, string groupName, CancellationToken ct)
    {
        var result = new List<Chapter>();
        var offset = 0;
        while (true)
        {
            var path = $"/api/v3/comic/{Uri.EscapeDataString(comicId)}/group/{Uri.EscapeDataString(groupPathWord)}/chapters" +
                       $"?limit={CopymangaConstants.ChapterPageSize}&offset={offset}";
            var page = await _client.GetAsync<CopyChaptersResult>(path, ct: ct);

            foreach (var chapter in page.List)
            {
                if (string.IsNullOrEmpty(chapter.Uuid))
                {
                    continue;
                }
                var chapterTitle = string.IsNullOrEmpty(groupName) ? chapter.Name : $"{groupName}·{chapter.Name}";
                if (string.IsNullOrEmpty(chapterTitle))
                {
                    chapterTitle = chapter.Index.ToString();
                }
                result.Add(new Chapter
                {
                    Id = chapter.Uuid,
                    Title = chapterTitle,
                    ComicId = comicId,
                    ComicTitle = _comicTitleCache.Length > 0 ? _comicTitleCache : FilenameFilter.Filter(comicId),
                    SourceId = "copymanga",
                    OrderValue = chapter.Ordered / 10.0,
                });
            }

            if (offset + page.List.Count >= page.Total || page.List.Count == 0)
            {
                break;
            }
            offset += page.List.Count;
        }
        return result;
    }

    private static ComicSummary ToSummary(CopyComicInSearch item) => new()
    {
        Id = item.PathWord,
        Title = item.Name,
        Author = item.Author.Count > 0 ? item.Author[0].Name : "",
        Category = "",
        CoverUrl = item.Cover,
    };
}
