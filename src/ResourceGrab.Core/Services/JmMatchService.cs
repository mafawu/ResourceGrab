using System.Net.Http;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Sources.Jm;
using ResourceGrab.Core.Utils;

namespace ResourceGrab.Core.Services;

/// <summary>匹配判定状态：唯一命中 / 多候选歧义 / 未找到。</summary>
public enum ComicMatchStatus
{
    Matched,
    Ambiguous,
    NotFound,
}

/// <summary>带相似度得分的候选结果。</summary>
public class ComicMatchCandidate
{
    public ComicSummary Summary { get; init; } = new();
    public double Score { get; init; }
}

/// <summary>匹配结果：Candidates 按分数降序；Matched 时首项为自动判定项。</summary>
public class ComicMatchResult
{
    public ComicMatchStatus Status { get; init; }
    public List<ComicMatchCandidate> Candidates { get; init; } = new();

    /// <summary>自动判定的命中项（仅 Matched 时非空）。</summary>
    public ComicMatchCandidate? Best =>
        Status == ComicMatchStatus.Matched ? Candidates.FirstOrDefault() : null;
}

/// <summary>
/// 本地漫画 → 禁漫在线匹配：从目录名解析标题，搜索禁漫并对候选打分排序。
/// 唯一性判定按短路链执行：已有 id 无需匹配；目录名中的疑似专辑 id 由站点直接验证；
/// 标题搜索被站点重定向到唯一专辑、或分数达标且明显领先时自动命中；
/// 其余情况返回按分排序的候选列表交由用户挑选。
/// </summary>
public class JmMatchService
{
    private const string SourceId = "jm";

    private readonly JmSource _source;
    private readonly LocalLibraryService _localLib;

    public JmMatchService(JmSource source, LocalLibraryService localLib)
    {
        _source = source;
        _localLib = localLib;
    }

    /// <summary>搜索并打分，不做任何写操作。网络异常向上抛出由调用方提示。</summary>
    public async Task<ComicMatchResult> MatchAsync(LocalComic comic, CancellationToken ct = default)
    {
        var dirName = Path.GetFileName(comic.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var parsed = MangaFilenameParser.Parse(dirName);
        var keyword = parsed.Title.Length > 0 ? parsed.Title : (comic.NameCn.Length > 0 ? comic.NameCn : comic.Name);

        // 1) 目录名中的疑似专辑 id：数字 id 搜索命中唯一专辑时站点直接返回该专辑，确定唯一
        foreach (var id in ComicTitleMatcher.ExtractJmAlbumIds(dirName))
        {
            SearchResult? byId;
            try
            {
                byId = await _source.SearchAsync(id, 1, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // id 探测失败不阻断标题搜索（可能只是巧合数字或网络抖动）
                continue;
            }
            if (byId.SingleComicId is { } hitId)
            {
                return await SingleMatchAsync(hitId, ct);
            }
        }

        if (keyword.Length == 0)
        {
            return new ComicMatchResult { Status = ComicMatchStatus.NotFound };
        }

        // 2) 标题搜索：站点命中唯一专辑时同样直接确定
        var byTitle = await _source.SearchAsync(keyword, 1, ct);
        if (byTitle.SingleComicId is { } singleId)
        {
            return await SingleMatchAsync(singleId, ct);
        }

        // 零命中时带上作者再试一次（作者 + 标题是禁漫常见的完整命名）
        if (byTitle.Items.Count == 0 && comic.Author.FirstOrDefault() is { Length: > 0 } author)
        {
            byTitle = await _source.SearchAsync($"{keyword} {author}", 1, ct);
            if (byTitle.SingleComicId is { } singleId2)
            {
                return await SingleMatchAsync(singleId2, ct);
            }
        }

        // 3) 多个候选：打分排序；本地为汉化本时优先标题含翻译标记的候选（同一本的汉化/生肉是不同 id）
        var authors = comic.Author.Count > 0
            ? comic.Author
            : parsed.Tags.Where(t => t.StartsWith("作者:", StringComparison.Ordinal))
                .Select(t => t["作者:".Length..])
                .ToList();
        var candidates = byTitle.Items
            .Select(item => new ComicMatchCandidate
            {
                Summary = item,
                Score = ComicTitleMatcher.Score(keyword, item.Title, authors, item.Author),
            })
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => LooksTranslated(c.Summary.Title) ? 1 : 0)
            .ToList();

        if (candidates.Count == 0)
        {
            return new ComicMatchResult { Status = ComicMatchStatus.NotFound };
        }

        var status = ComicTitleMatcher.AcceptsAutoMatch(candidates.Select(c => c.Score).ToList())
            ? ComicMatchStatus.Matched
            : ComicMatchStatus.Ambiguous;
        return new ComicMatchResult { Status = status, Candidates = candidates };
    }

    /// <summary>
    /// 把选定的禁漫专辑回填到本地漫画目录：重建 album.json（保留已有 nameCn）、
    /// 写 source.json 记录来源 id（再次扫描时不再重复匹配）、下载封面到 cover.jpg。
    /// </summary>
    public async Task<ComicDetail> ApplyMatchAsync(LocalComic comic, string comicId, CancellationToken ct = default)
    {
        if (comic is null || string.IsNullOrWhiteSpace(comic.Path) || string.IsNullOrWhiteSpace(comicId))
        {
            throw new ArgumentException("本地漫画或专辑 id 无效");
        }

        var (detail, raw) = await _source.GetComicWithRawAsync(comicId, ct);
        var albumDir = comic.Path;
        _localLib.SaveMatchedMetadata(albumDir, AlbumBuilder.Build(raw, ""),
            TitleTranslator.ExtractChineseName(detail.Title));
        _localLib.SaveSourceMetadataForDir(albumDir, SourceId, detail);
        await DownloadCoverAsync(albumDir, detail.CoverUrl, ct);
        return detail;
    }

    /// <summary>站点唯一命中：拉取详情生成 100% 置信的候选（供 UI 展示与确认）。</summary>
    private async Task<ComicMatchResult> SingleMatchAsync(string comicId, CancellationToken ct)
    {
        var detail = await _source.GetComicAsync(comicId, ct);
        var candidate = new ComicMatchCandidate
        {
            Summary = new ComicSummary
            {
                Id = detail.Id,
                Title = detail.Title,
                Author = string.Join(", ", detail.Authors),
                CoverUrl = detail.CoverUrl,
            },
            Score = 1.0,
        };
        return new ComicMatchResult
        {
            Status = ComicMatchStatus.Matched,
            Candidates = { candidate },
        };
    }

    private static bool LooksTranslated(string title)
        => title.Contains("中国翻訳", StringComparison.OrdinalIgnoreCase)
           || title.Contains("中国翻译", StringComparison.OrdinalIgnoreCase)
           || title.Contains("中文", StringComparison.OrdinalIgnoreCase)
           || title.Contains("汉化", StringComparison.OrdinalIgnoreCase)
           || title.Contains("漢化", StringComparison.OrdinalIgnoreCase);

    private static async Task DownloadCoverAsync(string albumDir, string coverUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            return;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", JmConstants.UserAgent);
            http.DefaultRequestHeaders.TryAddWithoutValidation("referer", $"https://{JmConstants.ApiDomain}/");
            var bytes = await http.GetByteArrayAsync(coverUrl, ct);
            if (bytes.Length == 0)
            {
                return;
            }
            await File.WriteAllBytesAsync(Path.Combine(albumDir, "cover.jpg"), bytes, ct);
        }
        catch (Exception ex)
        {
            // 封面下载失败不阻断匹配，本地扫描会回退到第一章图片
            Console.Error.WriteLine($"下载封面失败: {ex.Message}");
        }
    }
}
