using System.Text.RegularExpressions;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Utils;

namespace ResourceGrab.Core.Services;

// ---------------------------------------------------------------------------
// 作者合集：一键搜某作者在禁漫天堂 + 绅士漫画的全部作品，作者精确过滤、
// 跨源按归一化标题去重（禁漫优先），中文版判定供 UI 默认勾选。
// ---------------------------------------------------------------------------

public sealed class AuthorWorkItem
{
    public required string SourceId { get; init; }
    public required string SourceName { get; init; }
    public required string ComicId { get; init; }
    public required string Title { get; init; }
    public required string Author { get; init; }
    /// <summary>是否中文版（汉化标记，或含汉字且无假名）。</summary>
    public bool HasChinese { get; init; }
}

public sealed class AuthorWorksResult
{
    public required string Author { get; init; }
    public List<AuthorWorkItem> Items { get; init; } = [];
    /// <summary>去重掉的条目数（跨源同名）。</summary>
    public int DroppedDuplicates { get; set; }
    /// <summary>各源原始命中数（过滤前），key = sourceId。</summary>
    public Dictionary<string, int> RawCounts { get; init; } = new();
    public List<string> Errors { get; init; } = [];
}

public sealed class AuthorWorksService
{
    private static readonly TimeSpan PerPageTimeout = TimeSpan.FromSeconds(12);
    /// <summary>单源整体预算：超时后保留已取到的页，不再翻页（慢源不拖死全局）。</summary>
    private static readonly TimeSpan PerSourceBudget = TimeSpan.FromSeconds(45);
    /// <summary>单源最多翻页数（防无底洞）。</summary>
    public const int MaxPagesPerSource = 8;

    /// <summary>汉化标记（方括号/圆括号内的汉化·中文·翻译字样）。</summary>
    private static readonly Regex TranslationMarkRegex = new(
        @"[【\[][^【】\[\]]*(?:汉化|漢化|中文|翻譯|翻译)[^【】\[\]]*[】\]]"
        + @"|[（\(][^（）()]*?(?:汉化|漢化|中文|翻譯|翻译)[^（）()]*?[）\)]",
        RegexOptions.Compiled);
    private static readonly Regex CjkRegex = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
    private static readonly Regex KanaRegex = new(@"[\p{IsHiragana}\p{IsKatakana}]", RegexOptions.Compiled);
    private static readonly char[] AuthorSeparators = ['、', ',', '，', '/', '／', '&', '+'];

    private readonly IReadOnlyList<IComicSource> _sources;

    /// <param name="sources">参与合集的源（调用方传入，通常为 jm + wnacg；顺序即去重优先级）。</param>
    public AuthorWorksService(IEnumerable<IComicSource> sources)
    {
        _sources = sources.ToList();
    }

    public async Task<AuthorWorksResult> SearchAuthorAsync(
        string author, Action<string>? report = null, CancellationToken ct = default)
    {
        var result = new AuthorWorksResult { Author = author };
        if (string.IsNullOrWhiteSpace(author) || _sources.Count == 0) return result;

        // 双源并行：总耗时取决于最慢的源，而不是两者之和
        var tasks = _sources.Select(s => SearchOneSourceAsync(s, author, report, ct)).ToArray();
        var partials = await Task.WhenAll(tasks);
        foreach (var (source, items, rawCount, error, truncated) in partials)
        {
            result.RawCounts[source.Info.Id] = rawCount;
            if (error is not null) result.Errors.Add(error);
            else if (truncated)
                result.Errors.Add($"{source.Info.DisplayName}只取到前 {items.Count} 本（超时，后续页未取）");
            foreach (var summary in items)
            {
                if (!IsAuthorMatch(author, summary.Author)) continue;
                result.Items.Add(new AuthorWorkItem
                {
                    SourceId = source.Info.Id,
                    SourceName = source.Info.DisplayName,
                    ComicId = summary.Id,
                    Title = summary.Title,
                    Author = summary.Author,
                    HasChinese = HasChineseEdition(summary.Title),
                });
            }
        }

        Deduplicate(result);
        return result;
    }

    /// <returns>(源, 原始条目, 原始总数, 错误信息, 是否因超时截断)。用户取消时抛 OperationCanceledException。</returns>
    private static async Task<(IComicSource Source, List<ComicSummary> Items, int RawCount, string? Error, bool Truncated)>
        SearchOneSourceAsync(IComicSource source, string author, Action<string>? report, CancellationToken ct)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(PerSourceBudget);
        var budgetCt = budgetCts.Token;
        var all = new List<ComicSummary>();
        try
        {
            report?.Invoke($"正在搜索{source.Info.DisplayName}…");
            var first = await SearchPageAsync(source, author, 1, budgetCt);
            all.AddRange(first.Items);
            var totalPages = (int)Math.Min(first.TotalPages, MaxPagesPerSource);
            for (var page = 2; page <= totalPages; page++)
            {
                budgetCt.ThrowIfCancellationRequested();
                report?.Invoke($"正在搜索{source.Info.DisplayName}（第 {page}/{totalPages} 页，已取 {all.Count} 本）…");
                // 单本直达（SingleComicId）只有第一页有意义，后续页按普通列表取
                var r = await SearchPageAsync(source, author, page, budgetCt);
                all.AddRange(r.Items);
                if (r.Items.Count == 0) break;
            }
            return (source, all, all.Count, null, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 用户主动取消
        }
        catch (OperationCanceledException)
        {
            // 单页超时或整源预算耗尽：保留已取到的页
            var reason = all.Count > 0 ? null : $"{source.Info.DisplayName}搜索超时";
            return (source, all, all.Count, reason, all.Count > 0);
        }
        catch (Exception ex)
        {
            var reason = all.Count > 0 ? null : $"{source.Info.DisplayName}搜索失败：{ex.Message}";
            return (source, all, all.Count, reason, all.Count > 0);
        }
    }

    private static async Task<SearchResult> SearchPageAsync(
        IComicSource source, string keyword, int page, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PerPageTimeout);
        var r = await source.SearchAsync(keyword, page, timeoutCts.Token);
        // 单本直达时把该本也纳入合集候选
        if (r.IsSingleMatch && !string.IsNullOrEmpty(r.SingleComicId) && r.Items.Count == 0)
        {
            try
            {
                var detail = await source.GetComicAsync(r.SingleComicId, timeoutCts.Token);
                return new SearchResult
                {
                    Total = 1,
                    TotalPages = 1,
                    Items = [new ComicSummary
                    {
                        Id = detail.Id, Title = detail.Title,
                        Author = string.Join("、", detail.Authors),
                    }],
                };
            }
            catch { }
        }
        return r;
    }

    /// <summary>跨源去重：归一化标题相同视为同一本，保留来源顺序靠前的（调用方把禁漫放前面）。</summary>
    public static void Deduplicate(AuthorWorksResult result)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<AuthorWorkItem>();
        foreach (var item in result.Items)
        {
            var key = ComicTitleMatcher.NormalizeTitle(item.Title);
            if (string.IsNullOrEmpty(key)) continue;
            if (!seen.Add(key))
            {
                result.DroppedDuplicates++;
                continue;
            }
            kept.Add(item);
        }
        result.Items.Clear();
        result.Items.AddRange(kept);
    }

    /// <summary>作者匹配：归一化后互相包含（源作者字段常为多作者逗号分隔）。</summary>
    public static bool IsAuthorMatch(string query, string? authorField)
    {
        var q = NormalizeAuthor(query);
        if (q.Length == 0) return false;
        if (string.IsNullOrWhiteSpace(authorField)) return false;
        return authorField.Split(AuthorSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeAuthor)
            .Any(a => a.Length > 0 && (a.Contains(q, StringComparison.Ordinal) || q.Contains(a, StringComparison.Ordinal)));
    }

    public static string NormalizeAuthor(string author)
        => new string(author.ToLowerInvariant()
            .Where(c => !char.IsWhiteSpace(c) && c is not '·' and not '・')
            .ToArray());

    /// <summary>是否中文版：显式汉化/中文标记，或含汉字且无假名（纯汉字标题默认中文，假名残留视为日文）。</summary>
    public static bool HasChineseEdition(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (TranslationMarkRegex.IsMatch(title)) return true;
        return CjkRegex.IsMatch(title) && !KanaRegex.IsMatch(title);
    }
}
