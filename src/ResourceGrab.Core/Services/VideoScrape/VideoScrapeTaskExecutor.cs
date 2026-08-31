using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape;

/// <summary>
/// 任务队列的真实执行器：按配置的刮削引擎把队列里的刮削任务分发给
/// VideoScrapeService（legacy，老三源管道）或 VideoGraphScrapeOrchestrator（graph，
/// 图调度多源择优），并把批内进度、成功/失败计数和日志回填到任务，供看板与进度条读取。
/// </summary>
public sealed class VideoScrapeTaskExecutor
{
    private readonly VideoScrapeService _scrapeService;
    private readonly VideoGraphScrapeOrchestrator? _graphOrchestrator;
    private readonly VideoScrapeTaskQueue _queue;
    private readonly ConfigService? _configService;
    private readonly ILogger? _logger;

    public VideoScrapeTaskExecutor(VideoScrapeService scrapeService, VideoScrapeTaskQueue queue,
        ILogger? logger = null, VideoGraphScrapeOrchestrator? graphOrchestrator = null,
        ConfigService? configService = null)
    {
        _scrapeService = scrapeService;
        _graphOrchestrator = graphOrchestrator;
        _queue = queue;
        _configService = configService;
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

        // 引擎选择：仅 "legacy" 走老管道，其余（graph/空/非法值）按配置合法性处理
        var settings = _configService?.Current.VideoScraping ?? new VideoScrapeSettings();
        var engine = (settings.Advanced ?? new VideoScrapeAdvancedSettings()).Engine?.Trim().ToLowerInvariant() ?? "graph";
        if (engine is not ("graph" or "legacy"))
        {
            _logger?.Warn($"[TaskExecutor] 未知刮削引擎 \"{engine}\"，回退 legacy");
            engine = "legacy";
        }
        var useGraph = engine == "graph" && _graphOrchestrator is not null;
        if (engine == "graph" && _graphOrchestrator is null)
        {
            _logger?.Warn("[TaskExecutor] 配置为 graph 引擎但编排器未注册，回退 legacy");
        }

        task.Total = ids.Count;
        task.Logs = [];
        // 批量任务跳过已成功条目（重试/重跑不再重新联网）；显式单条重刮削强制联网。
        var skipSucceeded = ids.Count > 1 && task.Type != VideoTaskType.RescrapeVideo;

        Action<VideoScrapeProgress> backfill = progress =>
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
                task.ItemResults = progress.ItemResults.ToList();
                task.Error = "";
                _queue.NotifyTaskProgress();
            }
        };

        if (useGraph)
            await _graphOrchestrator!.ScrapeAsync(ids, backfill, ct, skipSucceeded);
        else
            await _scrapeService.ScrapeAsync(ids, backfill, ct, skipSucceeded);

        _logger?.Info($"[TaskExecutor] 完成 {task.Id} (engine={engine}): {task.Completed}/{task.Total} " +
                      $"成功{task.SuccessCount} 未匹配{task.NoMatchCount} 失败{task.FailedCount} 跳过{task.SkippedCount}");
    }
}
