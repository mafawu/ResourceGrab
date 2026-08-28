using ResourceGrab.Core.Logging;

namespace ResourceGrab.Core.Services.VideoScrape;

/// <summary>
/// 任务队列的真实执行器：把队列里的刮削任务交给 VideoScrapeService 执行，
/// 并把批内进度、成功/失败计数和日志回填到任务，供看板与进度条读取。
/// </summary>
public sealed class VideoScrapeTaskExecutor
{
    private readonly VideoScrapeService _scrapeService;
    private readonly VideoScrapeTaskQueue _queue;
    private readonly ILogger? _logger;

    public VideoScrapeTaskExecutor(VideoScrapeService scrapeService, VideoScrapeTaskQueue queue, ILogger? logger = null)
    {
        _scrapeService = scrapeService;
        _queue = queue;
        _logger = logger;
    }

    public async Task ExecuteAsync(VideoScrapeTask task, CancellationToken ct)
    {
        var ids = task.ItemIds is { Count: > 0 } items
            ? items
            : task.VideoItemId is { Length: > 0 } itemId
                ? [itemId]
                : [];

        if (ids.Count == 0)
        {
            throw new InvalidOperationException($"任务 {task.Id} 没有关联的视频条目，无法刮削");
        }

        task.Total = ids.Count;
        task.Logs = [];
        // 批量任务跳过已成功条目（重试/重跑不再重新联网）；显式单条重刮削强制联网。
        var skipSucceeded = ids.Count > 1 && task.Type != VideoTaskType.RescrapeVideo;
        await _scrapeService.ScrapeAsync(ids, progress =>
        {
            lock (task)
            {
                if (progress.Total > 0) task.Total = progress.Total;
                task.Completed = progress.Completed;
                task.SuccessCount = progress.SuccessCount;
                task.NoMatchCount = progress.NoMatchCount;
                task.FailedCount = progress.FailedCount;
                task.SkippedCount = progress.SkippedCount;
                task.Logs = progress.Logs.ToList();
                task.Error = "";
                _queue.NotifyTaskProgress();
            }
        }, ct, skipSucceeded);

        _logger?.Info($"[TaskExecutor] 完成 {task.Id}: {task.Completed}/{task.Total} " +
                      $"成功{task.SuccessCount} 未匹配{task.NoMatchCount} 失败{task.FailedCount} 跳过{task.SkippedCount}");
    }
}
