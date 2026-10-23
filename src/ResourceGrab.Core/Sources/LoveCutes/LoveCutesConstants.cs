namespace ResourceGrab.Core.Sources.LoveCutes;

/// <summary>
/// 爱妹子（lovecutes.com）站点常量与 URL 规则。
/// 站点要求**尾斜杠**：不带斜杠会 301（如 <c>/type/9</c> → <c>/type/9/</c>），
/// 因此下方所有路径一律以 <c>/</c> 结尾，避免多一次跳转。
/// </summary>
public static class LoveCutesConstants
{
    public const string Domain = "www.lovecutes.com";

    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

    public static string BaseUrl => $"https://{Domain}";

    /// <summary>列表 / 索引页每页条目数（实测统一为 16）。</summary>
    public const int PageSize = 16;

    /// <summary>搜索命中数上限（站点封顶 1000，报告值超过时按此截断）。</summary>
    public const long SearchResultCap = 1000;

    /// <summary>
    /// 维度索引翻页上限。
    /// <para>
    /// 站点规模很大：performer（出演）实测 2166 项，全量枚举需 2166/16 ≈ 136 次请求，
    /// 且站点会偶发断连需重试，串行拉完要一两分钟，对 UI 不可接受。
    /// 故一期截断到 40 页（约 640 项），先保证链路可用；
    /// 待左栏接入过滤/虚拟化后再放开（见 PhotobookBrowseView 的条目过滤）。
    /// </para>
    /// </summary>
    public const int MaxTaxonomyIndexPages = 40;

    /// <summary>图集详情翻页上限（同上；实测最大 12 页左右）。</summary>
    public const int MaxGalleryPages = 200;

    // ====================== 图集 ======================

    /// <summary>图集详情首页：/article/{id}/</summary>
    public static string Article(string id) => $"/article/{id}/";

    /// <summary>图集详情内页：/article/{id}/page/{n}/（n≤1 归一化为首页）</summary>
    public static string ArticlePage(string id, int page) =>
        page <= 1 ? Article(id) : $"/article/{id}/page/{page}/";

    // ====================== 搜索 ======================

    /// <summary>
    /// 搜索：/search/?s={kw}，翻页为 /search/page/{n}/?s={kw}。
    /// 注意参数名是 <c>s</c> 而非 <c>q</c> —— 用 q 会得到"找到 0 个结果"的空页。
    /// </summary>
    public static string Search(string keyword, int page)
    {
        var q = Uri.EscapeDataString(keyword);
        return page <= 1 ? $"/search/?s={q}" : $"/search/page/{page}/?s={q}";
    }

    /// <summary>
    /// 「最新」列表：<c>/sort/new/</c>，翻页 <c>/sort/new/page/{n}/</c>（实测每页 16 条）。
    /// <para>
    /// 写真首页（关键词为空）走这个端点：站点没有"全部图集"入口，
    /// 而 <c>/search/?s=</c> 在空关键词下会返回"找到 0 个结果"的空页。
    /// </para>
    /// </summary>
    public static string Latest(int page) =>
        page <= 1 ? "/sort/new/" : $"/sort/new/page/{page}/";

    // ====================== 维度索引 ======================

    /// <summary>分类导航附着在每个分类页上（站内无 /type/ 索引页），取任意分类页即可枚举全集。</summary>
    public const string CategoryNavPath = "/type/1/";

    /// <summary>标签索引：/tag/（单页列全 507 项，无分页）。</summary>
    public const string TagIndexPath = "/tag/";

    /// <summary>专题索引首页：/topics/</summary>
    public const string TopicsIndexPath = "/topics/";

    /// <summary>专题索引翻页：/topics/page/{n}/</summary>
    public static string TopicsIndexPage(int page) =>
        page <= 1 ? TopicsIndexPath : $"/topics/page/{page}/";

    /// <summary>
    /// 实体索引翻页：/entities/{plural}/page/{n}/。
    /// plural ∈ creators（创作者）/ performers（出演）/ characters（角色）/ franchises（系列）。
    /// </summary>
    public static string EntityIndex(string plural, int page) =>
        page <= 1 ? $"/entities/{plural}/" : $"/entities/{plural}/page/{page}/";

    // ====================== 维度浏览 ======================

    /// <summary>分类浏览：/type/{id}/</summary>
    public static string TypeBrowse(string id, int page) =>
        page <= 1 ? $"/type/{id}/" : $"/type/{id}/page/{page}/";

    /// <summary>标签浏览：/tag/{id}/</summary>
    public static string TagBrowse(string id, int page) =>
        page <= 1 ? $"/tag/{id}/" : $"/tag/{id}/page/{page}/";

    /// <summary>
    /// 实体浏览：/entity/{entityId}/。
    /// entityId 形如 <c>creator/djawa</c>、<c>performer/xxx</c>、<c>franchise/bi-lan-hang-xian</c>。
    /// </summary>
    public static string EntityBrowse(string entityId, int page)
    {
        var id = entityId.Trim('/');
        return page <= 1 ? $"/entity/{id}/" : $"/entity/{id}/page/{page}/";
    }

    /// <summary>专题浏览：/topic/{slug}/</summary>
    public static string TopicBrowse(string slug, int page) =>
        page <= 1 ? $"/topic/{slug}/" : $"/topic/{slug}/page/{page}/";
}
