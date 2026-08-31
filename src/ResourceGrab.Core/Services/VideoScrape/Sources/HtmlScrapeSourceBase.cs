using System.Net;
using System.Text.RegularExpressions;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape.Fetching;

namespace ResourceGrab.Core.Services.VideoScrape.Sources;

// ---------------------------------------------------------------------------
// 阶段2: HTML 刮削源公共基类。
// 统一封装：异常 → VideoSourceOutcome 翻译、计时、经 MultiTierFetcher 的抓取
// （反爬降级链 + Cookie 罐 + per-source 限速）、番号精确匹配校验。
// 各站点源只需实现 FetchCoreAsync（搜索 + 解析），产出 VideoScrapeMetadata。
// 异常 → Outcome 契约见 docs/多源刮削与反爬改造实施方案.md 第 2.3 节。
// ---------------------------------------------------------------------------

public abstract class HtmlScrapeSourceBase : IVideoScrapeSource
{
    protected readonly IResilientFetcher Fetcher;
    protected readonly ILogger? Logger;

    protected HtmlScrapeSourceBase(IResilientFetcher fetcher, ILogger? logger = null)
    {
        Fetcher = fetcher;
        Logger = logger;
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract IReadOnlySet<VideoContentKind> SupportedKinds { get; }

    /// <summary>站点特定抓取：搜索 + 详情解析；未命中返回 null；异常统一由基类翻译为 Outcome。</summary>
    protected abstract Task<VideoScrapeMetadata?> FetchCoreAsync(VideoSourceRequest request, CancellationToken ct);

    public async ValueTask<VideoSourceFetchResult> FetchAsync(VideoSourceRequest request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var metadata = await FetchCoreAsync(request, ct);
            sw.Stop();
            return metadata is null
                ? VideoSourceFetchResult.NoMatch(Id, (int)sw.ElapsedMilliseconds)
                : VideoSourceFetchResult.Success(Id, metadata, (int)sw.ElapsedMilliseconds, request.Language);
        }
        catch (OperationCanceledException)
        {
            return VideoSourceFetchResult.Failure(Id, VideoSourceOutcome.Cancelled, "Cancelled", (int)sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex) when (BlockDetector.IsBlocked((int?)ex.StatusCode, null))
        {
            return VideoSourceFetchResult.Failure(Id, VideoSourceOutcome.Blocked,
                $"HTTP {(int?)ex.StatusCode}", (int)sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            return VideoSourceFetchResult.Failure(Id, VideoSourceOutcome.HttpError, ex.Message, (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return VideoSourceFetchResult.Failure(Id, VideoSourceOutcome.ParseError, ex.Message, (int)sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// GET 页面：经多级抓取栈（反爬降级链 + Cookie 罐 + per-source 限速）。
    /// 返回 2xx 的 body；4xx/5xx 抛 HttpRequestException（带 StatusCode，基类翻译为 HttpError/Blocked）。
    /// </summary>
    protected async Task<string> GetHtmlAsync(string url, string? referer = null, CancellationToken ct = default)
    {
        var result = await Fetcher.GetAsync(url, new FetchOptions { SourceId = Id, Referer = referer }, ct);
        if (BlockDetector.IsBlocked(result.StatusCode, result.Body))
            throw new HttpRequestException("被反爬拦截", null, HttpStatusCode.Forbidden);
        if (result.StatusCode is < 200 or >= 400)
            throw new HttpRequestException($"HTTP {result.StatusCode}", null, (HttpStatusCode)result.StatusCode);
        return result.Body;
    }

    /// <summary>POST（GraphQL/API 类源用），错误语义与 GetHtmlAsync 一致。</summary>
    protected async Task<string> PostAsync(string url, string contentType, string body, string? referer = null, CancellationToken ct = default)
    {
        var result = await Fetcher.PostAsync(url, contentType, body, new FetchOptions { SourceId = Id, Referer = referer }, ct);
        if (BlockDetector.IsBlocked(result.StatusCode, result.Body))
            throw new HttpRequestException("被反爬拦截", null, HttpStatusCode.Forbidden);
        if (result.StatusCode is < 200 or >= 400)
            throw new HttpRequestException($"HTTP {result.StatusCode}", null, (HttpStatusCode)result.StatusCode);
        return result.Body;
    }

    /// <summary>
    /// 番号精确匹配校验：前缀前无字母、数字段忽略前导零相等（SNOS-1 不会误匹配 SNOS-123，
    /// SNOS-001 与 SNOS1 视为同一番号）。
    /// </summary>
    internal static bool MatchesNumber(string text, string number)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(number)) return false;
        var target = Regex.Match(number, @"^([A-Za-z]+)[-_. ]?0*(\d+)$");
        if (!target.Success)
            return text.Contains(number, StringComparison.OrdinalIgnoreCase);

        var prefix = target.Groups[1].Value;
        var digits = target.Groups[2].Value.TrimStart('0');
        foreach (Match candidate in Regex.Matches(text, @"(?<![A-Za-z])([A-Za-z]{2,8})[-_. ]?0*(\d{1,6})(?![0-9])"))
        {
            if (candidate.Groups[1].Value.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                && candidate.Groups[2].Value.TrimStart('0') == digits)
                return true;
        }
        return false;
    }

    /// <summary>拆番号为（前缀, 去零数字）：剥掉尾部数字段，其余为前缀。兼容 SNOS-001 / MIDV00123 / FC2-PPV-3175496。</summary>
    internal static (string Prefix, string Digits) SplitNumber(string number)
    {
        var m = Regex.Match(number, @"^(.*?)[\-_.\s]?0*(\d+)$");
        if (!m.Success || m.Groups[2].Value.Length == 0) return (number, "");
        var prefix = m.Groups[1].Value.TrimEnd('-', '_', '.', ' ');
        return (prefix.Length > 0 ? prefix : number, m.Groups[2].Value.TrimStart('0'));
    }

    /// <summary>构造新元数据：填 Source 与 SourceUrls[Id] = 详情页地址。</summary>
    protected static VideoScrapeMetadata NewMetadata(string sourceId, string detailUrl) => new()
    {
        Source = sourceId,
        SourceUrls = { [sourceId] = detailUrl },
    };

    /// <summary>把相对路径解析为绝对 URL（兼容 "./x"、"//host/x" 两种形态）。</summary>
    protected static string ResolveUrl(string baseUrl, string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        if (url.StartsWith("//")) return "https:" + url;
        if (url.StartsWith("./")) url = url[2..];
        return baseUrl.TrimEnd('/') + (url.StartsWith("/") ? "" : "/") + url;
    }

    /// <summary>解析 "yyyy-MM-dd" 类日期文本，失败返回 null。</summary>
    protected static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"\d{4}-\d{2}-\d{2}");
        return m.Success && DateTime.TryParse(m.Value, out var date) ? date : null;
    }

    /// <summary>解析分钟数（"120 分鐘" / "120min"），失败返回 0。</summary>
    protected static int ParseMinutes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var m = Regex.Match(text, @"(\d{1,4})");
        return m.Success && int.TryParse(m.Groups[1].Value, out var minutes) ? minutes : 0;
    }
}
