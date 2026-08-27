using System.Diagnostics;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// M8: DMM — 有码核心来源（HTML + API 双通道，日文原题、高清图）
// 状态: 待实现 — 需要实际抓取逻辑
// ---------------------------------------------------------------------------

public sealed class DmmSource : IVideoScrapeSource
{
    public string Id => "dmm";
    public string DisplayName => "DMM";
    public IReadOnlySet<VideoContentKind> SupportedKinds { get; } =
        new HashSet<VideoContentKind>([VideoContentKind.Censored, VideoContentKind.Amateur, VideoContentKind.Hentai]);

    public async ValueTask<VideoSourceFetchResult> FetchAsync(
        VideoSourceRequest request, CancellationToken ct)
    {
        // TODO: 实现 DMM HTML/API 抓取逻辑
        // DMM 提供: 日文原题、高清封面图、官方元数据
        await Task.CompletedTask;
        return VideoSourceFetchResult.Failure(Id, VideoSourceOutcome.Disabled, "Not yet implemented");
    }
}
