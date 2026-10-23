namespace ResourceGrab.Core.Sources.Mzt;

/// <summary>
/// 妹子图（mzt.111404.xyz）站点常量与 API 规则。
/// <para>
/// 站点是 Vite + React 的 SPA，页面 HTML 只是个空壳，数据全部走**同域 JSON 接口**
/// （接口定义反推自 <c>/assets/index-*.js</c>）：
/// </para>
/// <list type="bullet">
/// <item><c>GET /urls?page&amp;pageSize&amp;q</c> —— 列表与搜索是同一个端点，q 为空即全站列表</item>
/// <item><c>GET /urls/{id}</c> —— 详情（与列表项同构，同样带完整图片数组）</item>
/// <item><c>GET /urls/random?count</c> —— 随机图集</item>
/// <item><c>GET /tags/all</c> —— 标签全集（单次返回，无需翻页）</item>
/// </list>
/// <para>
/// 前置 Cloudflare 只做 CDN（无 <c>Cf-Mitigated: challenge</c>），无鉴权、无指纹校验、
/// 无防盗链，普通 GET 即可；站点侧只有「响应偶发变慢/断连」这一个已知问题，故客户端带重试。
/// </para>
/// </summary>
public static class MztConstants
{
    public const string Domain = "mzt.111404.xyz";

    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public static string BaseUrl => $"https://{Domain}";

    /// <summary>列表每页条目数（站点默认即为 20）。</summary>
    public const int PageSize = 20;

    /// <summary>「随机」一次请求的图集数。</summary>
    public const int RandomCount = 24;

    /// <summary>
    /// 列表 / 搜索端点：<c>/urls?page={n}&amp;pageSize={m}[&amp;q={kw}]</c>。
    /// keyword 为空或不传时返回全站列表（写真首页即用此形态）。
    /// </summary>
    public static string List(int page, int pageSize = PageSize, string? keyword = null)
    {
        var url = $"{BaseUrl}/urls?page={Math.Max(page, 1)}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(keyword))
            url += $"&q={Uri.EscapeDataString(keyword.Trim())}";
        return url;
    }

    /// <summary>详情端点：<c>/urls/{id}</c>，响应与列表项同构。</summary>
    public static string Detail(string id) => $"{BaseUrl}/urls/{Uri.EscapeDataString(id)}";

    /// <summary>随机端点：<c>/urls/random?count={n}</c>。</summary>
    public static string Random(int count = RandomCount) => $"{BaseUrl}/urls/random?count={count}";

    /// <summary>标签全集：<c>/tags/all</c>（实测 201 项一次给全）。</summary>
    public static string TagIndex => $"{BaseUrl}/tags/all";
}
