using System.Collections.Concurrent;
using ResourceGrab.Core.Services;

namespace ResourceGrab.App.Services;

/// <summary>
/// 本地小说封面的后台刮削队列：一次只跑一本，慢速滴灌，避免把站点打崩。
/// 只在"当前页的卡片"上排队，翻页/切筛选时清空。
/// </summary>
public sealed class NovelCoverQueue
{
    private const int MaxPending = 240;
    private const int IntervalMs = 380;

    private readonly NovelCoverService _service;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _knownLock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private int _workerStarted;

    public NovelCoverQueue(NovelCoverService service)
    {
        _service = service;
    }

    /// <summary>封面就绪：参数是规范化书名与本地图片路径。在后台线程触发。</summary>
    public event Action<string, string>? CoverReady;

    public void Enqueue(string title)
    {
        var key = NovelCoverService.NormalizeTitle(title);
        if (key.Length == 0) return;

        if (_service.Peek(title) is { } cached)
        {
            CoverReady?.Invoke(key, cached);
            return;
        }

        lock (_knownLock)
        {
            if (_known.Contains(key)) return;
            if (_queue.Count >= MaxPending) return;
            _known.Add(key);
        }
        _queue.Enqueue(title);
        _signal.Release();

        if (Interlocked.Exchange(ref _workerStarted, 1) == 0)
            _ = Task.Run(WorkerAsync);
    }

    /// <summary>换页/换筛选：丢掉还没跑的，正在跑的那本不影响。</summary>
    public void ClearPending()
    {
        while (_queue.TryDequeue(out _)) { }
        lock (_knownLock) _known.Clear();
    }

    private async Task WorkerAsync()
    {
        while (true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);
            if (!_queue.TryDequeue(out var title)) continue;

            try
            {
                var path = await _service.GetCoverAsync(title).ConfigureAwait(false);
                if (path is not null)
                    CoverReady?.Invoke(NovelCoverService.NormalizeTitle(title), path);
            }
            catch { }
            finally
            {
                lock (_knownLock) _known.Remove(NovelCoverService.NormalizeTitle(title));
            }

            try { await Task.Delay(IntervalMs).ConfigureAwait(false); } catch { }
        }
    }
}
