using ResourceGrab.Core.Sources.Novel;

namespace ResourceGrab.Core.Sources.Alicesw;

/// <summary>
/// 爱丽丝书屋站点常量。站点有多个入口域名，按顺序探测；发布页 www.asw5.cc 只用于换线提示。
/// </summary>
public static class AliceswConstants
{
    public const string SourceId = "alicesw";

    public const string DefaultDomain = "www.alicesw.com";

    /// <summary>候选域名（简体站 + 繁体镜像）。失效的域名由客户端在本次运行内跳过。</summary>
    public static readonly string[] Domains = ["www.alicesw.com", "alicesw.tw"];

    /// <summary>官方发布页，域名全挂时提示用户去这里找新地址。</summary>
    public const string PublishPage = "https://www.asw5.cc";

    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>站点搜索参数，对应 NovelSearchField。</summary>
    public static string FieldParam(NovelSearchField field) => field switch
    {
        NovelSearchField.Title => "title",
        NovelSearchField.Author => "author",
        NovelSearchField.Tag => "tag",
        _ => "_all",
    };

    public static string SearchPath(string keyword, int page, NovelSearchField field)
    {
        var q = Uri.EscapeDataString(keyword.Trim());
        var path = $"/search.html?q={q}&f={FieldParam(field)}";
        if (page > 1) path += $"&p={page}";
        return path;
    }

    public static string DetailPath(string novelId) => "/novel/" + Uri.EscapeDataString(novelId) + ".html";

    public static string ChaptersPath(string novelId) =>
        "/other/chapters/id/" + Uri.EscapeDataString(novelId) + ".html";

    public static string ChapterPath(string bookId, string key) =>
        $"/book/{Uri.EscapeDataString(bookId)}/{Uri.EscapeDataString(key)}.html";

    /// <summary>分类列表：默认按更新；按字数 / 人气走 /all/id/{id}/order/{field}+desc.html。</summary>
    public static string CategoryPath(string categoryId, int page, NovelListSort sort)
    {
        var path = sort switch
        {
            NovelListSort.WordCount => $"/all/id/{Uri.EscapeDataString(categoryId)}/order/word+desc.html",
            NovelListSort.Hits => $"/all/id/{Uri.EscapeDataString(categoryId)}/order/hits+desc.html",
            _ => "/lists/" + Uri.EscapeDataString(categoryId) + ".html",
        };
        return page > 1 ? path + "?page=" + page : path;
    }

    /// <summary>榜单：hits_day / hits_week / hits_month / hits。</summary>
    public static string RankPath(NovelRankPeriod period)
    {
        var tail = period switch
        {
            NovelRankPeriod.Day => "hits_day",
            NovelRankPeriod.Week => "hits_week",
            NovelRankPeriod.Month => "hits_month",
            _ => "hits",
        };
        return "/other/rank_hits/order/" + tail + ".html";
    }
}
