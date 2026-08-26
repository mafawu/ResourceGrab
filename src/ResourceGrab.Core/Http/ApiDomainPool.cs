using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;

namespace ResourceGrab.Core.Http;

/// <summary>
/// 接口域名池：按 配置 apiDomains &gt; apiDomain（旧版单域名）&gt; 内置默认列表 构建域名列表，
/// 轮换提供候选域名；失败的域名进入冷却（短时间内跳过，避免每次请求都先等失效域名超时），
/// 成功后指针前进；配置中域名列表变化时自动重置轮换状态。
/// </summary>
public class ApiDomainPool
{
    private readonly ConfigService _configService;
    private readonly TimeSpan _cooldown;
    private readonly object _lock = new();

    private List<string> _domains = new();
    private string _fingerprint = "";
    private int _pointer = -1;
    private readonly Dictionary<string, DateTime> _cooldownUntil = new(StringComparer.OrdinalIgnoreCase);

    public ApiDomainPool(ConfigService configService, TimeSpan? cooldown = null)
    {
        _configService = configService;
        _cooldown = cooldown ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>当前域名快照（去重、去除 scheme / 空白，按优先级合并配置）。</summary>
    public IReadOnlyList<string> GetDomains()
    {
        lock (_lock)
        {
            RefreshLocked();
            return _domains;
        }
    }

    /// <summary>返回下一个候选域名（跳过冷却中的域名；全部冷却时清空冷却并周期性重试）。</summary>
    public string Next()
    {
        lock (_lock)
        {
            RefreshLocked();
            if (_domains.Count == 0)
            {
                throw new JmException("未配置可用的接口域名");
            }

            var now = DateTime.UtcNow;
            for (var i = 1; i <= _domains.Count; i++)
            {
                var index = Mod(_pointer + i, _domains.Count);
                if (!_cooldownUntil.TryGetValue(_domains[index], out var until) || until <= now)
                {
                    return _domains[index];
                }
            }

            // 全部域名都在冷却：清空冷却并重试，避免永久跳过已恢复的域名
            _cooldownUntil.Clear();
            return _domains[Mod(_pointer + 1, _domains.Count)];
        }
    }

    /// <summary>记录域名请求成功：指针指向该域名（下次从其后轮换），并取消其冷却。</summary>
    public void MarkSuccess(string domain)
    {
        lock (_lock)
        {
            var index = _domains.FindIndex(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _pointer = index;
                _cooldownUntil.Remove(domain);
            }
        }
    }

    /// <summary>记录域名请求失败：进入冷却，短时间内跳过。</summary>
    public void MarkFailed(string domain)
    {
        lock (_lock)
        {
            _cooldownUntil[domain] = DateTime.UtcNow + _cooldown;
        }
    }

    private void RefreshLocked()
    {
        var (domains, fingerprint) = Build();
        if (string.Equals(fingerprint, _fingerprint, StringComparison.Ordinal))
        {
            return;
        }
        _domains = domains;
        _fingerprint = fingerprint;
        _pointer = -1;
        _cooldownUntil.Clear();
    }

    private (List<string> Domains, string Fingerprint) Build()
    {
        var config = _configService.Current;
        var sources = new List<string>();
        if (config.ApiDomains is { Count: > 0 })
        {
            sources.AddRange(config.ApiDomains);
        }
        else if (!string.IsNullOrWhiteSpace(config.ApiDomain))
        {
            sources.Add(config.ApiDomain);
        }
        else
        {
            sources.AddRange(JmConstants.ApiDomains);
        }

        var domains = new List<string>(sources.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in sources)
        {
            var domain = Normalize(raw);
            if (domain.Length == 0 || !seen.Add(domain))
            {
                continue;
            }
            domains.Add(domain);
        }
        return (domains, string.Join(",", domains));
    }

    /// <summary>归一化域名：去空白、去 http(s):// 前缀、去尾部斜杠。</summary>
    public static string Normalize(string? raw)
    {
        var domain = raw?.Trim() ?? "";
        if (domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            domain = domain["http://".Length..];
        }
        else if (domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            domain = domain["https://".Length..];
        }
        return domain.Trim().TrimEnd('/').ToLowerInvariant();
    }

    private static int Mod(int value, int count) => ((value % count) + count) % count;
}