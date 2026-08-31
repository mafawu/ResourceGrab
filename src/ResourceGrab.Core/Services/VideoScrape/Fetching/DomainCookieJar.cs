using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.Core.Services.VideoScrape.Fetching;

// ---------------------------------------------------------------------------
// 阶段0: 按域名持久化的 Cookie 罐。
// 用途：过站后拿到的 cf_clearance 等放行 Cookie 在有效期内复用，避免反复触发
// 人机验证。此类 Cookie 与「User-Agent + 出口 IP」绑定，故记录 boundUa，
// 当前 UA 不一致即视为失效（出口 IP 变化无法在客户端感知，由挑战页自然兜底）。
// ---------------------------------------------------------------------------

public sealed class DomainCookieJar : IDisposable
{
    private sealed record CookieEntry(string Name, string Value, string BoundUa, DateTimeOffset FetchedAt);

    private sealed class JarFile
    {
        [JsonPropertyName("cookies")]
        public Dictionary<string, List<CookieEntry>> Cookies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(1500);

    private readonly string _path;
    private readonly string? _userAgent;
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<CookieEntry>> _byDomain;
    private Timer? _flushTimer;

    public DomainCookieJar(string path, string? userAgent = null, ILogger? logger = null)
    {
        _path = path;
        _userAgent = userAgent;
        _logger = logger;
        _byDomain = Load();
    }

    private Dictionary<string, List<CookieEntry>> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
            var file = JsonSerializer.Deserialize<JarFile>(File.ReadAllText(_path), JsonOpts);
            var result = new Dictionary<string, List<CookieEntry>>(StringComparer.OrdinalIgnoreCase);
            if (file?.Cookies is null) return result;

            foreach (var (domain, entries) in file.Cookies)
            {
                var valid = entries.Where(e => _userAgent is null || e.BoundUa == _userAgent).ToList();
                if (valid.Count > 0) result[domain] = valid;
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[CookieJar] 加载失败，使用空罐: {ex.Message}");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>取某 URL 对应站点的 Cookie 头（域名后缀匹配：a.missav.ws 命中 missav.ws 的条目）。</summary>
    public string? GetCookieHeader(Uri uri)
    {
        lock (_lock)
        {
            var pairs = new List<string>();
            foreach (var (domain, entries) in _byDomain)
            {
                if (!MatchesDomain(uri.Host, domain)) continue;
                foreach (var entry in entries)
                    if (_userAgent is null || entry.BoundUa == _userAgent)
                        pairs.Add($"{entry.Name}={entry.Value}");
            }
            return pairs.Count == 0 ? null : string.Join("; ", pairs);
        }
    }

    /// <summary>把响应的 Set-Cookie 行并入罐中（只取 name=value 部分，忽略属性）。</summary>
    public void UpdateFromResponse(Uri uri, IEnumerable<string> setCookieLines)
    {
        var host = uri.Host;
        var domain = ExtractRegistrableDomain(host);
        if (domain is null) return;

        var changed = false;
        lock (_lock)
        {
            if (!_byDomain.TryGetValue(domain, out var entries))
                _byDomain[domain] = entries = [];

            foreach (var line in setCookieLines)
            {
                var pair = line.Split(';')[0];
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var name = pair[..eq].Trim();
                var value = pair[(eq + 1)..].Trim();
                if (name.Length == 0) continue;

                var existing = entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    entries.Remove(existing);
                }
                entries.Add(new CookieEntry(name, value, _userAgent ?? "", DateTimeOffset.UtcNow));
                changed = true;
            }
        }
        if (changed) ScheduleFlush();
    }

    private static bool MatchesDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>粗略取可注册域（保留最后两段，足以覆盖 missav.ws / javdb.com 类站点）。</summary>
    private static string? ExtractRegistrableDomain(string host)
    {
        var parts = host.Split('.');
        return parts.Length < 2 ? null : string.Join('.', parts[^2..]);
    }

    private void ScheduleFlush()
    {
        // 去抖：连续请求只落盘一次
        _flushTimer?.Dispose();
        _flushTimer = new Timer(_ => { try { Flush(); } catch { /* 落盘失败不打扰抓取 */ } }, null, FlushDelay, Timeout.InfiniteTimeSpan);
    }

    public void Flush()
    {
        string json;
        lock (_lock)
        {
            if (_byDomain.Count == 0) return;
            json = JsonSerializer.Serialize(new JarFile { Cookies = _byDomain }, JsonOpts);
        }
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    public void Dispose() => _flushTimer?.Dispose();
}
