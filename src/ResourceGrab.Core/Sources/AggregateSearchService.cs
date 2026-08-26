namespace ResourceGrab.Core.Sources;

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
    public AggregateSearchService(IEnumerable<IComicSource> sources)
    {
        _sources = sources.Where(s => !s.Info.RequiresLogin).ToList();
    }
    public IReadOnlyList<IComicSource> Sources => _sources;
    public async Task<AggregateSearchResult> SearchAsync(string keyword, int page, CancellationToken ct = default)
    {
        var tasks = _sources.Select(s => SearchOneAsync(s, keyword, page, ct)).ToArray();
        var groups = await Task.WhenAll(tasks);
        return new AggregateSearchResult { Groups = groups, Page = page };
    }
    public Task<SourceSearchGroup> SearchSourceAsync(IComicSource source, string keyword, int page, CancellationToken ct = default) => SearchOneAsync(source, keyword, page, ct);
    private static async Task<SourceSearchGroup> SearchOneAsync(IComicSource source, string keyword, int page, CancellationToken ct)
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
            return new SourceSearchGroup { Source = source, Error = "Search timeout" };
        }
        catch (Exception ex)
        {
            return new SourceSearchGroup { Source = source, Error = ex.Message };
        }
    }
}
