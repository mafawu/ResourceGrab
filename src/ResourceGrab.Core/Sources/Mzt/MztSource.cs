using ResourceGrab.Core.Utils;

namespace ResourceGrab.Core.Sources.Mzt;

/// <summary>
/// 妹子图（mzt.111404.xyz）写真图集源。
/// <para>
/// 内容模型是**图集 → 图片序列**，无章节层级：整册建模为单个 <see cref="Chapter"/>。
/// 与 lovecutes 的差别在于数据通道：这里是同域 **JSON 接口**而非 HTML，
/// 且列表项自带完整的 <c>urls</c> 图片数组，整册图片一次拿全，无需分页拼接。
/// </para>
/// <para>
/// 能力：列表（也是「写真」首页）、搜索、详情、随机（<see cref="IRandomPhotobookSource"/>）、
/// 标签维度（<see cref="ITaxonomySource"/>，站点仅此一个维度）。
/// </para>
/// </summary>
public class MztSource : IComicSource, ITaxonomySource, IRandomPhotobookSource
{
    private readonly MztHttpClient _client;

    public MztSource(MztHttpClient client) => _client = client;

    public ComicSourceInfo Info { get; } = new()
    {
        Kind = ResourceKind.Photobook,
        Id = "mzt",
        DisplayName = "妹子图",
        RequiresLogin = false,
        SupportsSearchSort = false,
        // 站点只有标签一个维度（无分类 / Coser / 系列 / 角色 / 专题）
        Taxonomies = [TaxonomyKind.Tag],
        // 图片托管在 i.postimg.cc，实测裸链即可下载（无防盗链、不校验 UA）。
        CoverHeaders = new Dictionary<string, string>(),
        MaxImageConcurrency = 24,
        MaxChapterConcurrency = 2,
        MaxUrlFetchConcurrency = 4,
    };

    // ====================== IComicSource ======================

    /// <summary>
    /// 关键词搜索；<paramref name="keyword"/> 为空时即「写真」首页的全站列表
    /// —— 站点把两者放在同一个端点（<c>/urls?q=</c>）。
    /// </summary>
    public async Task<SearchResult> SearchAsync(string keyword, int page, CancellationToken ct = default)
    {
        using var doc = await _client.GetJsonAsync(
            MztConstants.List(page, MztConstants.PageSize, keyword), ct);
        return ToSearchResult(MztParser.ParseList(doc));
    }

    public async Task<ComicDetail> GetComicAsync(string comicId, CancellationToken ct = default)
    {
        using var doc = await _client.GetJsonAsync(MztConstants.Detail(comicId), ct);
        var item = MztParser.ParseDetail(doc) ?? throw new JmException($"图集 {comicId} 不存在");
        var title = item.Title.Length > 0 ? item.Title : comicId;

        return new ComicDetail
        {
            Id = comicId,
            Title = title,
            CoverUrl = item.ImageUrls.Count > 0 ? item.ImageUrls[0] : "",
            // 图集无章节层级：整册即一个章节。Title 是下载目录末级名，
            // 沿用既有源对单册内容的约定字面量「全一册」，否则会出现
            // {图集名}/{图集名}/ 的双层同名目录（见 DownloadManager 落盘规则）。
            Chapters =
            [
                new Chapter
                {
                    Id = comicId,
                    NumericId = long.TryParse(comicId, out var numeric) ? numeric : null,
                    Title = "全一册",
                    ComicId = comicId,
                    ComicTitle = FilenameFilter.Filter(title),
                    SourceId = "mzt",
                },
            ],
        };
    }

    /// <summary>
    /// 取整册图片。<see cref="Chapter"/> 不携带图片列表，故按需再请求一次详情
    /// （一次请求即返回全部图片，代价可接受）。
    /// </summary>
    public async Task<IReadOnlyList<ImagePage>> GetChapterImagesAsync(Chapter chapter, CancellationToken ct = default)
    {
        var id = chapter.NumericId?.ToString() ?? chapter.Id;
        using var doc = await _client.GetJsonAsync(MztConstants.Detail(id), ct);
        var item = MztParser.ParseDetail(doc);
        if (item is null) return [];

        // 图床不校验 Referer，补 UA 只为与站点侧行为一致
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = MztConstants.UserAgent,
        };

        return item.ImageUrls
            .Where(u => u.Length > 0)
            .Select(u => new ImagePage { Url = u, Headers = headers })
            .ToList();
    }

    // ====================== IRandomPhotobookSource ======================

    public async Task<SearchResult> GetRandomGalleriesAsync(int count, CancellationToken ct = default)
    {
        using var doc = await _client.GetJsonAsync(MztConstants.Random(count), ct);
        return ToSearchResult(MztParser.ParseList(doc));
    }

    // ====================== ITaxonomySource ======================

    public async Task<IReadOnlyList<TaxonomyItem>> GetTaxonomyItemsAsync(TaxonomyKind kind, CancellationToken ct = default)
    {
        // 站点只有标签维度，其余维度不声明也不实现
        if (kind != TaxonomyKind.Tag) return [];

        using var doc = await _client.GetJsonAsync(MztConstants.TagIndex, ct);
        return MztParser.ParseTags(doc);
    }

    public async Task<SearchResult> BrowseByTaxonomyAsync(
        TaxonomyKind kind, string itemId, int page, CancellationToken ct = default)
    {
        if (kind != TaxonomyKind.Tag) return new SearchResult();

        // 站点没有独立的标签筛选端点：前端点击标签即跳 /?q={标签名}。
        // 因此这里复用关键词搜索（TaxonomyItem.Id 存的就是标签名，见 MztParser.ParseTags）。
        using var doc = await _client.GetJsonAsync(
            MztConstants.List(page, MztConstants.PageSize, itemId), ct);
        return ToSearchResult(MztParser.ParseList(doc));
    }

    // ====================== 内部工具 ======================

    private static SearchResult ToSearchResult(MztParser.ListResult result) => new()
    {
        Items = result.Items,
        Total = result.Total,
        TotalPages = result.TotalPages,
    };
}
