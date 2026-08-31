using ResourceGrab.Core.Services.VideoScrape;
using Xunit;

namespace ResourceGrab.Core.Tests.Services.VideoScrape;

// M5: 任务队列测试

public class TaskQueueTests
{
    [Fact]
    public void Enqueue_AddsTask_ReturnsId()
    {
        using var queue = new VideoScrapeTaskQueue();

        var id = queue.Enqueue("SSIS-405");

        Assert.NotEmpty(id);
        var task = queue.GetTask(id);
        Assert.NotNull(task);
        Assert.Equal("SSIS-405", task!.Number);
        Assert.Equal(VideoTaskType.ScrapeVideo, task.Type);
    }

    [Fact]
    public void EnqueueBatch_AddsMultipleTasks()
    {
        using var queue = new VideoScrapeTaskQueue();

        var ids = queue.EnqueueBatch(["SSIS-405", "SSIS-406", "SSIS-407"]);

        Assert.Equal(3, ids.Count);
        var progress = queue.GetProgress();
        Assert.Equal(3, progress.Total);
        Assert.Equal(3, progress.Pending);
    }

    [Fact]
    public void Cancel_PendingTask_SetsCancelled()
    {
        using var queue = new VideoScrapeTaskQueue();
        var id = queue.Enqueue("SSIS-405");

        var result = queue.Cancel(id);

        Assert.True(result);
        Assert.Equal(VideoTaskStatus.Cancelled, queue.GetTask(id)!.Status);
    }

    [Fact]
    public void Cancel_ReturnsFalse_ForUnknownTask()
    {
        using var queue = new VideoScrapeTaskQueue();

        Assert.False(queue.Cancel("nonexistent"));
    }

    [Fact]
    public void GetProgress_TracksCounts()
    {
        using var queue = new VideoScrapeTaskQueue();
        queue.Enqueue("SSIS-405");
        queue.Enqueue("SSIS-406");
        var id = queue.Enqueue("SSIS-407");
        queue.Cancel(id);

        var progress = queue.GetProgress();

        Assert.Equal(3, progress.Total);
        Assert.Equal(2, progress.Pending);
    }

    [Fact]
    public async Task StartProcessingAsync_ExecutesTasks()
    {
        using var queue = new VideoScrapeTaskQueue(maxConcurrency: 2);
        var processed = new List<string>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        queue.Enqueue("SSIS-405");
        queue.Enqueue("SSIS-406");

        await queue.StartProcessingAsync((task, ct) =>
        {
            lock (processed) processed.Add(task.Number);
            return Task.CompletedTask;
        }, cts.Token);

        // 等待处理完成
        await Task.Delay(500, cts.Token);
        queue.CancelAll();

        Assert.Equal(2, processed.Count);
    }

    [Fact]
    public void GetAllTasks_ReturnsAll()
    {
        using var queue = new VideoScrapeTaskQueue();
        queue.Enqueue("SSIS-405");
        queue.Enqueue("SSIS-406");

        var all = queue.GetAllTasks();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void EnqueueBatchTask_SetsTotalAndItemIds()
    {
        using var queue = new VideoScrapeTaskQueue();

        var id = queue.EnqueueBatchTask(["SSIS-405", "SSIS-406", "SSIS-407"], title: "批量");
        var task = queue.GetTask(id);

        Assert.NotNull(task);
        Assert.Equal(3, task!.Total);
        Assert.Equal(["SSIS-405", "SSIS-406", "SSIS-407"], task.ItemIds);
    }

    [Fact]
    public async Task Cancel_RunningTask_InterruptsExecution()
    {
        using var queue = new VideoScrapeTaskQueue(maxConcurrency: 1);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        queue.Enqueue("SSIS-405");

        await queue.StartProcessingAsync(async (task, ct) => await Task.Delay(10_000, ct), cts.Token);
        await Task.Delay(200, cts.Token);
        var id = queue.GetAllTasks().Single().Id;

        Assert.True(queue.Cancel(id));
        await Task.Delay(200, cts.Token);
        Assert.Equal(VideoTaskStatus.Cancelled, queue.GetTask(id)!.Status);
    }
}
