using System.Diagnostics;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M1: LegacyJavDbSource — 将现有 JavDbScraper 包装为 IVideoScrapeSource
// ---------------------------------------------------------------------------

public sealed class LegacyJavDbSource : IVideoScrapeSource
{
    private readonly JavDbScraper _legacy;

    public string Id => _legacy.Id;
    public string DisplayName => _legacy.DisplayName;
    public IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>(
        [
            VideoContentKind.Censored,
            VideoContentKind.Uncensored,
            VideoContentKind.Fc2,
            VideoContentKind.Chinese,
            VideoContentKind.Amateur,
            VideoContentKind.Western,
            VideoContentKind.Hentai
        ]);

    public LegacyJavDbSource(JavDbScraper legacy) => _legacy = legacy;

    public async ValueTask<VideoSourceFetchResult> FetchAsync(
        VideoSourceRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var metadata = await _legacy.SearchAsync(request.Number, ct);
            sw.Stop();
            return metadata is null
                ? VideoSourceFetchResult.NoMatch(Id, (int)sw.ElapsedMilliseconds)
                : VideoSourceFetchResult.Success(Id, metadata, (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return VideoSourceFetchResult.Failure(Id, VideoSourceOutcome.Cancelled, "Cancelled", (int)sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            var outcome = ex.StatusCode is System.Net.HttpStatusCode.Forbidden
                ? VideoSourceOutcome.Blocked
                : VideoSourceOutcome.HttpError;
            return VideoSourceFetchResult.Failure(Id, outcome, ex.Message, (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return VideoSourceFetchResult.Failure(Id, VideoSourceOutcome.ParseError, ex.Message, (int)sw.ElapsedMilliseconds);
        }
    }
}
