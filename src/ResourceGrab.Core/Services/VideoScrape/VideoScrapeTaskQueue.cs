using System.Collections.Concurrent;
using System.Threading.Channels;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M5: 视频刮削任务队列
// 支持取消、重试、优先级排序、进度事件、批量刮削
// ---------------------------------------------------------------------------

public sealed class VideoScrapeTaskQueue : IDisposable
{
    private readonly ConcurrentDictionary<string, VideoScrapeTask> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<VideoScrapeTask> _channel;
    private readonly ILogger? _logger;
    private readonly int _maxConcurrency;
    private int _runningCount;
    private readonly SemaphoreSlim _concurrencyGate;
    private CancellationTokenSource? _globalCts;
    private Task? _consumerTask;

    public event Action<VideoTaskProgress>? ProgressChanged;
    public event Action<VideoScrapeTask>? TaskCompleted;
    public event Action<VideoScrapeTask>? TaskFailed;

    public VideoScrapeTaskQueue(int maxConcurrency = 4, ILogger? logger = null)
    {
        _maxConcurrency = Math.Clamp(maxConcurrency, 1, 16);
        _concurrencyGate = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        _logger = logger;
        _channel = Channel.CreateUnbounded<VideoScrapeTask>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    }

    /// <summary>入队单个任务。</summary>
    public string Enqueue(string number, VideoTaskType type = VideoTaskType.ScrapeVideo,
        string? videoItemId = null, int priority = 0)
    {
        var task = new VideoScrapeTask
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Type = type,
            Number = number,
            VideoItemId = videoItemId,
            Priority = priority
        };
        _tasks[task.Id] = task;
        _channel.Writer.TryWrite(task);
        _logger?.Info($"[TaskQueue] 入队: {task.Id} {type} {number} priority={priority}");
        return task.Id;
    }

    /// <summary>批量入队为单个任务（ScrapeAsync 一次性处理全部）。</summary>
    public string EnqueueBatchTask(List<string> ids, VideoTaskType type = VideoTaskType.ScrapeVideo, string? title = null, int priority = 0)
    {
        var task = new VideoScrapeTask
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Type = type,
            Number = title ?? $"{ids.Count} 个",
            ItemIds = ids,
            Priority = priority
        };
        _tasks[task.Id] = task;
        _channel.Writer.TryWrite(task);
        _logger?.Info($"[TaskQueue] 批量入队: {task.Id} {type} {ids.Count} 个");
        return task.Id;
    }

    /// <summary>批量入队。</summary>
    public IReadOnlyList<string> EnqueueBatch(IEnumerable<string> numbers, VideoTaskType type = VideoTaskType.ScrapeVideo, int priority = 0)
    {
        var ids = new List<string>();
        foreach (var number in numbers)
        {
            var id = Enqueue(number, type, priority: priority);
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>取消指定任务。</summary>
    public bool Cancel(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task) && task.Status is VideoTaskStatus.Pending or VideoTaskStatus.WaitingRetry)
        {
            task.Status = VideoTaskStatus.Cancelled;
            task.CompletedAt = DateTimeOffset.UtcNow;
            _logger?.Info($"[TaskQueue] 取消: {taskId} {task.Number}");
            PublishProgress();
            return true;
        }
        return false;
    }

    /// <summary>取消所有任务。</summary>
    public void CancelAll()
    {
        _globalCts?.Cancel();
        foreach (var task in _tasks.Values.Where(t => t.Status is VideoTaskStatus.Pending or VideoTaskStatus.Running))
        {
            task.Status = VideoTaskStatus.Cancelled;
            task.CompletedAt = DateTimeOffset.UtcNow;
        }
        _logger?.Info("[TaskQueue] 全部取消");
        PublishProgress();
    }

    /// <summary>获取任务状态。</summary>
    public VideoScrapeTask? GetTask(string taskId) =>
        _tasks.TryGetValue(taskId, out var task) ? task : null;

    /// <summary>获取所有任务。</summary>
    public IReadOnlyCollection<VideoScrapeTask> GetAllTasks() => _tasks.Values.ToList();

    /// <summary>开始消费任务队列。processAsync 是每个任务的实际执行逻辑。</summary>
    public Task StartProcessingAsync(Func<VideoScrapeTask, CancellationToken, Task> processAsync, CancellationToken ct)
    {
        _globalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _consumerTask = Task.Run(async () =>
        {
            await foreach (var task in _channel.Reader.ReadAllAsync(_globalCts.Token))
            {
                if (task.Status == VideoTaskStatus.Cancelled) continue;
                if (_globalCts.Token.IsCancellationRequested) break;

                await _concurrencyGate.WaitAsync(_globalCts.Token);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        task.Status = VideoTaskStatus.Running;
                        task.StartedAt = DateTimeOffset.UtcNow;
                        Interlocked.Increment(ref _runningCount);
                        PublishProgress();

                        await processAsync(task, _globalCts.Token);

                        if (task.Status == VideoTaskStatus.Running)
                        {
                            task.Status = VideoTaskStatus.Completed;
                            task.CompletedAt = DateTimeOffset.UtcNow;
                            TaskCompleted?.Invoke(task);
                            _logger?.Info($"[TaskQueue] 完成: {task.Id} {task.Number}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        task.Status = VideoTaskStatus.Cancelled;
                        task.CompletedAt = DateTimeOffset.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        task.Attempt++;
                        task.Error = ex.Message;
                        if (task.Attempt < task.MaxRetries)
                        {
                            task.Status = VideoTaskStatus.WaitingRetry;
                            _logger?.Warn($"[TaskQueue] 重试({task.Attempt}/{task.MaxRetries}): {task.Id} {task.Number} - {ex.Message}");
                            // 延迟后重新入队
                            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, task.Attempt)), _globalCts.Token);
                            task.Status = VideoTaskStatus.Pending;
                            _channel.Writer.TryWrite(task);
                        }
                        else
                        {
                            task.Status = VideoTaskStatus.Failed;
                            task.CompletedAt = DateTimeOffset.UtcNow;
                            TaskFailed?.Invoke(task);
                            _logger?.Error($"[TaskQueue] 失败: {task.Id} {task.Number}", ex);
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _runningCount);
                        _concurrencyGate.Release();
                        PublishProgress();
                    }
                }, _globalCts.Token);
            }
        }, _globalCts.Token);

        return Task.CompletedTask;
    }

    public VideoTaskProgress GetProgress()
    {
        var all = _tasks.Values.ToList();
        var recentLogs = all
            .Where(t => t.CompletedAt.HasValue)
            .OrderByDescending(t => t.CompletedAt)
            .Take(20)
            .Select(t => $"{t.CompletedAt:HH:mm:ss} {t.Number} {t.Status}")
            .ToList();

        return new VideoTaskProgress
        {
            Total = all.Count,
            Completed = all.Count(t => t.Status == VideoTaskStatus.Completed),
            Running = all.Count(t => t.Status == VideoTaskStatus.Running),
            Pending = all.Count(t => t.Status is VideoTaskStatus.Pending or VideoTaskStatus.WaitingRetry),
            Failed = all.Count(t => t.Status == VideoTaskStatus.Failed),
            Skipped = all.Count(t => t.Status == VideoTaskStatus.Skipped),
            RecentLogs = recentLogs
        };
    }

    private void PublishProgress() => ProgressChanged?.Invoke(GetProgress());

    public void Dispose()
    {
        _globalCts?.Cancel();
        _globalCts?.Dispose();
        _concurrencyGate.Dispose();
    }
}