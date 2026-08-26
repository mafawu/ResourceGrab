using ResourceGrab.Core.Utils;

namespace ResourceGrab.Core.Sources.Hitomi;

/// <summary>
/// hitomi.la 内容源：免登录，单册（无章节），图片走二进制索引 + 动态图床子域。
/// 排行 = popular nozomi 列表；搜索 = 关键词 B-tree 索引（tagindex 协议）。
/// </summary>
public class HitomiSource : IComicSource, IRankSource
{
    private readonly HitomiGalleryClient _gallery;
    private readonly HitomiGgResolver _gg;

    public HitomiSource(HitomiGalleryClient gallery, HitomiGgResolver gg)
    {
        _gallery = gallery;
        _gg = gg;
    }

    public ComicSourceInfo Info { get; } = new()
    {
        Id = "hitomi",
        DisplayName = "hitomi",
        RequiresLogin = false,
        SupportsSearchSort = false,
        SupportsCategories = false,
        SupportsRank = true,
        SupportsWeekly = false,
        SupportsFavorites = false,
        CoverHeaders = new Dictionary<string, string>
        {
            ["Referer"] = HitomiConstants.Referer,
        },
        // 图片走多子域 CDN，限流可比 wnacg 略宽但仍保守
        MaxImageConcurrency = 8,
        MaxChapterConcurrency = 2,
        MaxUrlFetchConcurrency = 2,
    };

    // ====================== IComicSource ======================

    public async Task<SearchResult> SearchAsync(string keyword, int page, CancellationToken ct = default)
    {
        var ids = await DoSearchAsync(keyword, ct);
        return await GetPageAsync(ids, page, ct);
    }

    public async Task<ComicDetail> GetComicAsync(string comicId, CancellationToken ct = default)
    {
        var id = int.Parse(comicId, System.Globalization.CultureInfo.InvariantCulture);
        var info = await _gallery.GetGalleryInfoAsync(id, ct);
        var title = info.Title.Length > 0 ? info.Title : id.ToString();

        return new ComicDetail
        {
            Id = id.ToString(),
            Title = title,
            CoverUrl = await _gg.CoverUrlAsync(info, ct),
            Description = BuildDescription(info),
            Authors = (info.Artists ?? new List<HitomiArtist>())
                .Select(a => a.Artist).Where(a => a.Length > 0).ToList(),
            Tags = (info.Tags ?? new List<HitomiTag>())
                .Select(t => t.Tag).Where(t => t.Length > 0).ToList(),
            // hitomi 无"章节"概念：整册建模为单个章节
            Chapters = new List<Chapter>
            {
                new()
                {
                    Id = id.ToString(),
                    NumericId = id,
                    Title = "全一册",
                    ComicId = id.ToString(),
                    ComicTitle = FilenameFilter.Filter(title),
                    SourceId = "hitomi",
                },
            },
        };
    }

    public async Task<IReadOnlyList<ImagePage>> GetChapterImagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        var id = (int)(chapter.NumericId ?? int.Parse(chapter.Id, System.Globalization.CultureInfo.InvariantCulture));
        var info = await _gallery.GetGalleryInfoAsync(id, ct);

        var pages = new List<ImagePage>(info.Files.Count);
        foreach (var file in info.Files)
        {
            var url = await _gg.ImageUrlAsync(file, ct);
            if (url.Length == 0)
            {
                continue;
            }
            pages.Add(new ImagePage
            {
                Url = url,
                Headers = new Dictionary<string, string>
                {
                    ["Referer"] = HitomiConstants.Referer,
                    ["User-Agent"] = HitomiConstants.UserAgent,
                },
            });
        }
        return pages;
    }

    // ====================== IRankSource ======================

    private static readonly IReadOnlyList<RankPeriodInfo> RankPeriods = new List<RankPeriodInfo>
    {
        new() { Id = "popular", Name = "热门" },
    };

    public IReadOnlyList<RankPeriodInfo> GetRankPeriods() => RankPeriods;

    public async Task<SearchResult> GetRankAsync(string periodId, int page, CancellationToken ct = default)
    {
        var ids = await _gallery.GetNozomiIdsAsync(null, "popular", "all", ct);
        return await GetPageAsync(ids, page, ct);
    }

    // ====================== 内部 ======================

    /// <summary>do_search：分词（-前缀为排除词）、并发取每词 id 集合后求交/求差。</summary>
    private async Task<List<int>> DoSearchAsync(string query, CancellationToken ct)
    {
        var q = query.Trim();
        if (q.StartsWith('?'))
        {
            q = q[1..];
        }
        q = q.ToLowerInvariant();

        var positive = new List<string>();
        var negative = new List<string>();
        foreach (var rawTerm in q.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var term = rawTerm.Replace('_', ' ');
            if (term.StartsWith('-'))
            {
                negative.Add(term[1..]);
            }
            else if (term.Length > 0)
            {
                positive.Add(term);
            }
        }

        var results = new List<int>();
        if (positive.Count == 0)
        {
            // 空关键词 = 全站列表（index-all.nozomi），供"全部"浏览使用
            results = await _gallery.GetNozomiIdsAsync(null, "index", "all", ct);
        }

        foreach (var term in positive)
        {
            var ids = await _gallery.GetGalleryIdsForQueryAsync(term, ct);
            if (results.Count == 0)
            {
                results = ids;
            }
            else
            {
                results = results.Intersect(ids).ToList();
            }
        }

        foreach (var term in negative)
        {
            var ids = await _gallery.GetGalleryIdsForQueryAsync(term, ct);
            results = results.Except(ids).ToList();
        }

        return results;
    }

    /// <summary>按页取画廊信息并映射为通用摘要（单条失败跳过，不影响整页）。</summary>
    private async Task<SearchResult> GetPageAsync(List<int> ids, int page, CancellationToken ct)
    {
        var totalPages = ids.Count == 0 ? 1 : (ids.Count + HitomiConstants.PageSize - 1) / HitomiConstants.PageSize;
        var slice = ids
            .Skip((page - 1) * HitomiConstants.PageSize)
            .Take(HitomiConstants.PageSize)
            .ToList();

        var items = new List<ComicSummary>(slice.Count);
        using var gate = new SemaphoreSlim(8);
        var tasks = slice.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var info = await _gallery.GetGalleryInfoAsync(id, ct);
                return await BuildSummaryAsync(info, ct);
            }
            catch
            {
                return null;
            }
            finally
            {
                gate.Release();
            }
        });
        var results = await Task.WhenAll(tasks);
        items.AddRange(results.Where(s => s is not null)!);

        return new SearchResult
        {
            Items = items,
            Total = ids.Count,
            TotalPages = totalPages,
        };
    }

    private async Task<ComicSummary?> BuildSummaryAsync(GalleryInfo info, CancellationToken ct)
    {
        if (info.Files.Count == 0)
        {
            return null;
        }
        var artist = (info.Artists ?? new List<HitomiArtist>()).FirstOrDefault()?.Artist ?? "";
        // 封面缩略图构造失败时保留条目（卡片显示占位），不让整页结果被 gg.js 单点故障拖垮
        var cover = "";
        try
        {
            cover = await _gg.CoverUrlAsync(info, ct);
        }
        catch
        {
            // 忽略封面失败
        }
        return new ComicSummary
        {
            Id = info.Id.ToString(),
            Title = info.Title,
            Author = artist,
            CoverUrl = cover,
        };
    }

    private static string BuildDescription(GalleryInfo info)
    {
        var parts = new List<string>();
        if (info.LanguageLocalname is { Length: > 0 } lang)
        {
            parts.Add($"語言：{lang}");
        }
        if (info.TypeField.Length > 0)
        {
            parts.Add($"類型：{info.TypeField}");
        }
        if (info.Date.Length > 0)
        {
            parts.Add($"日期：{info.Date}");
        }
        if (info.Files.Count > 0)
        {
            parts.Add($"頁數：{info.Files.Count}");
        }
        return string.Join(" · ", parts);
    }
}


