using System.Text.Json;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using System.Threading;
using ResourceGrab.Core.Utils;

using ResourceGrab.Core.Services.VideoScrape;
namespace ResourceGrab.Core.Services;

/// <summary>视频库多维查询条件。</summary>
public sealed record VideoQueryOptions
{
    public string SearchText { get; init; } = "";
    public IReadOnlySet<string>? IncludedTags { get; init; }
    public IReadOnlySet<string>? ExcludedTags { get; init; }
    public IReadOnlySet<string>? ActorFilter { get; init; }
    public string? Series { get; init; }
    public string? Studio { get; init; }
    public CensorType? CensorType { get; init; }
    public string? Resolution { get; init; }
    public DurationRange? Duration { get; init; }
    public bool FavoritesOnly { get; init; }
    public bool WatchedOnly { get; init; }
    public ScrapeStatus? ScrapeStatus { get; init; }
    public VideoSortBy SortBy { get; init; } = VideoSortBy.AddedDesc;
}

public enum DurationRange
{
    Any = 0,
    Under30Minutes,
    From30To60Minutes,
    Over60Minutes,
}

public enum VideoSortBy
{
    AddedDesc = 0,
    AddedAsc,
    TitleAsc,
    TitleDesc,
    ReleaseDateDesc,
    ScoreDesc,
    UserRatingDesc,
        SizeDesc,
        LastWatchedDesc,
        WatchCountDesc,
        OpenCountDesc,
        LastOpenedDesc,
}

/// <summary>以单个视频文件为中心的本地媒体库。</summary>
public class VideoLibraryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static readonly string[] VideoExtensions =
    {
        ".mp4", ".mkv", ".avi", ".webm", ".wmv", ".mov", ".flv", ".m4v", ".ts", ".rmvb"
    };

    private readonly string _filePath;
    private readonly string _legacyFilePath;
    private List<VideoItem> _items;
    private readonly object _lock = new();
    private int _dataVersion;
    private bool _pendingSave;
    private DateTime _lastThrottledSaveAt = DateTime.MinValue;
    private readonly List<string> _rootFolders;
    private readonly ILogger? _logger;
    private readonly ScrapeReportService? _reportService;

    public VideoLibraryService(string filePath, string legacyFilePath, ILogger? logger = null)
        : this(filePath, legacyFilePath, logger, null)
    {
    }

    public VideoLibraryService(string filePath, string legacyFilePath, ILogger? logger, ScrapeReportService? reportService)
    {
        _filePath = filePath;
        _legacyFilePath = legacyFilePath;
        _logger = logger;
        _items = LoadWithMigration();
        _dataVersion = 1;
        _rootFolders = LoadRootFolders();
        _reportService = reportService ?? new ScrapeReportService(logger);
        if (_rootFolders.Count == 0 && File.Exists(_legacyFilePath))
            SeedRootsFromLegacy();
        _logger?.Info($"[VideoLibrary] 已加载 {_items.Count} 条记录 ({Path.GetFileName(_filePath)})");
    }

    public IReadOnlyList<VideoItem> Items => _items;
    public int DataVersion => _dataVersion;

    public IReadOnlyList<string> RootFolders => _rootFolders;

    public List<VideoItem> AddFolder(string folderPath)
    {
        var normalized = Path.GetFullPath(folderPath);
        if (!_rootFolders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            _rootFolders.Add(normalized);
            SaveRootFolders();
        }
        var existing = _items.Select(i => i.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = ScanVideoFiles(folderPath).Select(file =>
        {
            file.Id = VideoItem.CreateId(file.FilePath);
            var parsed = VideoNumberParser.Parse(file.FileName);
            file.Number = parsed.Number;
            file.Part = parsed.Part;
            return file;
        }).Where(file => existing.Add(file.FilePath)).ToList();

        if (added.Count > 0)
        {
            _items.AddRange(added);
            RestoreMetadataFromCache(added);
            Save();
        }
        _logger?.Info($"[VideoLibrary] 添加文件夹 {folderPath}: 新增 {added.Count} 条");
        return added;
    }

    public bool RemoveRootFolder(string folderPath)
    {
        var normalized = Path.GetFullPath(folderPath);
        var count = _rootFolders.RemoveAll(path =>
            string.Equals(Path.GetFullPath(path), normalized, StringComparison.OrdinalIgnoreCase));
        if (count > 0) SaveRootFolders();
        return count > 0;
    }

    /// <summary>重新扫描目录；按路径和大小合并旧记录，保留元数据与用户数据。</summary>
    public List<VideoItem> Rescan(string folderPath)
    {
        var fresh = ScanVideoFiles(folderPath);
        List<VideoItem> snapshot;
        lock (_lock) { snapshot = _items.ToList(); }
        var old = new Dictionary<string, VideoItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _items)
        {
            old.TryAdd(StateKey(item.FilePath, item.FileSizeBytes), item);
        }

        // 路径变了但「文件名 + 大小」没变 → 视为同一文件被移动，继承全部状态。
        // 排除路径仍匹配的旧记录（那些走正常合并），只把真正消失的记录当移动候选。
        var freshKeys = fresh.Select(i => StateKey(i.FilePath, i.FileSizeBytes)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var movedPool = new Dictionary<(string FileName, long FileSizeBytes), VideoItem>();
        foreach (var i in snapshot)
        {
            if (freshKeys.Contains(StateKey(i.FilePath, i.FileSizeBytes))) continue;
            var key = (i.FileName ?? "", i.FileSizeBytes);
            movedPool.TryAdd(key, i);
        }

        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacedOldPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in fresh)
        {
            var parsed = VideoNumberParser.Parse(item.FileName);
            if (old.TryGetValue(StateKey(item.FilePath, item.FileSizeBytes), out var previous))
            {
                CopyItemState(previous, item);
                if (item.Number.Length == 0 && parsed.Number.Length > 0)
                {
                    item.Number = parsed.Number;
                    item.Part = parsed.Part;
                }
            }
            else if ((item.FileName ?? "", item.FileSizeBytes) is var moveKey
                && movedPool.TryGetValue(moveKey, out var moved)
                && consumed.Add(moved.Id))
            {
                CopyItemState(moved, item);
                _logger?.Info($"[VideoLibrary] 识别到移动: {moved.FilePath} -> {item.FilePath}");
                replacedOldPaths.Add(moved.FilePath);
                if (item.Number.Length == 0 && parsed.Number.Length > 0)
                {
                    item.Number = parsed.Number;
                    item.Part = parsed.Part;
                }
            }
            else
            {
                item.Id = VideoItem.CreateId(item.FilePath);
                item.Number = parsed.Number;
                item.Part = parsed.Part;
            }
        }

        var freshPaths = fresh.Select(i => i.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var otherItems = _items.Where(i => !freshPaths.Contains(i.FilePath) && !replacedOldPaths.Contains(i.FilePath)).ToList();
        var removedCount = otherItems.Count;
        lock (_lock) {
            _items.Clear();
            _items.AddRange(fresh.Concat(otherItems));
        }
        Save();
        _logger?.Info($"[VideoLibrary] 重扫 {folderPath}: 扫到 {fresh.Count}, 库外保留 {removedCount}");
        return fresh.Where(i => i.ScrapeStatus == ScrapeStatus.Pending).ToList();
    }

    /// <summary>清空全部视频记录（不删除磁盘上的视频文件与封面缓存）。</summary>
    public void ClearAll()
    {
        var count = _items.Count;
        _items.Clear();
        Save();
        _logger?.Info($"[VideoLibrary] 清空视频库: 移除 {count} 条记录");
    }

    private static string StateKey(string path, long size) => $"{path}|{size}";

    internal static void CopyItemState(VideoItem source, VideoItem target)
    {
        target.Id = source.Id;
        target.DurationSeconds = source.DurationSeconds;
        target.Resolution = source.Resolution;
        target.ThumbnailPath = source.ThumbnailPath;
        target.AddedDate = source.AddedDate;
        target.Number = source.Number;
        target.Part = source.Part;
        target.Title = source.Title;
        target.OriginalTitle = source.OriginalTitle;
        target.Description = source.Description;
        target.Actors = source.Actors;
        target.Tags = source.Tags;
        target.Series = source.Series;
        target.Studio = source.Studio;
        target.Publisher = source.Publisher;
        target.Director = source.Director;
        target.ReleaseDate = source.ReleaseDate;
        target.Score = source.Score;
        target.CensorType = source.CensorType;
        target.CoverPath = source.CoverPath;
        target.PosterPath = source.PosterPath;
        target.PreviewImages = source.PreviewImages;
        target.IsFavorite = source.IsFavorite;
        target.UserRating = source.UserRating;
        target.WatchProgress = source.WatchProgress;
        target.WatchCount = source.WatchCount;
        target.LastWatchedAt = source.LastWatchedAt;
        target.OpenCount = source.OpenCount;
        target.LastOpenedAt = source.LastOpenedAt;
        target.UserTags = source.UserTags;
        target.ScrapeStatus = source.ScrapeStatus;
        target.ScrapeSource = source.ScrapeSource;
        target.ScrapedAt = source.ScrapedAt;
        target.Reviews = source.Reviews;
        target.RelatedNumbers = source.RelatedNumbers;
        target.ScoreVotes = source.ScoreVotes;
        target.HasMagnet = source.HasMagnet;
        target.HasChineseSubtitle = source.HasChineseSubtitle;
        target.PreviewSourceUrls = source.PreviewSourceUrls;
        target.PreviewTotalCount = source.PreviewTotalCount;
        target.SeriesNumbers = source.SeriesNumbers;
        target.SourceUrls = source.SourceUrls;
    }

    public void Update(VideoItem item) => Update(item, deferSave: false);

    /// <summary>deferSave=true 时延迟去抖保存，供刮削等批量写入方避免每条整文件重写；批末调用 Flush() 落盘。</summary>
    public void Update(VideoItem item, bool deferSave)
    {
        lock (_lock)
        {
            var idx = _items.FindIndex(i => (!string.IsNullOrEmpty(item.Id) && i.Id == item.Id)
                     || (!string.IsNullOrEmpty(item.FilePath) && string.Equals(i.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)));
            if (idx < 0) return;
            if (string.IsNullOrEmpty(item.Id)) item.Id = _items[idx].Id;
            _items[idx] = item;
            Interlocked.Increment(ref _dataVersion);
            if (deferSave) SaveThrottledLocked();
            else SaveCore(_items);
        }
    }

    /// <summary>批量写入方批末调用；若有未落盘的延迟保存则立即写盘。</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (!_pendingSave) return;
            _pendingSave = false;
            SaveCore(_items);
        }
    }

    /// <summary>仅在 _lock 内调用；批内最多约 1.5 秒写一次盘。</summary>
    private void SaveThrottledLocked()
    {
        _pendingSave = true;
        if (DateTime.UtcNow - _lastThrottledSaveAt < TimeSpan.FromMilliseconds(1500)) return;
        _lastThrottledSaveAt = DateTime.UtcNow;
        _pendingSave = false;
        SaveCore(_items);
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            var count = _items.RemoveAll(i => i.Id == id);
            if (count > 0) _logger?.Info($"[VideoLibrary] 移除记录 {id}");
            Interlocked.Increment(ref _dataVersion);
        }
        Save();
    }

    public int RemoveMany(IEnumerable<string> ids)
    {
        int count;
        lock (_lock)
        {
            var idSet = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (idSet.Count == 0) return 0;
            count = _items.RemoveAll(i => idSet.Contains(i.Id));
            Interlocked.Increment(ref _dataVersion);
        }
        Save();
        _logger?.Info($"[VideoLibrary] 批量移除记录 {count} 条");
        return count;
    }

    public VideoItem? GetById(string id)
    {
        lock (_lock) return _items.FirstOrDefault(i => i.Id == id);
    }

    public bool ToggleFavorite(string itemId)
    {
        var item = GetById(itemId);
        if (item is null) return false;
        item.IsFavorite = !item.IsFavorite;
        Save();
        return item.IsFavorite;
    }

    public int SetFavorites(IEnumerable<string> ids, bool isFavorite)
    {
        var changed = 0;
        var idSet = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _items.Where(i => idSet.Contains(i.Id) && i.IsFavorite != isFavorite))
        {
            item.IsFavorite = isFavorite;
            changed++;
        }
        if (changed == 0) return 0;
        Save();
        return changed;
    }

    /// <summary>记录一次外部播放器播放；进度至少标记为已开始。</summary>
    public bool RecordWatch(string itemId)
    {
        var item = GetById(itemId);
        if (item is null) return false;
        item.WatchCount++;
        item.WatchProgress = Math.Max(item.WatchProgress, 0.01);
        item.LastWatchedAt = DateTime.UtcNow;
        Save();
        return true;
    }

    /// <summary>记录一次成功打开所在目录。</summary>
    public bool RecordFolderOpen(string itemId)
    {
        var item = GetById(itemId);
        if (item is null) return false;
        item.OpenCount++;
        item.LastOpenedAt = DateTime.UtcNow;
        Save();
        return true;
    }

    public IReadOnlyList<VideoItem> Query(VideoQueryOptions options)
    {
        IEnumerable<VideoItem> result = _items;
        var text = options.SearchText.Trim();
        if (text.Length > 0) result = result.Where(i => MatchesSearchText(i, text));
        if (options.IncludedTags is { Count: > 0 } included)
            result = result.Where(i => included.All(tag => AllTags(i).Contains(tag, StringComparer.OrdinalIgnoreCase)));
        if (options.ExcludedTags is { Count: > 0 } excluded)
            result = result.Where(i => !AllTags(i).Any(tag => excluded.Contains(tag, StringComparer.OrdinalIgnoreCase)));
        if (options.ActorFilter is { Count: > 0 } actors)
            result = result.Where(i => i.Actors.Any(actor => actors.Contains(actor, StringComparer.OrdinalIgnoreCase)));
        if (!string.IsNullOrEmpty(options.Series))
            result = result.Where(i => i.Series.Equals(options.Series, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(options.Studio))
            result = result.Where(i => StudioName(i).Equals(options.Studio, StringComparison.OrdinalIgnoreCase));
        if (options.CensorType is { } censor) result = result.Where(i => i.CensorType == censor);
        if (!string.IsNullOrEmpty(options.Resolution))
            result = options.Resolution == "4K"
                ? result.Where(i => ParseWidth(i.Resolution) >= 3600)
                : result.Where(i => MatchesResolutionBucket(i.Resolution, options.Resolution));
        if (options.Duration is { } duration and not DurationRange.Any)
        {
            result = duration switch
            {
                DurationRange.Under30Minutes => result.Where(i => i.DurationSeconds > 0 && i.DurationSeconds < 1800),
                DurationRange.From30To60Minutes => result.Where(i => i.DurationSeconds >= 1800 && i.DurationSeconds <= 3600),
                DurationRange.Over60Minutes => result.Where(i => i.DurationSeconds > 3600),
                _ => result,
            };
        }
        if (options.FavoritesOnly) result = result.Where(i => i.IsFavorite);
        if (options.WatchedOnly) result = result.Where(i => i.WatchProgress > 0);
        if (options.ScrapeStatus is { } status) result = result.Where(i => i.ScrapeStatus == status);
        return Sort(result, options.SortBy).ToList();
    }

    internal static IEnumerable<VideoItem> Sort(IEnumerable<VideoItem> source, VideoSortBy sortBy) => sortBy switch
    {
        VideoSortBy.AddedAsc => source.OrderBy(i => i.AddedDate),
        VideoSortBy.TitleAsc => source.OrderBy(i => i.DisplayTitle, StringComparer.OrdinalIgnoreCase),
        VideoSortBy.TitleDesc => source.OrderByDescending(i => i.DisplayTitle, StringComparer.OrdinalIgnoreCase),
        VideoSortBy.ReleaseDateDesc => source.OrderByDescending(i => i.ReleaseDate ?? DateTime.MinValue),
        VideoSortBy.ScoreDesc => source.OrderByDescending(i => i.Score).ThenByDescending(i => i.UserRating),
        VideoSortBy.UserRatingDesc => source.OrderByDescending(i => i.UserRating).ThenByDescending(i => i.Score),
        VideoSortBy.SizeDesc => source.OrderByDescending(i => i.FileSizeBytes),
        VideoSortBy.LastWatchedDesc => source.OrderByDescending(i => i.LastWatchedAt ?? DateTime.MinValue),
        VideoSortBy.WatchCountDesc => source.OrderByDescending(i => i.WatchCount).ThenByDescending(i => i.LastWatchedAt ?? DateTime.MinValue),
        VideoSortBy.OpenCountDesc => source.OrderByDescending(i => i.OpenCount).ThenByDescending(i => i.LastOpenedAt ?? DateTime.MinValue),
        VideoSortBy.LastOpenedDesc => source.OrderByDescending(i => i.LastOpenedAt ?? DateTime.MinValue),
        _ => source.OrderByDescending(i => i.AddedDate),
    };

    private static bool MatchesSearchText(VideoItem item, string text) =>
        Contains(item.FileName, text) || Contains(item.Number, text) || Contains(item.Title, text)
        || Contains(item.OriginalTitle, text) || Contains(item.Description, text) || Contains(item.Series, text)
        || Contains(item.Studio, text) || Contains(item.Publisher, text) || Contains(item.Director, text)
        || AllTags(item).Any(tag => Contains(tag, text)) || item.Actors.Any(actor => Contains(actor, text));

    private static bool Contains(string? field, string text) => !string.IsNullOrEmpty(field) && field.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> AllTags(VideoItem item) => item.Tags.Concat(item.UserTags);

    private static string StudioName(VideoItem item) => string.IsNullOrWhiteSpace(item.Studio) ? item.Publisher : item.Studio;

    private static int ParseWidth(string resolution)
    {
        var left = resolution.Split('x', '×').FirstOrDefault();
        return int.TryParse(left, out var width) ? width : 0;
    }

    private static bool MatchesResolutionBucket(string resolution, string bucket)
    {
        var width = ParseWidth(resolution);
        return bucket switch
        {
            "1080p" => width >= 1920 && width < 2560,
            "720p" => width >= 1280 && width < 1920,
            "480p" => width > 0 && width < 1280,
            _ => false,
        };
    }

    public Dictionary<string, int> GetTagCounts() => CountValues(_items.ToList().SelectMany(AllTags));
    public Dictionary<string, int> GetActorCounts() => CountValues(_items.ToList().SelectMany(i => i.Actors));
    public List<string> GetAllActors() => GetActorCounts().Keys.ToList();
    public Dictionary<string, int> GetSeriesCounts() => CountValues(_items.ToList().Select(i => i.Series));
    public List<string> GetAllSeries() => GetSeriesCounts().Keys.ToList();
    public Dictionary<string, int> GetStudioCounts() => CountValues(_items.ToList().Select(StudioName));

    private static Dictionary<string, int> CountValues(IEnumerable<string?> values)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var key = value?.Trim() ?? "";
            if (key.Length == 0) continue;
            counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
        }
        return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    public static List<VideoItem> ScanVideoFiles(string dirPath)
    {
        var result = new List<VideoItem>();
        if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath)) return result;
        try
        {
            foreach (var path in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
            {
                // 跳过隐藏文件与系统目录（Thumbs.db、@eaDir 等）
                var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Any(s => s.StartsWith('.') || s.Equals("@eaDir", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("#recycle", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (!VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) continue;

                // 先跳过明显过小的文件（字幕轨道、空文件等），再做文件头魔数校验。
                var info = new FileInfo(path);
                if (info.Length < VideoFileValidator.MinFileSizeBytes) continue;
                if (!VideoFileValidator.IsValidVideoFile(path)) continue;

                // 文件名与父目录均无法识别出有效番号时，视为非正片（推广/试看/附属文件），跳过。
                var numberResult = VideoNumberParser.Parse(path);
                if (numberResult.Number.Length == 0 && numberResult.Confidence <= 0)
                    continue;

                result.Add(new VideoItem
                {
                    FileName = Path.GetFileNameWithoutExtension(path),
                    FilePath = path,
                    FileSizeBytes = info.Length,
                });
            }
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"[VideoLibrary] 扫描目录异常 {dirPath}: {ex.Message}");
        }
        return result.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<VideoItem> LoadWithMigration()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var items = (JsonSerializer.Deserialize<List<VideoItem?>>(File.ReadAllText(_filePath)) ?? [])
                    .Where(i => i is not null)
                    .Cast<VideoItem>()
                    .ToList();
                foreach (var item in items) NormalizeItem(item);
                return items;
            }
            if (File.Exists(_legacyFilePath))
            {
                var folders = JsonSerializer.Deserialize<List<VideoFolder>>(File.ReadAllText(_legacyFilePath)) ?? [];
                var items = folders.SelectMany(MigrateFolder).ToList();
                foreach (var item in items) NormalizeItem(item);
                SaveCore(items);
                try { File.Move(_legacyFilePath, _legacyFilePath + ".bak", true); } catch { }
                return items;
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("[VideoLibrary] 加载视频库失败", ex);
        }
        return [];
    }

    private List<string> LoadRootFolders()
    {
        try
        {
            var path = Path.ChangeExtension(_filePath, ".roots.json");
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[VideoLibrary] 加载视频根目录失败: {ex.Message}");
            return [];
        }
    }


    /// <summary>从路径缓存恢复刮削元数据（删除 video-library.json 后重新添加文件时调用）。</summary>
    private void RestoreMetadataFromCache(List<VideoItem> items)
    {
        if (_reportService is null) return;
        foreach (var item in items)
        {
            var cached = _reportService.GetMetadataByNumber(item.Number);
            if (cached is null) continue;
            var fs = cached.FieldSources;
            if (fs.TryGetValue("title", out var t) && !string.IsNullOrEmpty(t)) item.Title = t;
            if (fs.TryGetValue("originalTitle", out var ot) && !string.IsNullOrEmpty(ot)) item.OriginalTitle = ot;
            if (fs.TryGetValue("description", out var desc) && !string.IsNullOrEmpty(desc)) item.Description = desc;
            if (fs.TryGetValue("actors", out var actors) && !string.IsNullOrEmpty(actors)) item.Actors = actors.Split(',').Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (fs.TryGetValue("tags", out var tags) && !string.IsNullOrEmpty(tags)) item.Tags = tags.Split(',').Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (fs.TryGetValue("series", out var series) && !string.IsNullOrEmpty(series)) item.Series = series;
            if (fs.TryGetValue("studio", out var studio) && !string.IsNullOrEmpty(studio)) item.Studio = studio;
            if (fs.TryGetValue("score", out var scoreStr) && double.TryParse(scoreStr, out var score)) item.Score = score;
            if (fs.TryGetValue("releaseDate", out var rd) && !string.IsNullOrEmpty(rd) && DateTime.TryParse(rd, out var dt)) item.ReleaseDate = dt;
            if (fs.TryGetValue("number", out var num) && !string.IsNullOrEmpty(num)) item.Number = num;
            if (fs.TryGetValue("coverPath", out var cp) && !string.IsNullOrEmpty(cp)) item.CoverPath = cp;
            if (fs.TryGetValue("posterPath", out var pp) && !string.IsNullOrEmpty(pp)) item.PosterPath = pp;
            item.ScrapeStatus = ScrapeStatus.Success;
            _logger?.Info($"[VideoLibrary] 从缓存恢复元数据: {item.Number} ({item.FilePath})");
        }
    }

    private void SaveRootFolders()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var path = Path.ChangeExtension(_filePath, ".roots.json");
            File.WriteAllText(path, JsonSerializer.Serialize(_rootFolders, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger?.Error("[VideoLibrary] 保存视频根目录失败", ex);
        }
    }

    private void SeedRootsFromLegacy()
    {
        try
        {
            var folders = JsonSerializer.Deserialize<List<VideoFolder>>(File.ReadAllText(_legacyFilePath));
            var roots = folders?.Select(folder => folder.FolderPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
            if (roots.Count == 0) return;
            _rootFolders.AddRange(roots);
            SaveRootFolders();
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[VideoLibrary] 从旧版根目录初始化失败: {ex.Message}");
        }
    }

    /// <summary>JSON 显式 null 会绕过属性初始化器；加载后统一修复旧版/手改数据的空集合与空字符串。</summary>
    internal static void NormalizeItem(VideoItem item)
    {
        item.Id ??= "";
        item.FilePath ??= "";
        item.FileName ??= "";
        item.Resolution ??= "";
        item.Number ??= "";
        item.Part ??= "";
        item.Title ??= "";
        item.OriginalTitle ??= "";
        item.Description ??= "";
        item.Series ??= "";
        item.Studio ??= "";
        item.Publisher ??= "";
        item.Director ??= "";
        item.CoverPath ??= "";
        item.PosterPath ??= "";
        item.ScrapeSource ??= "";
        item.Actors ??= [];
        item.Tags ??= [];
        item.PreviewImages ??= [];
        item.UserTags ??= [];
    }

    internal static IEnumerable<VideoItem> MigrateFolder(VideoFolder folder)
    {
        foreach (var file in folder.Files)
        {
            yield return new VideoItem
            {
                Id = VideoItem.CreateId(file.FilePath),
                FilePath = file.FilePath,
                FileName = file.FileName,
                FileSizeBytes = file.FileSizeBytes,
                DurationSeconds = file.DurationSeconds,
                Resolution = file.Resolution,
                ThumbnailPath = file.ThumbnailPath,
                AddedDate = folder.AddedDate,
                Number = VideoNumberParser.Parse(file.FileName).Number,
                Series = folder.Series,
                Actors = [.. folder.Actors, .. folder.VoiceActors],
                Tags = [.. folder.Tags],
                Director = folder.Director,
                IsFavorite = folder.IsFavorite,
                UserRating = folder.Rating,
                WatchProgress = file.WatchProgress > 0 ? file.WatchProgress : folder.WatchProgress,
                WatchCount = folder.WatchCount,
                LastWatchedAt = DateTime.TryParse(file.LastWatchedAt, out var watched) ? watched : null,
            };
        }
    }

    public void Save() => SaveCore(_items);

    private void SaveCore(List<VideoItem> items)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var temp = _filePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(items, JsonOptions));
                File.Move(temp, _filePath, true);
            }
            catch (Exception ex)
            {
                _logger?.Error("[VideoLibrary] 保存视频库失败", ex);
            }
        }
    }

    // ===== 侧栏计数磁盘缓存 =====

    private sealed class SidebarCountsCache
    {
        public int DataVersion { get; set; }
        public Dictionary<string, int> TagCounts { get; set; } = new();
        public Dictionary<string, int> ActorCounts { get; set; } = new();
        public Dictionary<string, int> SeriesCounts { get; set; } = new();
        public Dictionary<string, int> StudioCounts { get; set; } = new();
    }

    public void SaveSidebarCounts(
        Dictionary<string, int> tags, Dictionary<string, int> actors,
        Dictionary<string, int> series, Dictionary<string, int> studios)
    {
        try
        {
            var cache = new SidebarCountsCache
            {
                DataVersion = _dataVersion,
                TagCounts = tags,
                ActorCounts = actors,
                SeriesCounts = series,
                StudioCounts = studios,
            };
            var json = System.Text.Json.JsonSerializer.Serialize(cache, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.VideoSidebarCountsPath)!);
            File.WriteAllText(AppPaths.VideoSidebarCountsPath, json);
        }
        catch { }
    }

    public (int version, Dictionary<string, int> tags, Dictionary<string, int> actors,
            Dictionary<string, int> series, Dictionary<string, int> studios)? LoadSidebarCounts()
    {
        try
        {
            if (!File.Exists(AppPaths.VideoSidebarCountsPath)) return null;
            var json = File.ReadAllText(AppPaths.VideoSidebarCountsPath);
            var cache = System.Text.Json.JsonSerializer.Deserialize<SidebarCountsCache>(json);
            if (cache is null || cache.DataVersion != _dataVersion) return null;
            return (cache.DataVersion, cache.TagCounts, cache.ActorCounts, cache.SeriesCounts, cache.StudioCounts);
        }
        catch { return null; }
    }}
