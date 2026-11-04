using System.Text.RegularExpressions;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Sources.ShortDrama;

namespace ResourceGrab.Core.Services.ShortDrama;

/// <summary>导入结果：一部剧一条。</summary>
public sealed class DramaImportItem
{
    public string FolderPath { get; init; } = "";
    public string GuessedTitle { get; init; } = "";
    public int LocalEpisodeCount { get; init; }
    /// <summary>匹配到的在线剧集 id；空表示没匹配上。</summary>
    public string? MatchedId { get; init; }
    public string? MatchedTitle { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}

public sealed class DramaImportReport
{
    public List<DramaImportItem> Items { get; } = [];
    public int Succeeded => Items.Count(i => i.Success);
    public int Unmatched => Items.Count(i => !i.Success && i.Error is null);
    public int Failed => Items.Count(i => i.Error is not null);
}

/// <summary>
/// 把「用户原本就有的短剧文件」接进本地库：
/// 扫目录 → 猜剧名 → 去站点搜同一部 → 用在线详情补元数据 → 写 drama.json + 解密封面 → 登记到库里。
/// <para>
/// 视频文件本身不动、不复制、不重命名，只在原目录旁边写一个 <c>drama.json</c> 和 <c>poster.jpg</c>。
/// 匹配不到就如实报告，不做"强行关联"（错关联比不关联更糟）。
/// </para>
/// </summary>
public sealed class DramaLocalImportService
{
    private static readonly string[] VideoExtensions =
        [".mp4", ".mkv", ".ts", ".mov", ".m4v", ".webm", ".avi", ".flv", ".wmv"];

    private readonly IShortDramaSource _source;
    private readonly ShortDramaLibraryService _library;
    private readonly DramaCoverService? _covers;
    private readonly ILogger? _logger;

    public DramaLocalImportService(
        IShortDramaSource source,
        ShortDramaLibraryService library,
        DramaCoverService? covers = null,
        ILogger? logger = null)
    {
        _source = source;
        _library = library;
        _covers = covers;
        _logger = logger;
    }

    /// <summary>
    /// 扫描并导入。<paramref name="rootFolder"/> 的每个**子目录**视为一部剧；
    /// 若该目录自己就散放着视频文件，则整体视为一部剧。
    /// <para>
    /// 若传入的是**单个视频文件**：先在同目录新建以片名命名的文件夹，把文件移进去，
    /// 再按“一部剧一个目录”的流程刮削、写 drama.json/poster.jpg 并登记。
    /// </para>
    /// </summary>
    public async Task<DramaImportReport> ImportAsync(
        string rootFolder,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (File.Exists(rootFolder))
            return await ImportSingleFileAsync(rootFolder, progress, ct).ConfigureAwait(false);

        var report = new DramaImportReport();
        if (!Directory.Exists(rootFolder)) return report;

        foreach (var folder in EnumerateDramaFolders(rootFolder))
        {
            ct.ThrowIfCancellationRequested();
            var videos = FindVideos(folder);
            if (videos.Count == 0) continue;

            var title = GuessTitle(folder, rootFolder);
            progress?.Report($"正在匹配：{title}");

            var item = await ImportOneAsync(folder, title, videos, ct).ConfigureAwait(false);
            report.Items.Add(item);
        }

        return report;
    }

    /// <summary>
    /// 单个视频：同目录建“片名”文件夹 → 移入 → 刮削入库。
    /// 文件移动失败时返回错误项，不留下空文件夹。
    /// </summary>
    private async Task<DramaImportReport> ImportSingleFileAsync(
        string videoPath, IProgress<string>? progress, CancellationToken ct)
    {
        var report = new DramaImportReport();
        var fullPath = Path.GetFullPath(videoPath);
        var fileName = Path.GetFileName(fullPath);
        var localCount = IsVideoFile(fullPath) ? 1 : 0;

        if (!IsVideoFile(fullPath))
        {
            report.Items.Add(new DramaImportItem
            {
                FolderPath = Path.GetDirectoryName(fullPath) ?? "",
                GuessedTitle = Path.GetFileNameWithoutExtension(fullPath),
                LocalEpisodeCount = 0,
                Error = "不是支持的视频文件",
            });
            return report;
        }

        var title = GuessTitleFromFile(fullPath);
        progress?.Report($"正在整理：{fileName}");

        string folder;
        string target;
        try
        {
            var parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(parent))
            {
                report.Items.Add(new DramaImportItem
                {
                    FolderPath = "",
                    GuessedTitle = title,
                    LocalEpisodeCount = localCount,
                    Error = "无法确定视频所在目录",
                });
                return report;
            }

            var folderName = ShortDramaLibraryService.SanitizeFolderName(title);
            folder = CreateUniqueFolder(parent, folderName);
            target = Path.Combine(folder, fileName);
            try
            {
                File.Move(fullPath, target);
            }
            catch
            {
                // 移动失败：清掉刚建的空目录，原文件留在原处
                try { Directory.Delete(folder, recursive: false); } catch { }
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[DramaImport] 单文件整理失败 {fullPath}：{ex.Message}");
            report.Items.Add(new DramaImportItem
            {
                FolderPath = Path.GetDirectoryName(fullPath) ?? "",
                GuessedTitle = title,
                LocalEpisodeCount = localCount,
                Error = ex.Message,
            });
            return report;
        }

        var item = await ImportOneAsync(folder, title, [target], ct).ConfigureAwait(false);
        report.Items.Add(item);
        return report;
    }

    /// <summary>同目录创建唯一文件夹；重名时追加 (2)、(3)… 避免混入已有剧集。</summary>
    private static string CreateUniqueFolder(string parent, string baseName)
    {
        for (var i = 1; i <= 999; i++)
        {
            var name = i == 1 ? baseName : $"{baseName} ({i})";
            var path = Path.Combine(parent, name);
            if (Directory.Exists(path) || File.Exists(path)) continue;
            Directory.CreateDirectory(path);
            return path;
        }
        throw new IOException($"无法创建文件夹：{baseName}");
    }

    private async Task<DramaImportItem> ImportOneAsync(
        string folder, string title, IReadOnlyList<string> videos, CancellationToken ct)
    {
        var localCount = videos.Count;
        try
        {
            var match = await MatchAsync(title, ct).ConfigureAwait(false);
            if (match is null)
            {
                _logger?.Info($"[DramaImport] 未匹配到在线剧集：{title}（{folder}）");
                return new DramaImportItem
                {
                    FolderPath = folder,
                    GuessedTitle = title,
                    LocalEpisodeCount = localCount,
                };
            }

            var detail = await _source.GetDetailAsync(match.Id, ct).ConfigureAwait(false);
            if (detail is null)
            {
                return new DramaImportItem
                {
                    FolderPath = folder, GuessedTitle = title, LocalEpisodeCount = localCount,
                    Error = "详情解析失败",
                };
            }

            var meta = _library.MergeDetail(detail, folder);
            var assigned = AssignLocalFiles(meta, folder, videos);
            meta.ImportedFrom = folder;
            _library.Save(folder, meta);
            _library.RegisterExternal(folder);

            // 封面：在线封面是加密的，走解密服务存成 poster.jpg（失败不影响导入）
            if (_covers is not null && !string.IsNullOrWhiteSpace(detail.CoverUrl))
            {
                try
                {
                    var cover = await _covers.EnsureAsync(detail.Id, detail.CoverUrl, ct).ConfigureAwait(false);
                    if (cover is not null)
                    {
                        var target = Path.Combine(folder, "poster.jpg");
                        if (!File.Exists(target)) File.Copy(cover, target, overwrite: true);
                        meta.CoverFile = "poster.jpg";
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"[DramaImport] 封面处理失败 {title}：{ex.Message}");
                }
            }

            _logger?.Info($"[DramaImport] 已导入 {title} → 《{detail.Title}》{assigned}/{localCount} 集");
            return new DramaImportItem
            {
                FolderPath = folder,
                GuessedTitle = title,
                LocalEpisodeCount = localCount,
                MatchedId = detail.Id,
                MatchedTitle = detail.Title,
                Success = true,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.Warn($"[DramaImport] 导入失败 {title}：{ex.Message}");
            return new DramaImportItem
            {
                FolderPath = folder, GuessedTitle = title, LocalEpisodeCount = localCount,
                Error = ex.Message,
            };
        }
    }

    /// <summary>把本地文件按集号挂到元数据上（原样引用文件名，不移动文件）。</summary>
    private static int AssignLocalFiles(DramaMetadata meta, string folder, IReadOnlyList<string> videos)
    {
        var assigned = 0;
        var used = new HashSet<int>();

        for (var i = 0; i < videos.Count; i++)
        {
            var path = videos[i];
            var number = ParseEpisodeNumber(Path.GetFileNameWithoutExtension(path)) ?? (i + 1);
            while (used.Contains(number)) number++;

            var episode = meta.Episodes.FirstOrDefault(e => e.Number == number);
            if (episode is null)
            {
                episode = new DramaEpisodeMeta { Number = number };
                meta.Episodes.Add(episode);
            }
            episode.File = Path.GetFileName(path);
            try { episode.SizeBytes = new FileInfo(path).Length; } catch { }
            episode.DownloadedAt = DateTimeOffset.Now;
            used.Add(number);
            assigned++;
        }

        meta.Episodes = meta.Episodes.OrderBy(e => e.Number).ToList();
        if (meta.TotalEpisodes < meta.Episodes.Count) meta.TotalEpisodes = meta.Episodes.Count;
        return assigned;
    }

    private async Task<ShortDramaSummary?> MatchAsync(string title, CancellationToken ct)
    {
        var keyword = CleanForSearch(title);
        if (keyword.Length == 0) return null;

        var listing = await _source.SearchAsync(keyword, 1, ct).ConfigureAwait(false);
        if (listing.Items.Count == 0 && keyword.Length > 4)
        {
            // 名字太长时站点常常搜不到，退一步用前 6 个字再搜一次
            listing = await _source.SearchAsync(keyword[..6], 1, ct).ConfigureAwait(false);
        }
        if (listing.Items.Count == 0) return null;

        var target = Normalize(title);
        ShortDramaSummary? best = null;
        var bestScore = 0.0;
        foreach (var item in listing.Items.Take(12))
        {
            var candidate = Normalize(item.Title);
            double score;
            if (candidate == target) score = 1.0;
            else if (candidate.Contains(target) || target.Contains(candidate)) score = 0.85;
            else score = Dice(target, candidate);

            if (score > bestScore) { bestScore = score; best = item; }
        }

        // 阈值偏严：宁可漏，也不要给用户的剧挂上错误的简介和封面
        return bestScore >= 0.62 ? best : null;
    }

    // ── 目录 / 文件名解析 ──────────────────────────────────────────────

    private static IEnumerable<string> EnumerateDramaFolders(string root)
    {
        var subDirs = Directory.EnumerateDirectories(root).ToList();
        if (subDirs.Count > 0) return subDirs;
        return [root];
    }

    private static List<string> FindVideos(string folder)
        => Directory.EnumerateFiles(folder)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsVideoFile(string path)
        => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 从单个视频文件名猜片名：去掉扩展名、画质/字幕标记、结尾的集号或区间，
    /// 例如 “废柴杂役11-15.mp4” → “废柴杂役”、“某剧 第12集.mkv” → “某剧”。
    /// 猜空时回退到原文件名，避免生成空目录名。
    /// </summary>
    internal static string GuessTitleFromFile(string path)
    {
        var raw = Path.GetFileNameWithoutExtension(path);
        var name = CleanTitle(raw);
        name = Regex.Replace(name,
            @"(?i)(?:[\s._-]*第\s*\d{1,4}\s*[集话話]|[\s._-]*EP?\s*\d{1,4}|[\s._-]*S\d{1,2}E\d{1,2})\s*$",
            " ");
        name = Regex.Replace(name, @"[\s._-]*\d{1,4}(?:\s*[-~至]\s*\d{1,4})?\s*$", " ");
        name = Regex.Replace(name, @"\s+", " ").Trim(' ', '-', '_', '.');
        return name.Length > 0 ? name : raw.Trim();
    }

    /// <summary>剧名：目录名优先（去掉【】[]() 里的标记和"全12集/更新至8集"这类尾巴）。</summary>
    private static string GuessTitle(string folder, string root)
    {
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        if (string.Equals(folder.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            // 整个 root 就是一部剧时，用目录名（可能是"某剧 1-30集"这种）
            name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        }
        return CleanTitle(name);
    }

    internal static string CleanTitle(string raw)
    {
        var s = Regex.Replace(raw, @"[【\[（(][^】\]）)]*[】\]）)]", " ");
        s = Regex.Replace(s, @"(全|共)\s*\d+\s*集", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"更新至\s*\d+\s*集", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b(4K|1080P|720P|HD|HEVC|H264|X264|WEB-?DL|未删减|无删减|完结)\b", " ",
            RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '_', '.');
        return s.Length == 0 ? raw.Trim() : s;
    }

    /// <summary>去掉标点空格，只留字母数字汉字，用于相似度比较。</summary>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Dice 系数（按字符二元组），中文短标题比较稳。</summary>
    internal static double Dice(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return a == b ? 1 : 0;
        var grams = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < a.Length - 1; i++)
        {
            var g = a.Substring(i, 2);
            grams[g] = grams.TryGetValue(g, out var n) ? n + 1 : 1;
        }
        var hit = 0;
        for (var i = 0; i < b.Length - 1; i++)
        {
            var g = b.Substring(i, 2);
            if (grams.TryGetValue(g, out var n) && n > 0)
            {
                grams[g] = n - 1;
                hit++;
            }
        }
        return 2.0 * hit / (a.Length - 1 + b.Length - 1);
    }

    /// <summary>从文件名里认集号：第12集 / EP12 / E12 / 12 / 剧名-12。</summary>
    internal static int? ParseEpisodeNumber(string fileNameWithoutExt)
    {
        var patterns = new[]
        {
            @"第\s*(\d{1,4})\s*[集话話]",
            @"(?:^|[^A-Za-z])EP?\s*(\d{1,4})(?:[^0-9]|$)",
            @"(?:^|[^0-9])(\d{1,4})(?:[^0-9]|$)",
        };
        foreach (var p in patterns)
        {
            var m = Regex.Match(fileNameWithoutExt, p, RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n is > 0 and < 2000) return n;
        }
        return null;
    }

    private static string CleanForSearch(string title)
        => CleanTitle(title).Replace(" ", "").Trim();
}
