using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Sources.ShortDrama;

namespace ResourceGrab.Core.Services.ShortDrama;

/// <summary>
/// 本地短剧库：读写每个剧文件夹下的 <c>drama.json</c>。
/// <para>
/// 刻意不做"全局索引文件"：一个文件夹 = 一部剧，用户可以整个拷走、直接删掉，
/// 库列表每次扫描目录重建（几十部剧的 JSON 读取耗时可忽略）。
/// </para>
/// </summary>
public sealed class ShortDramaLibraryService
{
    /// <summary>元数据文件名。</summary>
    public const string MetadataFileName = "drama.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Func<ShortDramaSettings> _settingsProvider;
    private readonly ILogger? _logger;

    public ShortDramaLibraryService(Func<ShortDramaSettings> settingsProvider, ILogger? logger = null)
    {
        _settingsProvider = settingsProvider;
        _logger = logger;
    }

    /// <summary>当前生效的下载根目录（改设置即时生效）。</summary>
    public string RootDir => ShortDramaSettings.ResolveDownloadDir(_settingsProvider().DownloadDir);

    /// <summary>某部剧的落盘目录（不创建）。</summary>
    public string FolderFor(string title)
        => Path.Combine(RootDir, SanitizeFolderName(title));

    /// <summary>由详情算出落盘目录（并按需创建）。</summary>
    public string EnsureFolder(ShortDramaDetail detail)
    {
        var folder = FolderFor(detail.Title.Length > 0 ? detail.Title : detail.Id);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>扫描根目录 + 已登记的外部目录，返回全部本地剧集（按最近下载时间倒序）。</summary>
    public IReadOnlyList<LocalDrama> Load()
    {
        var list = new List<LocalDrama>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in EnumerateAllFolders())
        {
            if (!seen.Add(Path.GetFullPath(dir))) continue;
            var local = ReadFolder(dir);
            if (local is not null) list.Add(local);
        }

        return list.OrderByDescending(d => d.LastDownloadedAt).ToList();
    }

    /// <summary>根目录下的子目录 + 外部登记目录（去重由调用方按全路径处理）。</summary>
    private IEnumerable<string> EnumerateAllFolders()
    {
        var root = RootDir;
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.EnumerateDirectories(root)) yield return dir;
        }
        foreach (var dir in ExternalFolders)
        {
            if (Directory.Exists(dir)) yield return dir;
        }
    }

    /// <summary>读一个目录下的 drama.json（元数据与磁盘不符时以磁盘为准）。</summary>
    private LocalDrama? ReadFolder(string dir)
    {
        var file = Path.Combine(dir, MetadataFileName);
        if (!File.Exists(file)) return null;
        try
        {
            var json = File.ReadAllText(file);
            var meta = JsonSerializer.Deserialize<DramaMetadata>(json, JsonOptions);
            if (meta is null || string.IsNullOrWhiteSpace(meta.Title)) return null;
            // 用户可能手动删了 mp4，以磁盘为准
            foreach (var ep in meta.Episodes)
            {
                if (ep.File is not { Length: > 0 } name) continue;
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) ep.File = null;
            }
            return new LocalDrama { FolderPath = dir, Metadata = meta };
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDramaLibrary] 读取失败 {file}: {ex.Message}");
            return null;
        }
    }

    // ── 外部目录登记（导入功能用：视频留在用户原来的位置） ───────────────

    private static readonly object ImportsLock = new();
    private List<string>? _external;

    private string ImportsPath => Path.Combine(AppPaths.AppDataDir, "drama-imports.json");

    /// <summary>已登记的外部目录（持久化在 config\drama-imports.json）。</summary>
    public IReadOnlyList<string> ExternalFolders
    {
        get
        {
            lock (ImportsLock)
            {
                if (_external is not null) return _external;
                try
                {
                    if (File.Exists(ImportsPath))
                    {
                        var json = File.ReadAllText(ImportsPath);
                        _external = JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
                    }
                    else
                    {
                        _external = [];
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"[ShortDramaLibrary] 读取导入登记失败：{ex.Message}");
                    _external = [];
                }
                return _external;
            }
        }
    }

    /// <summary>登记一个外部剧集目录（已有则忽略）。</summary>
    public void RegisterExternal(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        var full = Path.GetFullPath(folder);
        lock (ImportsLock)
        {
            var list = ExternalFolders.ToList();
            if (list.Any(p => string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(full);
            _external = list;
            SaveImports(list);
        }
    }

    /// <summary>取消登记（不删文件）。</summary>
    public bool UnregisterExternal(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        var full = Path.GetFullPath(folder);
        lock (ImportsLock)
        {
            var list = ExternalFolders.ToList();
            var removed = list.RemoveAll(p =>
                string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            _external = list;
            SaveImports(list);
            return true;
        }
    }

    private void SaveImports(List<string> list)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataDir);
            File.WriteAllText(ImportsPath, JsonSerializer.Serialize(list, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDramaLibrary] 保存导入登记失败：{ex.Message}");
        }
    }

    public LocalDrama? TryLoad(string folderPath)
    {
        var file = Path.Combine(folderPath, MetadataFileName);
        if (!File.Exists(file)) return null;
        try
        {
            var meta = JsonSerializer.Deserialize<DramaMetadata>(File.ReadAllText(file), JsonOptions);
            return meta is null ? null : new LocalDrama { FolderPath = folderPath, Metadata = meta };
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDramaLibrary] 读取失败 {file}: {ex.Message}");
            return null;
        }
    }

    /// <summary>把详情合并进 drama.json（保留已下载集的记录，只刷新剧级字段与集清单）。</summary>
    public DramaMetadata MergeDetail(ShortDramaDetail detail, string folder)
    {
        var existing = TryLoad(folder)?.Metadata;
        var meta = existing ?? new DramaMetadata { Id = detail.Id };
        meta.Schema = 1;
        meta.Id = detail.Id;
        meta.Title = detail.Title;
        meta.Description = detail.Description;
        meta.ScoreText = detail.ScoreText;
        meta.Tags = detail.Tags.ToList();
        meta.ChannelName = detail.ChannelName;
        meta.TotalEpisodes = detail.TotalEpisodes;
        meta.EpisodeText = detail.EpisodeText;
        meta.ReleaseText = detail.ReleaseText;
        meta.DetailUrl = detail.DetailUrl;
        meta.FetchedAt = DateTimeOffset.Now;

        // 集清单以站点为准，但已下载记录必须沿用（否则每次打开详情就把"已下载"擦掉）
        var old = meta.Episodes.ToDictionary(e => e.Number);
        var episodes = new List<DramaEpisodeMeta>(detail.Episodes.Count);
        foreach (var ep in detail.Episodes.OrderBy(e => e.Number))
        {
            if (old.TryGetValue(ep.Number, out var kept))
            {
                kept.DurationText = kept.DurationText is { Length: > 0 } ? kept.DurationText : ep.DurationText;
                episodes.Add(kept);
            }
            else
            {
                episodes.Add(new DramaEpisodeMeta { Number = ep.Number, DurationText = ep.DurationText });
            }
        }
        meta.Episodes = episodes;
        return meta;
    }

    public void Save(string folder, DramaMetadata meta)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var json = JsonSerializer.Serialize(meta, JsonOptions);
            var path = Path.Combine(folder, MetadataFileName);
            // 先写临时文件再替换：避免断电/崩溃留下半个 JSON 把整部剧读废
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDramaLibrary] 写入 {folder} 失败：{ex.Message}");
        }
    }

    /// <summary>记录某一集下载完成。目录不在库根之下时返回 false（不是短剧下载的任务）。</summary>
    public bool RecordEpisodeDone(string? outputPath, int number, long sizeBytes)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return false;
        try
        {
            var folder = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(folder)) return false;
            var local = TryLoad(folder);
            if (local is null) return false;

            var meta = local.Metadata;
            var ep = meta.Episodes.FirstOrDefault(e => e.Number == number);
            if (ep is null)
            {
                ep = new DramaEpisodeMeta { Number = number };
                meta.Episodes.Add(ep);
                meta.Episodes.Sort((a, b) => a.Number.CompareTo(b.Number));
            }
            ep.File = Path.GetFileName(outputPath);
            ep.SizeBytes = sizeBytes;
            ep.DownloadedAt = DateTimeOffset.Now;
            ep.Partial = false;
            Save(folder, meta);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDramaLibrary] 回填失败 {outputPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>删除单集文件（走回收站，保留反悔余地）。</summary>
    public bool DeleteEpisode(LocalDrama drama, int number)
    {
        var path = drama.ResolveEpisodeFile(number);
        if (path is null) return false;
        if (!SendToRecycleBin(path)) return false;

        var meta = drama.Metadata;
        var ep = meta.Episodes.FirstOrDefault(e => e.Number == number);
        if (ep is not null)
        {
            ep.File = null;
            ep.SizeBytes = 0;
            ep.DownloadedAt = null;
            ep.Partial = false;
            Save(drama.FolderPath, meta);
        }
        return true;
    }

    /// <summary>删除整部剧（走回收站）。</summary>
    public bool DeleteDrama(LocalDrama drama)
    {
        try
        {
            if (!Directory.Exists(drama.FolderPath)) return true;
            // 显式声明：目标是库根目录下的子目录，防止误删根目录本身
            var root = Path.GetFullPath(RootDir).TrimEnd(Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(drama.FolderPath).TrimEnd(Path.DirectorySeparatorChar);
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.Warn($"[ShortDramaLibrary] 拒绝删除库目录之外的路径：{target}");
                return false;
            }
            return SendToRecycleBin(target);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDramaLibrary] 删除失败 {drama.FolderPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>目录名净化：去掉 Windows 非法字符与结尾点/空格。</summary>
    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0) cleaned = "未命名短剧";
        return cleaned.Length > 80 ? cleaned[..80].TrimEnd('.', ' ') : cleaned;
    }

    /// <summary>删除到回收站；失败时退回直接删除（只在明确目标路径后调用）。</summary>
    private bool SendToRecycleBin(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return true;
            }
            if (File.Exists(path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return true;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDramaLibrary] 回收站失败，改直接删除 {path}: {ex.Message}");
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception inner)
            {
                _logger?.Warn($"[ShortDramaLibrary] 直接删除也失败 {path}: {inner.Message}");
                return false;
            }
        }
    }
}
