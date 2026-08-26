using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using ResourceGrab.Core.Downloading;
using ResourceGrab.Core.Sources;

namespace ResourceGrab.App.Services;

/// <summary>
/// 在线阅读数据服务：按章节拉取图片页列表、抓取并重组图片字节。
/// 与下载管线共用同一数据链路（IComicSource.GetChapterImagesAsync），
/// 差异在只做内存缓存（LRU）不落盘；限流按源隔离。
/// </summary>
public class OnlineReaderService
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http = new() { Timeout = HttpTimeout };
    private readonly ConcurrentDictionary<string, SourceGates> _gates = new();
    private readonly ByteLruCache _cache = new();

    /// <summary>按源隔离的并发闸门：章节图片列表获取 / 图片下载（阅读场景图片并发上限 2，降低风控风险）。</summary>
    private sealed class SourceGates
    {
        public required SemaphoreSlim Urls { get; init; }
        public required SemaphoreSlim Images { get; init; }
    }

    private SourceGates GetGates(IComicSource source)
        => _gates.GetOrAdd(source.Info.Id, _ => new SourceGates
        {
            Urls = new SemaphoreSlim(Math.Max(1, source.Info.MaxUrlFetchConcurrency)),
            Images = new SemaphoreSlim(Math.Clamp(source.Info.MaxImageConcurrency, 1, 2)),
        });

    /// <summary>获取某章节的图片页列表（含请求头与分块数）。</summary>
    public async Task<IReadOnlyList<ImagePage>> GetChapterPagesAsync(
        IComicSource source, Chapter chapter, CancellationToken ct = default)
    {
        var gates = GetGates(source);
        await gates.Urls.WaitAsync(ct);
        try
        {
            return await source.GetChapterImagesAsync(chapter, ct);
        }
        finally
        {
            gates.Urls.Release();
        }
    }

    /// <summary>
    /// 抓取一张图片的完整字节（已按 BlockNum 重组）。命中缓存直接返回；
    /// 网络类错误重试 3 次，风控/权限类错误（429/403/401）直接失败不重试，避免放大请求量。
    /// </summary>
    public async Task<byte[]> GetImageBytesAsync(
        IComicSource source, ImagePage page, CancellationToken ct = default)
    {
        var key = page.Url;
        if (_cache.TryGet(key, out var cached))
        {
            return cached;
        }

        var gates = GetGates(source);
        await gates.Images.WaitAsync(ct);
        try
        {
            // 双检锁：排队期间可能已被其他请求写入
            if (_cache.TryGet(key, out cached))
            {
                return cached;
            }
            var raw = await FetchWithRetryAsync(page, ct);
            var bytes = page.BlockNum == 0
                ? raw
                : await Task.Run(() => ImageReassembler.Reassemble(raw, page.BlockNum), ct);
            _cache.Set(key, bytes);
            return bytes;
        }
        finally
        {
            gates.Images.Release();
        }
    }

    private async Task<byte[]> FetchWithRetryAsync(ImagePage page, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, page.Url);
                foreach (var (key, value) in page.Headers)
                {
                    request.Headers.TryAddWithoutValidation(key, value);
                }
                using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode != HttpStatusCode.OK)
                {
                    // 风控/权限类状态码不重试，避免失败重试放大请求量
                    if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    {
                        throw new HttpRequestException(
                            $"图片请求被拒绝（{resp.StatusCode}），可能触发了站点风控，请稍后再试");
                    }
                    var text = await resp.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException($"图片下载失败，状态码 {resp.StatusCode}: {text}");
                }
                return await resp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex) when (attempt < 2 && IsTransient(ex))
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        throw last ?? new HttpRequestException($"图片下载失败: {page.Url}");
    }

    /// <summary>网络类错误才重试；风控/权限类错误直接失败，避免雪崩。</summary>
    private static bool IsTransient(Exception ex)
        => ex is not HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized };

    /// <summary>按总字节数上限的 LRU 缓存（默认 256MB），回看已加载页面时直接命中、不重复请求。</summary>
    private sealed class ByteLruCache
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Bytes)>> _map = new();
        private readonly LinkedList<(string Key, byte[] Bytes)> _order = new();
        private readonly long _maxBytes;
        private long _totalBytes;

        public ByteLruCache(long maxBytes = 256L * 1024 * 1024)
        {
            _maxBytes = maxBytes;
        }

        public bool TryGet(string key, out byte[] bytes)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _order.Remove(node);
                    _order.AddLast(node);
                    bytes = node.Value.Bytes;
                    return true;
                }
                bytes = Array.Empty<byte>();
                return false;
            }
        }

        public void Set(string key, byte[] bytes)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    _totalBytes -= existing.Value.Bytes.Length;
                    _order.Remove(existing);
                    _map.Remove(key);
                }
                var node = _order.AddLast((key, bytes));
                _map[key] = node;
                _totalBytes += bytes.Length;
                while (_totalBytes > _maxBytes && _order.Count > 1)
                {
                    var oldest = _order.First!.Value;
                    _order.RemoveFirst();
                    _map.Remove(oldest.Key);
                    _totalBytes -= oldest.Bytes.Length;
                }
            }
        }
    }
}