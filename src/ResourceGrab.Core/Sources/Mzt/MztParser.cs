using System.Text.Json;

namespace ResourceGrab.Core.Sources.Mzt;

/// <summary>
/// 妹子图 JSON 接口的响应解析。三类结构：
/// <list type="bullet">
/// <item>列表 / 搜索 / 随机：<c>{ items:[{id,title,urls[],created_at,updated_at}], page, pageSize, total }</c></item>
/// <item>详情：<c>{ item:{ ...与列表项同构... } }</c></item>
/// <item>标签：<c>{ items:[{id,tag_name,count}], total }</c></item>
/// </list>
/// 关键点：<c>urls</c> 就是**完整图片直链数组**，列表项即已给出整册图片，
/// 无需像 lovecutes 那样进详情页再逐页拼接。
/// </summary>
public static class MztParser
{
    /// <summary>图集条目：列表与详情共用同一形态。</summary>
    /// <param name="Id">站点侧 id。</param>
    /// <param name="Title">标题。</param>
    /// <param name="ImageUrls">完整图片直链，顺序即站点顺序（不可重排）。</param>
    public sealed record GalleryItem(string Id, string Title, IReadOnlyList<string> ImageUrls);

    /// <summary>列表解析结果：摘要条目 + 分页信息。</summary>
    public sealed record ListResult(IReadOnlyList<ComicSummary> Items, long Total, long TotalPages);

    /// <summary>解析列表 / 搜索 / 随机响应。封面取图片数组首图。</summary>
    public static ListResult ParseList(JsonDocument doc)
    {
        var root = doc.RootElement;
        var items = new List<ComicSummary>();

        foreach (var el in EnumerateArray(root, "items"))
        {
            var gallery = ToGallery(el);
            if (gallery.Id.Length == 0) continue;

            items.Add(new ComicSummary
            {
                Id = gallery.Id,
                Title = gallery.Title,
                CoverUrl = gallery.ImageUrls.Count > 0 ? gallery.ImageUrls[0] : "",
            });
        }

        var total = ReadLong(root, "total");
        var pageSize = (int)ReadLong(root, "pageSize", MztConstants.PageSize);
        if (pageSize <= 0) pageSize = MztConstants.PageSize;

        var totalPages = total > 0 ? (long)Math.Ceiling(total / (double)pageSize) : 1;
        return new ListResult(items, total, Math.Max(1, totalPages));
    }

    /// <summary>解析详情响应 <c>{item:{...}}</c>；缺少 item 或 item 无 id 时返回 null。</summary>
    public static GalleryItem? ParseDetail(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("item", out var el) || el.ValueKind != JsonValueKind.Object) return null;

        var item = ToGallery(el);
        return item.Id.Length == 0 ? null : item;
    }

    /// <summary>
    /// 解析标签全集。
    /// <para>
    /// 注意条目 Id 直接取**标签名**而非标签数字 id：站点没有独立的标签筛选端点，
    /// 前端点击标签也是跳 <c>/?q={标签名}</c>（见 dist 里 Tags 分片的 <c>`/?${n}`</c>），
    /// 因此浏览时把标签名当关键词用即可，避免再维护一份 id→name 映射。
    /// </para>
    /// </summary>
    public static IReadOnlyList<TaxonomyItem> ParseTags(JsonDocument doc)
    {
        var list = new List<TaxonomyItem>();
        foreach (var el in EnumerateArray(doc.RootElement, "items"))
        {
            var name = ReadString(el, "tag_name");
            if (name.Length == 0) continue;

            var count = int.TryParse(ReadString(el, "count"), out var c) ? c : (int?)null;
            list.Add(new TaxonomyItem(name, name, count));
        }
        return list;
    }

    // ====================== 内部工具 ======================

    private static GalleryItem ToGallery(JsonElement el)
    {
        var urls = new List<string>();
        foreach (var u in EnumerateArray(el, "urls"))
        {
            var s = u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
            if (s.Length > 0) urls.Add(s);
        }

        return new GalleryItem(ReadString(el, "id"), ReadString(el, "title"), urls);
    }

    private static IEnumerable<JsonElement> EnumerateArray(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) yield break;
        if (!parent.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
        foreach (var el in arr.EnumerateArray()) yield return el;
    }

    /// <summary>宽松读字符串：站点多在字段上时而给数字时而给字符串（id / count）。</summary>
    private static string ReadString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return "";
        if (!el.TryGetProperty(name, out var v)) return "";

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            _ => "",
        };
    }

    private static long ReadLong(JsonElement el, string name, long fallback = 0)
    {
        if (el.ValueKind != JsonValueKind.Object) return fallback;
        if (!el.TryGetProperty(name, out var v)) return fallback;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : fallback,
            JsonValueKind.String => long.TryParse(v.GetString(), out var n) ? n : fallback,
            _ => fallback,
        };
    }
}
