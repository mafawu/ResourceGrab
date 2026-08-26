using System.Collections.Concurrent;
using System.Threading.Channels;
using ResourceGrab.Core.Http;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Sources.Jm;

namespace ResourceGrab.Core.Downloading;

/// <summary>
/// 多线程下载引擎（对应原 Rust 实现 download_manager.rs）。
/// 站点差异已收敛到 IComicSource：章节图片列表（URL / headers / 分块数）由各源提供。
/// 并发限流按源区分：每个源从 <see cref="ComicSourceInfo"/> 读取各自的图片/章节/图片 URL 获取并发上限，
/// 限流配置不同的源互不影响；章节通过 <see cref="Chapter.SourceId"/> 解析所属源。
/// </summary>
public class DownloadManager : IDisposable
{
    private readonly IReadOnlyDictionary<string, IComicSource> _sources;
    private readonly ConfigService _configService;
    private readonly HttpClient _imageClient;

    private readonly Channel<Chapter> _channel;
    private readonly ConcurrentDictionary<string, SourceThrottle> _throttles = new();

    private long _bytePerSec;
    private long _downloadedImageCount;
    private long _totalImageCount;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiverTask;
    private readonly ConcurrentDictionary<string, long> _eventChapterIds = new();
    private long _nextEventChapterId;

    public event EventHandler<ChapterPendingEventArgs>? ChapterPending;
    public event EventHandler<ChapterStartEventArgs>? ChapterStart;
    public event EventHandler<ImageSuccessEventArgs>? ImageSuccess;
    public event EventHandler<ImageErrorEventArgs>? ImageError;
    public event EventHandler<ChapterEndEventArgs>? ChapterEnd;
    public event EventHandler<OverallProgressEventArgs>? OverallProgress;
    public event EventHandler<SpeedEventArgs>? SpeedChanged;

    /// <summary>通用构造：按章节 SourceId 解析内容源，并发上限取各源自身配置。</summary>
    public DownloadManager(IEnumerable<IComicSource> sources, ConfigService configService)
        : this(sources, configService, CreateImageClient())
    {
    }

    /// <summary>单源构造：下载全部走同一源。</summary>
    public DownloadManager(IComicSource source, ConfigService configService)
        : this(new[] { source }, configService)
    {
    }

    /// <summary>测试用构造：注入图片下载 HttpClient（与 API 客户端共用 FakeHandler）。</summary>
    internal DownloadManager(JmHttpClient jmClient, ConfigService configService, HttpClient imageClient)
        : this(new IComicSource[] { new JmSource(jmClient) }, configService, imageClient)
    {
    }

    /// <summary>测试用构造：注入任意单源与图片下载 HttpClient。</summary>
    internal DownloadManager(IComicSource source, ConfigService configService, HttpClient imageClient)
        : this(new[] { source }, configService, imageClient)
    {
    }

    /// <summary>测试用构造：注入多源与图片下载 HttpClient。</summary>
    internal DownloadManager(IEnumerable<IComicSource> sources, ConfigService configService, HttpClient imageClient)
    {
        _sources = sources.ToDictionary(s => s.Info.Id, StringComparer.OrdinalIgnoreCase);
        _configService = configService;
        _imageClient = imageClient;

        _channel = Channel.CreateBounded<Chapter>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        _receiverTask = ReceiverLoopAsync(_cts.Token);
        _ = SpeedLoopAsync(_cts.Token);
    }

    private static HttpClient CreateImageClient() => new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>提交一个章节的下载任务（通用模型）。</summary>
    public async Task SubmitChapterAsync(Chapter chapter, CancellationToken ct = default)
    {
        await _channel.Writer.WriteAsync(chapter, ct);
    }

    /// <summary>提交一个章节的下载任务（禁漫旧模型兼容重载）。</summary>
    public async Task SubmitChapterAsync(ChapterInfo chapterInfo, CancellationToken ct = default)
    {
        await SubmitChapterAsync(ToChapter(chapterInfo), ct);
    }

    private async Task ReceiverLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chapter in _channel.Reader.ReadAllAsync(ct))
            {
                _ = ProcessChapterAsync(chapter, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 应用退出
        }
    }

    /// <summary>每秒上报一次下载速度。</summary>
    private async Task SpeedLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                var bytePerSec = Interlocked.Exchange(ref _bytePerSec, 0);
                var megaBytePerSec = bytePerSec / 1024.0 / 1024.0;
                SpeedChanged?.Invoke(this, new SpeedEventArgs($"{megaBytePerSec:F2}MB/s"));
            }
        }
        catch (OperationCanceledException)
        {
            // 应用退出
        }
    }

    /// <summary>按章节所属源解析 IComicSource；未知 id 回退到第一个源。</summary>
    private IComicSource GetSource(Chapter chapter)
        => _sources.TryGetValue(chapter.SourceId, out var source) ? source : _sources.Values.First();

    /// <summary>取（或懒创建）指定源的并发闸门，上限取自源的 ComicSourceInfo。</summary>
    private SourceThrottle GetThrottle(IComicSource source)
        => _throttles.GetOrAdd(source.Info.Id, _ => new SourceThrottle
        {
            Urls = new SemaphoreSlim(source.Info.MaxUrlFetchConcurrency),
            Chapters = new SemaphoreSlim(source.Info.MaxChapterConcurrency),
            Images = new SemaphoreSlim(source.Info.MaxImageConcurrency),
        });

    private async Task ProcessChapterAsync(Chapter chapter, CancellationToken ct)
    {
        var source = GetSource(chapter);
        var throttle = GetThrottle(source);
        // 非数字章节 ID（如拷贝漫画 UUID）无法直接作为事件 ID；
        // 用运行期稳定编号避免多章节并发时 UI 进度互相覆盖。
        var chapterId = _eventChapterIds.GetOrAdd(
            $"{chapter.SourceId}|{chapter.Id}",
            _ => Interlocked.Increment(ref _nextEventChapterId));
        var tempDownloadDir = GetTempDownloadDir(chapter);
        var downloadedCount = 0;

        try
        {
            ChapterPending?.Invoke(this, new ChapterPendingEventArgs(
                chapterId, chapter.Title, chapter.ComicTitle));

            var downloadFormat = _configService.Current.DownloadFormat;
            var ext = downloadFormat.Extension();

            // 获取此章节每张图片的下载链接（含请求头与分块数）
            var pages = await GetChapterPagesAsync(source, throttle, chapter, ct);

            // 总共需要下载的图片数量
            var total = pages.Count;
            Interlocked.Add(ref _totalImageCount, total);

            // 断点续传：正式目录已存在且包含全部图片 → 整章跳过，无需重新下载
            var parentDir = Path.Combine(_configService.Current.DownloadDir, chapter.ComicTitle);
            var finalDir = Path.Combine(parentDir, chapter.Title);
            if (IsChapterComplete(finalDir, total, ext))
            {
                ChapterStart?.Invoke(this, new ChapterStartEventArgs(chapterId, total));
                Interlocked.Add(ref _downloadedImageCount, total);
                ChapterEnd?.Invoke(this, new ChapterEndEventArgs(chapterId, null));
                return;
            }

            Directory.CreateDirectory(tempDownloadDir);
            // 清理临时目录中不属于本次预期文件名集合的文件（旧格式后缀 / 残留 .tmp / 页数变化后的多余文件）
            CleanupTempDir(tempDownloadDir, total, ext);

            // 限制同时下载的章节数量（按源）
            await throttle.Chapters.WaitAsync(ct);
            try
            {
                ChapterStart?.Invoke(this, new ChapterStartEventArgs(chapterId, total));

                var tasks = new List<Task>();
                for (var i = 0; i < pages.Count; i++)
                {
                    var page = pages[i];
                    var savePath = Path.Combine(tempDownloadDir, $"{i + 1:D3}.{ext}");
                    var task = DownloadImageAsync(
                        chapterId, throttle, page, savePath, downloadFormat,
                        () => Interlocked.Increment(ref downloadedCount), ct);
                    tasks.Add(task);
                }
                await Task.WhenAll(tasks);
            }
            finally
            {
                throttle.Chapters.Release();
            }

            // 如果所有图片全部已处理（无论成功或失败），则清空全局进度计数
            var currentDownloaded = Interlocked.Read(ref _downloadedImageCount);
            var currentTotal = Interlocked.Read(ref _totalImageCount);
            if (currentDownloaded == currentTotal)
            {
                Interlocked.Exchange(ref _downloadedImageCount, 0);
                Interlocked.Exchange(ref _totalImageCount, 0);
            }

            // 检查此章节的图片是否全部下载成功
            if (downloadedCount == total)
            {
                // 下载成功：正式目录已存在（如之前不完整）则合并文件，否则直接改名
                Directory.CreateDirectory(parentDir);
                if (Directory.Exists(finalDir))
                {
                    MergeTempIntoFinal(tempDownloadDir, finalDir);
                }
                else
                {
                    Directory.Move(tempDownloadDir, finalDir);
                }
                ChapterEnd?.Invoke(this, new ChapterEndEventArgs(chapterId, null));
            }
            else
            {
                var errMsg = $"`{chapter.Title}`总共有`{total}`张图片，但只下载了`{downloadedCount}`张";
                ChapterEnd?.Invoke(this, new ChapterEndEventArgs(chapterId, errMsg));
            }
        }
        catch (Exception ex)
        {
            ChapterEnd?.Invoke(this, new ChapterEndEventArgs(chapterId, ex.Message));
        }
    }

    private static Chapter ToChapter(ChapterInfo chapterInfo) => new()
    {
        Id = chapterInfo.ChapterId.ToString(),
        NumericId = chapterInfo.ChapterId,
        Title = chapterInfo.ChapterTitle,
        ComicId = chapterInfo.AlbumId.ToString(),
        ComicTitle = chapterInfo.AlbumTitle,
        SourceId = "jm",
    };

    private async Task DownloadImageAsync(
        long chapterId, SourceThrottle throttle, ImagePage page, string savePath, DownloadFormat downloadFormat,
        Func<int> onSuccess, CancellationToken ct)
    {
        // 断点续传：目标文件已存在且非空（原子写入保证完整）→ 跳过下载，直接计为成功
        if (IsFileComplete(savePath))
        {
            var count = onSuccess();
            ImageSuccess?.Invoke(this, new ImageSuccessEventArgs(chapterId, savePath, count));
            ReportProgress();
            return;
        }

        // 限制同时下载的图片数量（按源）
        await throttle.Images.WaitAsync(ct);
        byte[] imageData;
        try
        {
            imageData = await GetImageBytesAsync(page, ct);
        }
        catch (Exception ex)
        {
            ImageError?.Invoke(this, new ImageErrorEventArgs(chapterId, page.Url, ex.Message));
            return;
        }
        finally
        {
            throttle.Images.Release();
        }

        // 保存图片（图片拼接是 CPU 密集型操作，放到线程池执行，避免阻塞）
        try
        {
            await Task.Run(() =>
            {
                ImageReassembler.SaveImage(savePath, downloadFormat, page.BlockNum, imageData);
                // 记录下载字节数（仅统计成功保存的图片）
                Interlocked.Add(ref _bytePerSec, imageData.Length);
                var count = onSuccess();
                ImageSuccess?.Invoke(this, new ImageSuccessEventArgs(chapterId, savePath, count));
            }, ct);
        }
        catch (Exception ex)
        {
            ImageError?.Invoke(this, new ImageErrorEventArgs(chapterId, page.Url, ex.Message));
        }
        finally
        {
            ReportProgress();
        }

        return;

        // 每处理完一张图（无论成败 / 是否跳过）更新一次全局进度
        void ReportProgress()
        {
            var downloaded = Interlocked.Increment(ref _downloadedImageCount);
            var total = Interlocked.Read(ref _totalImageCount);
            var percentage = total == 0 ? 0 : downloaded * 100.0 / total;
            OverallProgress?.Invoke(this, new OverallProgressEventArgs(downloaded, total, percentage));
        }
    }

    /// <summary>从内容源获取章节图片页列表（限制同一源同时获取的数量）。</summary>
    private async Task<IReadOnlyList<ImagePage>> GetChapterPagesAsync(
        IComicSource source, SourceThrottle throttle, Chapter chapter, CancellationToken ct)
    {
        await throttle.Urls.WaitAsync(ct);
        try
        {
            return await source.GetChapterImagesAsync(chapter, ct);
        }
        finally
        {
            throttle.Urls.Release();
        }
    }

    private async Task<byte[]> GetImageBytesAsync(ImagePage page, CancellationToken ct)
    {
        var lastException = (Exception?)null;
        // 简单重试 3 次
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, page.Url);
                foreach (var (key, value) in page.Headers)
                {
                    request.Headers.TryAddWithoutValidation(key, value);
                }
                using var httpResp = await _imageClient.SendAsync(request, ct);
                if (httpResp.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    var text = await httpResp.Content.ReadAsStringAsync(ct);
                    throw new JmException($"下载图片`{page.Url}`失败，预料之外的状态码: {text}");
                }
                return await httpResp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex) when (attempt < 2)
            {
                lastException = ex;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        throw lastException ?? new JmException($"下载图片`{page.Url}`失败");
    }

    private string GetTempDownloadDir(Chapter chapter)
    {
        // 以 `.下载中-` 开头，表示是临时目录（与原版一致）
        return Path.Combine(
            _configService.Current.DownloadDir,
            chapter.ComicTitle,
            $".下载中-{chapter.Title}");
    }

    /// <summary>断点续传判断：文件已存在且非空（原子写入保证完整）。</summary>
    internal static bool IsFileComplete(string path)
        => File.Exists(path) && new FileInfo(path).Length > 0;

    /// <summary>章节是否已完整下载：目录存在且包含全部 001..N 的图片文件。</summary>
    internal static bool IsChapterComplete(string dir, int imageCount, string ext)
    {
        if (imageCount <= 0 || !Directory.Exists(dir))
        {
            return false;
        }
        for (var i = 1; i <= imageCount; i++)
        {
            if (!IsFileComplete(Path.Combine(dir, $"{i:D3}.{ext}")))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>清理临时目录中不属于本次预期文件名集合的文件（旧格式 / 残留 .tmp / 页数变化）。</summary>
    private static void CleanupTempDir(string tempDownloadDir, int total, string ext)
    {
        var expected = new HashSet<string>(total, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < total; i++)
        {
            expected.Add($"{i + 1:D3}.{ext}");
        }
        foreach (var file in Directory.EnumerateFiles(tempDownloadDir))
        {
            if (!expected.Contains(Path.GetFileName(file)))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // 清理失败不影响下载
                }
            }
        }
    }

    /// <summary>把临时目录中的文件合并进已存在的正式目录，然后删除临时目录。</summary>
    private static void MergeTempIntoFinal(string tempDownloadDir, string finalDir)
    {
        foreach (var file in Directory.EnumerateFiles(tempDownloadDir))
        {
            File.Move(file, Path.Combine(finalDir, Path.GetFileName(file)), true);
        }
        Directory.Delete(tempDownloadDir, true);
    }

    /// <summary>按源隔离的并发闸门（图片 URL 获取 / 章节 / 图片下载）。</summary>
    private sealed class SourceThrottle : IDisposable
    {
        public required SemaphoreSlim Urls { get; init; }
        public required SemaphoreSlim Chapters { get; init; }
        public required SemaphoreSlim Images { get; init; }

        public void Dispose()
        {
            Urls.Dispose();
            Chapters.Dispose();
            Images.Dispose();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        try
        {
            _receiverTask.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // 忽略退出时的任务异常
        }
        _cts.Dispose();
        foreach (var throttle in _throttles.Values)
        {
            throttle.Dispose();
        }
        _throttles.Clear();
        _imageClient.Dispose();
    }
}
