using System.Collections.Concurrent;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Sources.ShortDrama;

namespace ResourceGrab.Core.Services.ShortDrama;

/// <summary>一次入队的统计。</summary>
public readonly record struct DramaEnqueueResult(int Queued, int Skipped, int Failed);

/// <summary>
/// 短剧下载编排：**不自己实现下载**，只做三件事——
/// 1) 组 <see cref="VideoDownloadRequest"/>（每集现取签名 m3u8）；
/// 2) 管落盘位置（一部剧一个文件夹 + poster + drama.json）；
/// 3) 下载完成后回填元数据（订阅任务队列的完成事件）。
/// 真正的 ffmpeg 拉流/进度/重试全部由既有的 <see cref="VideoDownloadService"/> 负责。
/// </summary>
public sealed class ShortDramaDownloadService
{
    private readonly IShortDramaSource _source;
    private readonly VideoScrapeTaskQueue _queue;
    private readonly ShortDramaLibraryService _library;
    private readonly HttpClient _http;
    private readonly Func<ShortDramaSettings> _settings;
    private readonly Func<string?>? _proxyProvider;
    private readonly ILogger? _logger;

    /// <summary>taskId → (剧目录, 集号)：任务完成时按它回填 drama.json。</summary>
    private readonly ConcurrentDictionary<string, (string Folder, int Number)> _pending = new();

    public ShortDramaDownloadService(
        IShortDramaSource source,
        VideoScrapeTaskQueue queue,
        ShortDramaLibraryService library,
        HttpClient http,
        Func<ShortDramaSettings> settings,
        Func<string?>? proxyProvider = null,
        ILogger? logger = null)
    {
        _source = source;
        _queue = queue;
        _library = library;
        _http = http;
        _settings = settings;
        _proxyProvider = proxyProvider;
        _logger = logger;
        // 只订阅 TaskCompleted 会漏两类任务：执行器显式置 Skipped（队列不发完成事件）、以及失败任务。
        // 前者由入队前的"文件已存在"预检兜住，后者必须订阅，否则 _pending 会一直留着。
        _queue.TaskCompleted += OnTaskCompleted;
        _queue.TaskFailed += OnTaskFailed;
    }

    /// <summary>当前设置的下载根目录。</summary>
    public string RootDir => _library.RootDir;

    /// <summary>正在进行的下载任务（本地页「下载中」区块用）。</summary>
    public IReadOnlyList<VideoScrapeTask> ActiveDownloads()
        => _queue.GetAllTasks()
            .Where(t => t.Type == VideoTaskType.DownloadVideo && IsUnderRoot(t.DownloadOutputDir))
            .ToList();

    /// <summary>下载全剧（只补缺失集）。返回入队/跳过/失败计数。</summary>
    public async Task<DramaEnqueueResult> EnqueueDramaAsync(
        ShortDramaDetail detail, IReadOnlyCollection<int>? onlyEpisodes = null, CancellationToken ct = default)
    {
        if (detail.Episodes.Count == 0) return new DramaEnqueueResult(0, 0, 0);

        var folder = _library.EnsureFolder(detail);
        // 元数据先落盘：即使后面全部下载失败，本地页也能显示"这部剧有 N 集待下"
        var meta = _library.MergeDetail(detail, folder);
        _library.Save(folder, meta);

        _ = DownloadCoverAsync(folder, detail.CoverUrl, ct);

        var targets = onlyEpisodes is { Count: > 0 }
            ? detail.Episodes.Where(e => onlyEpisodes.Contains(e.Number))
            : detail.Episodes;

        var queued = 0;
        var skipped = 0;
        var failed = 0;
        var concurrency = Math.Clamp(_settings().DownloadConcurrency, 1, 4);
        var gate = new SemaphoreSlim(concurrency);

        var tasks = new List<Task>();
        foreach (var ep in targets.OrderBy(e => e.Number))
        {
            // 已下载且文件还在 → 跳过（不联网、不占队列）
            if (LocalEpisodeFile(folder, meta, ep.Number) is not null)
            {
                skipped++;
                continue;
            }

            await gate.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (await EnqueueEpisodeCoreAsync(detail, folder, ep.Number, ct).ConfigureAwait(false))
                        Interlocked.Increment(ref queued);
                    else
                        Interlocked.Increment(ref skipped);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _logger?.Warn($"[ShortDramaDownload] 第 {ep.Number} 集入队失败：{ex.Message}");
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        finally { gate.Dispose(); }

        _logger?.Info($"[ShortDramaDownload] 「{detail.Title}」入队 {queued} 集，跳过 {skipped}，失败 {failed}");
        return new DramaEnqueueResult(queued, skipped, failed);
    }

    /// <summary>下载单集。</summary>
    public async Task<DramaEnqueueResult> EnqueueEpisodeAsync(
        ShortDramaDetail detail, int number, CancellationToken ct = default)
    {
        var folder = _library.EnsureFolder(detail);
        var meta = _library.MergeDetail(detail, folder);
        _library.Save(folder, meta);
        _ = DownloadCoverAsync(folder, detail.CoverUrl, ct);

        var queued = await EnqueueEpisodeCoreAsync(detail, folder, number, ct).ConfigureAwait(false);
        return queued ? new DramaEnqueueResult(1, 0, 0) : new DramaEnqueueResult(0, 1, 0);
    }

    private async Task<bool> EnqueueEpisodeCoreAsync(
        ShortDramaDetail detail, string folder, int number, CancellationToken ct)
    {
        // 取流必须实时：签名约 2 小时过期，用缓存地址会直接 403
        var frame = await _source.GetPlayFrameAsync(detail.Id, number, ct).ConfigureAwait(false);
        if (frame is null || string.IsNullOrWhiteSpace(frame.StreamUrl))
            throw new InvalidOperationException($"第 {number} 集取流失败（播放帧无地址）");

        var durationText = string.IsNullOrWhiteSpace(frame.DurationText)
            ? FormatDuration(detail.DurationSeconds)
            : frame.DurationText;

        var request = new VideoDownloadRequest(
            Number: detail.Title,
            Title: $"第{number:D2}集",
            StreamUrl: frame.StreamUrl,
            Referer: "",                     // 实测该 CDN 不校验 Referer
            Proxy: _proxyProvider?.Invoke(), // 与刮削/在线播放共用同一个代理设置
            DurationText: durationText,
            OutputDirectory: folder,
                Category: "shortdrama");

        var taskId = _queue.EnqueueDownload(request, priority: 1);
        _pending[taskId] = (folder, number);
        _logger?.Info($"[ShortDramaDownload] 入队「{detail.Title}」第 {number} 集 task={taskId}");
        return true;
    }

    private void OnTaskCompleted(VideoScrapeTask task)
    {
        if (!_pending.TryRemove(task.Id, out var target)) return;
        try
        {
            var size = 0L;
            if (task.DownloadOutputPath is { Length: > 0 } path && File.Exists(path))
                size = new FileInfo(path).Length;

            if (task.Status == VideoTaskStatus.Failed)
            {
                _logger?.Warn($"[ShortDramaDownload] 第 {target.Number} 集下载失败：{task.Error}");
                return;
            }

            _library.RecordEpisodeDone(task.DownloadOutputPath, target.Number, size);
            _logger?.Info($"[ShortDramaDownload] 第 {target.Number} 集完成 {size / 1024 / 1024} MB");
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDramaDownload] 回填失败：{ex.Message}");
        }
    }

    private void OnTaskFailed(VideoScrapeTask task)
    {
        if (!_pending.TryRemove(task.Id, out var target)) return;
        _logger?.Warn($"[ShortDramaDownload] 第 {target.Number} 集下载失败（{task.Attempt} 次）：{task.Error}");
    }

    private static string? LocalEpisodeFile(string folder, DramaMetadata meta, int number)    {
        var ep = meta.Episodes.FirstOrDefault(e => e.Number == number);
        if (ep?.File is not { Length: > 0 } file) return null;
        var path = Path.Combine(folder, file);
        return File.Exists(path) ? path : null;
    }

    /// <summary>封面只抓一次：签名会过期，本地库必须有一份自己的。</summary>
    private async Task DownloadCoverAsync(string folder, string coverUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(coverUrl)) return;
        var path = Path.Combine(folder, "poster.jpg");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, coverUrl);
            // 同 XiguaDramaHttpClient：客户端已有默认 UA 时不能再加一遍（重复头会被 CDN 拦）
            if (!_http.DefaultRequestHeaders.UserAgent.Any())
                request.Headers.TryAddWithoutValidation("User-Agent", XiguaDramaConstants.UserAgent);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var raw = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (raw.Length < 512) return;        // 太小更像错误页

            // CDN 返回的是加密字节（AES-128-CBC，密钥在站点 crypto-worker.js 里），先解密再落盘，
            // 否则存下来的 poster.jpg 是打不开的密文。
            var bytes = DramaCoverCipher.TryDecrypt(raw);
            if (bytes is null)
            {
                _logger?.Warn("[ShortDramaDownload] 封面解密失败，跳过保存（不影响播放）");
                return;
            }
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDramaDownload] 封面下载失败（不影响播放）：{ex.Message}");
        }
    }

    private static string FormatDuration(int seconds)        => seconds <= 0 ? "" : $"{seconds / 60}:{seconds % 60:D2}";

    private bool IsUnderRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var root = Path.GetFullPath(RootDir).TrimEnd(Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            return target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
