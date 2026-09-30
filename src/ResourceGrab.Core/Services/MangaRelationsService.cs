using System.Text.Json;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services;

/// <summary>服务器相关推荐条目（来自 album.json 的 related_list），Image 为在线封面 URL。</summary>
public sealed class RelatedEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Image { get; set; } = "";
}

/// <summary>单本漫画的关系数据：作品名 + 服务器相关推荐。</summary>
public sealed class ComicRelation
{
    /// <summary>构建时的元数据时间戳（MetadataStamp），用于增量判断是否需要重读 album.json。</summary>
    public DateTime? Stamp { get; set; }
    public List<string> Works { get; set; } = new();
    public List<RelatedEntry> Related { get; set; } = new();
}

/// <summary>本地漫画关系缓存：按漫画目录路径保存作品/相关推荐，避免每次进推荐页重复读 album.json。</summary>
public sealed class MangaRelationsCache
{
    public DateTime SavedAt { get; set; }
    public Dictionary<string, ComicRelation> Entries { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 本地漫画关系服务：从 album.json 抽取作品名（Works）与相关推荐（RelatedList），
/// 增量落盘到 manga-relations-cache.json，并顺带回填 LocalComic.Works（避免二次扫描）。
/// 只读本地元数据，不联网。
/// </summary>
public sealed class MangaRelationsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly LocalLibraryService _library;
    private readonly ILogger? _logger;

    public MangaRelationsService(LocalLibraryService library, ILogger? logger = null)
    {
        _library = library;
        _logger = logger;
    }

    /// <summary>读取关系缓存；不存在或损坏时返回空缓存。</summary>
    public MangaRelationsCache Load()
    {
        try
        {
            var path = AppPaths.MangaRelationsCachePath;
            if (!File.Exists(path)) return new MangaRelationsCache();
            var cache = JsonSerializer.Deserialize<MangaRelationsCache>(File.ReadAllText(path));
            if (cache is null) return new MangaRelationsCache();
            cache.Entries ??= new Dictionary<string, ComicRelation>(StringComparer.OrdinalIgnoreCase);
            foreach (var relation in cache.Entries.Values)
            {
                relation.Works ??= new List<string>();
                relation.Related ??= new List<RelatedEntry>();
            }
            return cache;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[MangaRelations] 读取关系缓存失败: {ex.Message}");
            return new MangaRelationsCache();
        }
    }

    /// <summary>缓存是否已覆盖当前库且时间戳一致（不必读 album.json 即可判断）。</summary>
    public static bool IsFresh(MangaRelationsCache cache, IReadOnlyList<LocalComic> comics)
    {
        foreach (var comic in comics)
        {
            if (!comic.HasMetadata) continue;
            if (!cache.Entries.TryGetValue(comic.Path, out var entry)) return false;
            if (entry.Stamp != comic.MetadataStamp) return false;
        }
        return true;
    }

    /// <summary>
    /// 增量构建：只对新增/变更（MetadataStamp 不一致）的漫画读 album.json，其余复用缓存；
    /// 同时把作品名回填进 LocalComic.Works。返回是否有漫画对象被补写 Works。
    /// </summary>
    public bool Build(MangaRelationsCache cache, IReadOnlyList<LocalComic> comics)
    {
        var libraryChanged = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var comic in comics)
        {
            if (string.IsNullOrEmpty(comic.Path) || !comic.HasMetadata) continue;
            seen.Add(comic.Path);

            cache.Entries.TryGetValue(comic.Path, out var existing);
            if (existing is not null && existing.Stamp == comic.MetadataStamp)
            {
                libraryChanged |= BackfillWorks(comic, existing.Works);
                continue;
            }

            AlbumMetadata? metadata;
            try
            {
                metadata = _library.ReadMetadata(comic.Path);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"[MangaRelations] 读取元数据失败 {comic.Path}: {ex.Message}");
                metadata = null;
            }

            var entry = new ComicRelation { Stamp = comic.MetadataStamp };
            if (metadata is not null)
            {
                entry.Works = (metadata.Works ?? new List<string>())
                    .Where(w => !string.IsNullOrWhiteSpace(w))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                entry.Related = (metadata.RelatedList ?? new List<RelatedListRespData>())
                    .Where(r => !string.IsNullOrWhiteSpace(r.Id))
                    .Select(r => new RelatedEntry
                    {
                        Id = r.Id,
                        Name = r.Name ?? "",
                        Author = r.Author ?? "",
                        Image = r.Image ?? "",
                    })
                    .ToList();
            }

            cache.Entries[comic.Path] = entry;
            libraryChanged |= BackfillWorks(comic, entry.Works);
        }

        foreach (var stale in cache.Entries.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            cache.Entries.Remove(stale);
        }

        cache.SavedAt = DateTime.Now;
        return libraryChanged;
    }

    /// <summary>把关系缓存里的作品名回填到漫画对象（仅当 LocalComic.Works 为空时）。</summary>
    private static bool BackfillWorks(LocalComic comic, List<string> works)
    {
        if (comic.Works is { Count: > 0 }) return false;
        if (works.Count == 0) return false;
        comic.Works = new List<string>(works);
        return true;
    }

    /// <summary>原子写入关系缓存。</summary>
    public void Save(MangaRelationsCache cache)
    {
        try
        {
            var path = AppPaths.MangaRelationsCachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache, JsonOptions));
            File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[MangaRelations] 保存关系缓存失败: {ex.Message}");
        }
    }
}
