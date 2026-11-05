using System.Text.RegularExpressions;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services;

public enum LocalMatchKind
{
    None = 0,

    /// <summary>书名为其子串，可能不是同一本：只提示，不自动接管。</summary>
    Possible = 1,

    /// <summary>规范化书名完全相等：认定同一本，在线页改用本地数据。</summary>
    Exact = 2,
}

public sealed record NovelLocalMatch(NovelResource Resource, LocalMatchKind Kind)
{
    public string Path => Resource.FullPath;
    public string Title => Resource.DisplayName;
}

/// <summary>
/// 在线书籍与本地索引的匹配。规则先严后宽：
/// 规范化后书名全等 → Exact；本地名包含书名（≥4 字）→ Possible。
/// 本地索引没有作者字段，所以不做作者校验，宁可停在 Possible 也不错误接管。
/// </summary>
public sealed class NovelLocalMatcher(NovelIndexService index)
{
    private static readonly Regex BracketRegex = new(@"[《》〈〉\[\]【】()（）{}<>·・:：,，.。!！?？\s\-—_/\\|]+", RegexOptions.Compiled);

    public NovelIndexService Index => index;

    public NovelLocalMatch? Find(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        if (index.Resources.Count == 0) return null;

        var target = Normalize(title);
        if (target.Length < 2) return null;

        NovelResource? possible = null;

        foreach (var resource in index.Resources)
        {
            var name = Normalize(resource.DisplayName);
            if (name.Length == 0) continue;

            if (name == target)
                return new NovelLocalMatch(resource, LocalMatchKind.Exact);

            // 双向包含：本地名可能带「校对版 / 全本」后缀，在线书名可能带「外传 / 续」后缀，
            // 任一方是另一方的子串都只算"可能是这本"，不自动接管。
            if (possible is null)
            {
                var forward = target.Length >= 4 && name.Contains(target, StringComparison.Ordinal);
                var backward = name.Length >= 4 && target.Contains(name, StringComparison.Ordinal);
                if (forward || backward) possible = resource;
            }
        }

        return possible is null ? null : new NovelLocalMatch(possible, LocalMatchKind.Possible);
    }

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = BracketRegex.Replace(text, "");
        s = s.Replace("全本", "").Replace("完结", "").Replace("校对版", "").Replace("未删节", "");
        return s.ToLowerInvariant();
    }
}
