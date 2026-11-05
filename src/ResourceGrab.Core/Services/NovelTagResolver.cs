using ResourceGrab.Core.Sources.Novel;

namespace ResourceGrab.Core.Services;

/// <summary>
/// 详情页标签 → 站点分类的匹配。
/// 站点分类名（玄幻 / 都市 / 武侠…）与书籍标签常常同名，但标签里也有"群像 / 江湖"这类
/// 纯自由标签——两者必须分开处理：能对上分类就走分类页，对不上才按标签搜。
/// </summary>
public static class NovelTagResolver
{
    public static NovelCategory? MatchCategory(string? tag, IReadOnlyList<NovelCategory>? categories)
    {
        if (string.IsNullOrWhiteSpace(tag) || categories is null || categories.Count == 0) return null;

        var target = Normalize(tag);
        if (target.Length == 0) return null;

        foreach (var category in categories)
        {
            if (Normalize(category.Name) == target) return category;
        }

        return null;
    }

    /// <summary>去掉「小说」后缀、空白与常见标点，便于和分类名比对。</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Trim().TrimStart('#').Trim();
        if (s.EndsWith("小说", StringComparison.Ordinal)) s = s[..^2];
        foreach (var ch in new[] { ' ', '\t', '　', '·', '-', '_', '（', '）', '(', ')' })
            s = s.Replace(ch.ToString(), "");
        return s.ToLowerInvariant();
    }
}
