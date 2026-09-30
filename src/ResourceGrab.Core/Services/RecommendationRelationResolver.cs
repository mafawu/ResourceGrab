using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services;

/// <summary>推荐分组（同作品 / 同作者）。</summary>
public sealed class ComicGroup
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public IReadOnlyList<LocalComic> Members { get; init; } = Array.Empty<LocalComic>();
}

/// <summary>相关推荐项：优先命中本地库（LocalMatch 非空），否则展示在线封面。</summary>
public sealed class RelatedRecommendation
{
    public RelatedEntry Entry { get; init; } = new();
    public LocalComic? LocalMatch { get; init; }
    public int Weight { get; init; }
}

/// <summary>
/// 本地漫画推荐关系解析器（纯逻辑、无 I/O、可单测）。
/// 只使用精确层：作品名（LocalComic.Works）→ 无则 Tags 里的「作品:」→ 作者；相关推荐来自关系缓存。
/// 不做标题模糊分组（避免误合）。
/// </summary>
public static class RecommendationRelationResolver
{
    private const string WorkTagPrefix = "作品:";

    /// <summary>同作品分组：多本共用同一作品名（或回退「作品:」标签）。成员数 &lt; 2 的组不输出。</summary>
    public static List<ComicGroup> BuildWorkGroups(
        IReadOnlyList<LocalComic> comics, int maxGroups = 5, int maxMembers = 30)
    {
        var groups = new Dictionary<string, List<LocalComic>>(StringComparer.OrdinalIgnoreCase);
        foreach (var comic in comics)
        {
            foreach (var work in EnumerateWorkKeys(comic))
            {
                if (!groups.TryGetValue(work, out var list))
                {
                    list = new List<LocalComic>();
                    groups[work] = list;
                }
                list.Add(comic);
            }
        }

        return groups
            .Where(kv => kv.Value.Count >= 2)
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(maxGroups)
            .Select(kv => new ComicGroup
            {
                Key = kv.Key,
                Title = kv.Key,
                Members = DistinctByPath(kv.Value)
                    .OrderByDescending(c => c.ModifiedAt)
                    .Take(maxMembers)
                    .ToList(),
            })
            .ToList();
    }

    /// <summary>同作者分组。成员数 &lt; 2 的组不输出。</summary>
    public static List<ComicGroup> BuildAuthorGroups(
        IReadOnlyList<LocalComic> comics, int maxGroups = 5, int maxMembers = 30)
    {
        var groups = new Dictionary<string, List<LocalComic>>(StringComparer.OrdinalIgnoreCase);
        foreach (var comic in comics)
        {
            foreach (var author in comic.Author)
            {
                if (string.IsNullOrWhiteSpace(author)) continue;
                var key = author.Trim();
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<LocalComic>();
                    groups[key] = list;
                }
                list.Add(comic);
            }
        }

        return groups
            .Where(kv => kv.Value.Count >= 2)
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(maxGroups)
            .Select(kv => new ComicGroup
            {
                Key = kv.Key,
                Title = kv.Key,
                Members = DistinctByPath(kv.Value)
                    .OrderByDescending(c => c.ModifiedAt)
                    .Take(maxMembers)
                    .ToList(),
            })
            .ToList();
    }

    /// <summary>
    /// 汇总全库的服务器相关推荐，按出现频次（越多本推荐越靠前）+ 来源漫画的新旧排序；
    /// 命中本地库的项带上 LocalMatch，未命中的用 Entry.Image 作在线封面。
    /// </summary>
    public static List<RelatedRecommendation> BuildRelated(
        IReadOnlyList<LocalComic> comics,
        MangaRelationsCache relations,
        int max = 20)
    {
        if (comics.Count == 0) return new List<RelatedRecommendation>();

        var localByAlbumId = BuildLocalAlbumMap(comics);
        var orderedComics = comics.OrderByDescending(c => c.ModifiedAt).ToList();

        var aggregated = new Dictionary<string, (RelatedEntry Entry, int Weight, DateTime Recency)>(StringComparer.OrdinalIgnoreCase);
        var seenSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var comic in orderedComics)
        {
            if (string.IsNullOrEmpty(comic.Path)) continue;
            if (!relations.Entries.TryGetValue(comic.Path, out var relation)) continue;
            if (relation.Related.Count == 0) continue;

            // 同一本只算一次；来源本身也是本地已有漫画时跳过（推荐库里已有的意义不大）
            if (!seenSource.Add(comic.Path)) continue;

            foreach (var related in relation.Related)
            {
                if (string.IsNullOrWhiteSpace(related.Id)) continue;
                if (aggregated.TryGetValue(related.Id, out var current))
                {
                    aggregated[related.Id] = (current.Entry, current.Weight + 1,
                        comic.ModifiedAt > current.Recency ? comic.ModifiedAt : current.Recency);
                }
                else
                {
                    aggregated[related.Id] = (related, 1, comic.ModifiedAt);
                }
            }
        }

        return aggregated.Values
            .OrderByDescending(x => x.Weight)
            .ThenByDescending(x => x.Recency)
            .Take(max)
            .Select(x => new RelatedRecommendation
            {
                Entry = x.Entry,
                Weight = x.Weight,
                LocalMatch = long.TryParse(x.Entry.Id, out var albumId)
                    && localByAlbumId.TryGetValue(albumId, out var match)
                        ? match
                        : null,
            })
            .ToList();
    }

    private static IEnumerable<string> EnumerateWorkKeys(LocalComic comic)
    {
        var any = false;
        foreach (var work in comic.Works)
        {
            if (string.IsNullOrWhiteSpace(work)) continue;
            any = true;
            yield return work.Trim();
        }
        if (any) yield break;

        foreach (var tag in comic.Tags)
        {
            if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith(WorkTagPrefix, StringComparison.Ordinal)) continue;
            var value = tag[WorkTagPrefix.Length..].Trim();
            if (value.Length > 0) yield return value;
        }
    }

    private static Dictionary<long, LocalComic> BuildLocalAlbumMap(IReadOnlyList<LocalComic> comics)
    {
        var map = new Dictionary<long, LocalComic>();
        foreach (var comic in comics)
        {
            if (comic.AlbumId is not { } albumId || albumId <= 0) continue;
            // 同号跨源：优先禁漫（SourceId 空视为 jm），其余源仅在无禁漫时占位
            if (!map.ContainsKey(albumId) || IsJm(comic)) map[albumId] = comic;
        }
        return map;
    }

    private static bool IsJm(LocalComic comic) =>
        string.IsNullOrEmpty(comic.SourceId) || comic.SourceId.Equals("jm", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<LocalComic> DistinctByPath(IEnumerable<LocalComic> comics)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var comic in comics)
        {
            if (seen.Add(comic.Path)) yield return comic;
        }
    }
}
