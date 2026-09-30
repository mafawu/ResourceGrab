namespace ResourceGrab.Core.Sources;

using ResourceGrab.Core.Logging;

public class SourceSearchGroup
{
    public required IComicSource Source { get; init; }
    public SearchResult? Result { get; init; }
    public string? Error { get; init; }
}

public class AggregateSearchResult
{
    public IReadOnlyList<SourceSearchGroup> Groups { get; init; } = Array.Empty<SourceSearchGroup>();
    public int Page { get; init; }
    public long TotalPages => Groups.Count == 0 ? 1 : Groups.Max(g => g.Result?.TotalPages ?? 1);
}

public class AggregateSearchService
{
    private static readonly TimeSpan PerSourceTimeout = TimeSpan.FromSeconds(12);
    private readonly IReadOnlyList<IComicSource> _sources;
    private readonly ILogger? _logger;
    public AggregateSearchService(IEnumerable<IComicSource> sources, ILogger? logger = null)
    {
        _sources = sources.Where(s => !s.Info.RequiresLogin).ToList();
        _logger = logger;
    }
    public IReadOnlyList<IComicSource> Sources => _sources;
    public async Task<AggregateSearchResult> SearchAsync(string keyword, int page, CancellationToken ct = default)
    {
        var tasks = _sources.Select(s => SearchOneAsync(s, keyword, page, ct)).ToArray();
        var groups = await Task.WhenAll(tasks);
        return new AggregateSearchResult { Groups = groups, Page = page };
    }
    public Task<SourceSearchGroup> SearchSourceAsync(IComicSource source, string keyword, int page, CancellationToken ct = default) => SearchOneAsync(source, keyword, page, ct);
    private async Task<SourceSearchGroup> SearchOneAsync(IComicSource source, string keyword, int page, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PerSourceTimeout);
        try
        {
            var result = await source.SearchAsync(keyword, page, timeoutCts.Token);
            return new SourceSearchGroup { Source = source, Result = result };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger?.Warn($"[Search] {source.Info.Id} 搜索超时（{PerSourceTimeout.TotalSeconds}s）");
            return new SourceSearchGroup { Source = source, Error = "Search timeout" };
        }
        catch (Exception ex)
        {
            // 搜索失败此前只体现在 UI toast，App 日志里查不到原因；这里落盘便于排查网络/域名问题
            _logger?.Warn($"[Search] {source.Info.Id} 搜索失败: {ex.Message}");
            return new SourceSearchGroup { Source = source, Error = ex.Message };
        }
    }
}
