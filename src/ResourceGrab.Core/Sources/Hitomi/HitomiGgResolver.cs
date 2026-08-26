using System.Text.RegularExpressions;

namespace ResourceGrab.Core.Sources.Hitomi;

/// <summary>
/// gg.js 解析与图片 URL 构造（对应原版 gg.rs + common.rs 的 URL 函数）。
/// gg.js 每 60 秒刷新一次并缓存，避免每个图片都重新拉取。
/// </summary>
public class HitomiGgResolver
{
    private const long RefreshIntervalMs = 60_000;

    private static readonly Regex DefaultValueRegex = new(@"var o = (\d)", RegexOptions.Compiled);
    private static readonly Regex CaseValueRegex = new(@"o = (\d); break;", RegexOptions.Compiled);
    private static readonly Regex CaseNumberRegex = new(@"case (\d+):", RegexOptions.Compiled);
    private static readonly Regex BValueRegex = new(@"b: '(.+)'", RegexOptions.Compiled);

    // subdomain_from_url：hash 倒数第 3 位起，取倒数第 2/3 位组成 16 进制数
    private static readonly Regex SubdomainHashRegex =
        new(@"/[0-9a-f]{61}([0-9a-f]{2})([0-9a-f])", RegexOptions.Compiled);

    // url_from_url：替换图床子域
    private static readonly Regex HostRegex =
        new(@"//..?\.(?:gold-usergeneratedcontent\.net|hitomi\.la)/", RegexOptions.Compiled);

    // s(hash)：取 hash 末 3 位（后两位 + 最后一位）反转拼成 16 进制数
    private static readonly Regex HashTailRegex = new(@"(..)(.)$", RegexOptions.Compiled);

    // real_full_path_from_hash：末位 / 倒数 2-3 位 / hash
    private static readonly Regex RealPathRegex = new(@"^.*(..)(.)$", RegexOptions.Compiled);

    private readonly HitomiHttpClient _client;
    private readonly object _lock = new();
    private GgState? _state;

    public HitomiGgResolver(HitomiHttpClient client)
    {
        _client = client;
    }

    internal sealed class GgState
    {
        public required int MDefault { get; init; }
        public required Dictionary<int, int> MMap { get; init; }
        public required string B { get; init; }
        public required long LastRetrievalMs { get; init; }
    }

    /// <summary>取 gg.js 解析结果（缓存 60 秒）。</summary>
    private async Task<GgState> EnsureAsync(CancellationToken ct)
    {
        var now = Environment.TickCount64;
        lock (_lock)
        {
            if (_state is not null && now - _state.LastRetrievalMs < RefreshIntervalMs)
            {
                return _state;
            }
        }

        var body = await _client.GetStringAsync($"https://{HitomiConstants.Domain}/gg.js", ct);
        var state = Parse(body, now);

        lock (_lock)
        {
            _state = state;
        }
        return state;
    }

    /// <summary>解析 gg.js（结构与原版 gg.rs refresh 一致，可独立测试）。</summary>
    internal static GgState Parse(string body, long nowMs)
    {
        var mDefault = 0;
        if (DefaultValueRegex.Match(body) is { Success: true } d)
        {
            int.TryParse(d.Groups[1].Value, out mDefault);
        }

        var mMap = new Dictionary<int, int>();
        var oValue = 0;
        if (CaseValueRegex.Match(body) is { Success: true } o)
        {
            int.TryParse(o.Groups[1].Value, out oValue);
            foreach (Match caseMatch in CaseNumberRegex.Matches(body))
            {
                if (int.TryParse(caseMatch.Groups[1].Value, out var caseNumber))
                {
                    mMap[caseNumber] = oValue;
                }
            }
        }

        var b = "";
        if (BValueRegex.Match(body) is { Success: true } bm)
        {
            b = bm.Groups[1].Value;
        }

        return new GgState { MDefault = mDefault, MMap = mMap, B = b, LastRetrievalMs = nowMs };
    }

    /// <summary>m(g)：hash 子域映射值（未命中走默认值）。</summary>
    private async Task<int> MAsync(int g, CancellationToken ct)
    {
        var state = await EnsureAsync(ct);
        return state.MMap.TryGetValue(g, out var value) ? value : state.MDefault;
    }

    /// <summary>b：gg.js 中的路径前缀。</summary>
    private async Task<string> BAsync(CancellationToken ct)
    {
        var state = await EnsureAsync(ct);
        return state.B;
    }

    /// <summary>s(hash)：hash 末位 + 倒数第 2-3 位组成的 16 进制数（如 "…96f7" → "76f" → 1903）。</summary>
    internal static string S(string hash)
    {
        var match = HashTailRegex.Match(hash);
        if (!match.Success)
        {
            throw new JmException($"无效的 hash 格式: {hash}");
        }
        var combined = match.Groups[2].Value + match.Groups[1].Value;
        return Convert.ToInt32(combined, 16).ToString();
    }

    /// <summary>full_path_from_hash：{b}{s}/{hash}。</summary>
    private async Task<string> FullPathFromHashAsync(string hash, CancellationToken ct)
    {
        var b = await BAsync(ct);
        return $"{b}{S(hash)}/{hash}";
    }

    /// <summary>real_full_path_from_hash：{末位}/{倒数 2-3 位}/{hash}（封面缩略图用）。</summary>
    internal static string RealFullPathFromHash(string hash)
    {
        var match = RealPathRegex.Match(hash);
        return match.Success
            ? $"{match.Groups[2].Value}/{match.Groups[1].Value}/{hash}"
            : hash;
    }

    /// <summary>subdomain_from_url：从 URL 中的 hash 计算图床子域前缀。</summary>
    private async Task<string> SubdomainFromUrlAsync(string url, string? basePrefix, string? dir, CancellationToken ct)
    {
        var baseIsEmpty = string.IsNullOrEmpty(basePrefix);
        var retval = "";
        if (baseIsEmpty)
        {
            retval = dir switch
            {
                "webp" => "w",
                "avif" => "a",
                _ => "",
            };
        }

        var match = SubdomainHashRegex.Match(url);
        if (!match.Success)
        {
            return "";
        }

        var g = Convert.ToInt32(match.Groups[2].Value + match.Groups[1].Value, 16);
        var m = await MAsync(g, ct);
        return baseIsEmpty ? $"{retval}{1 + m}" : $"{(char)(97 + m)}{basePrefix}";
    }

    /// <summary>url_from_url：把 URL 中的图床子域替换为计算出的子域。</summary>
    private async Task<string> UrlFromUrlAsync(string url, string? basePrefix, string? dir, CancellationToken ct)
    {
        var subdomain = await SubdomainFromUrlAsync(url, basePrefix, dir, ct);
        return HostRegex.Replace(url, $"//{subdomain}.gold-usergeneratedcontent.net/");
    }

    /// <summary>url_from_hash：构造 a.gold-usergeneratedcontent.net 基址下的图片 URL。</summary>
    private async Task<string> UrlFromHashAsync(GalleryFile image, string? dir, string? ext, CancellationToken ct)
    {
        var extension = ext ?? dir;
        if (extension is null)
        {
            var dot = image.Name.LastIndexOf('.');
            extension = dot >= 0 ? image.Name[(dot + 1)..] : "";
        }

        var url = "https://a.gold-usergeneratedcontent.net/";
        if (dir is not null && dir != "webp" && dir != "avif")
        {
            url += dir + "/";
        }
        url += await FullPathFromHashAsync(image.Hash, ct);
        url += "." + extension;
        return url;
    }

    /// <summary>url_from_url_from_hash：封面（base="tn"）或普通图片的统一入口。</summary>
    private async Task<string> UrlFromUrlFromHashAsync(
        GalleryFile image, string? dir, string? ext, string? basePrefix, CancellationToken ct)
    {
        if (basePrefix == "tn")
        {
            var realPath = RealFullPathFromHash(image.Hash);
            var url = $"https://a.gold-usergeneratedcontent.net/{dir}/{realPath}.{ext}";
            return await UrlFromUrlAsync(url, basePrefix, null, ct);
        }

        var baseUrl = await UrlFromHashAsync(image, dir, ext, ct);
        return await UrlFromUrlAsync(baseUrl, basePrefix, dir, ct);
    }

    /// <summary>
    /// 整页图片 URL：只要站点提供 webp 变体（haswebp 或 hasavif）就走 webp 子域，
    /// 站点对 avif-only 文件同样按需返回 webp（实测 w 子域 .webp 为 200）；
    /// 本应用图片管线（ImageSharp）不支持 AVIF，因此不请求 avif。
    /// </summary>
    public async Task<string> ImageUrlAsync(GalleryFile image, CancellationToken ct = default)
    {
        if (image.HasWebp > 0 || image.HasAvif > 0)
        {
            return await UrlFromUrlFromHashAsync(image, "webp", null, null, ct);
        }
        return await UrlFromUrlFromHashAsync(image, null, null, null, ct);
    }

    /// <summary>封面缩略图 URL（webpbigtn + tn 子域）。</summary>
    public async Task<string> CoverUrlAsync(GalleryInfo info, CancellationToken ct = default)
    {
        if (info.Files.Count == 0)
        {
            return "";
        }
        return await UrlFromUrlFromHashAsync(info.Files[0], "webpbigtn", "webp", "tn", ct);
    }
}


