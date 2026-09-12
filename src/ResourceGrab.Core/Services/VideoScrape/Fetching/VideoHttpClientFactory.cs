using System.Collections.Concurrent;
using System.Net;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape.Fetching;

// ---------------------------------------------------------------------------
// 阶段0: IVideoHttpClientFactory 的默认实现（接口在 VideoScrape/IVideoHttpClientFactory.cs）。
// per-source 客户端：代理取 VideoSourceConfig.ProxyOverride，缺省回退全局 VideoScraping.Proxy；
// UA/语言/自动解压/超时统一，与老 VideoScrapeHttpClient 保持同族配置。
// ---------------------------------------------------------------------------

public sealed class VideoHttpClientFactory : IVideoHttpClientFactory
{
    /// <summary>视频链路统一 UA（单一来源见 <see cref="VideoConstants.UserAgent"/>）。</summary>
    public const string DefaultUserAgent = VideoConstants.UserAgent;

    private readonly VideoScrapeAdvancedSettings _advanced;
    private readonly VideoScrapeSettings _settings;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public VideoHttpClientFactory(VideoScrapeAdvancedSettings advanced, VideoScrapeSettings settings, ILogger? logger = null)
    {
        _advanced = advanced;
        _settings = settings;
        _logger = logger;
    }

    public HttpClient Create(string sourceId)
    {
        return _clients.GetOrAdd(string.IsNullOrEmpty(sourceId) ? "default" : sourceId, id =>
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                // 刮削目标站点证书链常缺中间证书，无法逐站安装，故放宽校验（仅刮削流量）。
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            var proxy = ResolveProxy(id);
            if (!string.IsNullOrWhiteSpace(proxy))
            {
                handler.Proxy = new WebProxy(proxy);
                _logger?.Info($"[HttpClientFactory] {id} 走代理: {proxy}");
            }
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Clamp(_settings.TimeoutSeconds, 5, 60)) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.AcceptLanguage.TryParseAdd("zh-CN,zh;q=0.9,en-US;q=0.8,ja;q=0.7");
            return client;
        });
    }

    /// <summary>解析某源生效的代理：VideoSourceConfig.ProxyOverride 优先，否则全局 VideoScraping.Proxy。</summary>
    public string? ResolveProxy(string sourceId)
    {
        var cfg = _advanced.SourceConfigs.TryGetValue(sourceId, out var sourceConfig) ? sourceConfig : null;
        if (!string.IsNullOrWhiteSpace(cfg?.ProxyOverride)) return cfg!.ProxyOverride;
        return string.IsNullOrWhiteSpace(_settings.Proxy) ? null : _settings.Proxy;
    }
}
