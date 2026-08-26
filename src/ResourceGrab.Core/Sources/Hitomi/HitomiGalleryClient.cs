using System.Text.Json;

namespace ResourceGrab.Core.Sources.Hitomi;

/// <summary>
/// hitomi 数据协议客户端：版本号、nozomi 画廊 id 列表、画廊信息、B-tree 关键词搜索。
/// 协议细节与 lanyeeee/hitomi-downloader 的 search.rs / common.rs 保持一致。
/// </summary>
public class HitomiGalleryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HitomiHttpClient _http;
    private readonly object _versionLock = new();
    private string? _cachedVersion;

    public HitomiGalleryClient(HitomiHttpClient http)
    {
        _http = http;
    }

    /// <summary>galleriesindex 版本号（进程内缓存；取不到时返回空串）。</summary>
    public async Task<string> GetGalleryIndexVersionAsync(CancellationToken ct = default)
    {
        lock (_versionLock)
        {
            if (_cachedVersion is not null)
            {
                return _cachedVersion;
            }
        }

        var version = "";
        try
        {
            var text = await _http.GetStringAsync($"{_http.BaseUrl}/{HitomiConstants.GalleryIndexDir}/version", ct);
            version = text.Trim();
        }
        catch
        {
            // 版本号取不到时按原版 unwrap_or_default 处理：搜索直接返回空
        }

        lock (_versionLock)
        {
            _cachedVersion = version;
        }
        return version;
    }

    /// <summary>读取 nozomi 列表（BigEndian Int32 序列）；文件不存在时返回空列表。</summary>
    public async Task<List<int>> GetNozomiIdsAsync(string? area, string tag, string language, CancellationToken ct = default)
    {
        var url = string.IsNullOrEmpty(area)
            ? $"{_http.BaseUrl}/{HitomiConstants.CompressedNozomiPrefix}/{tag}-{language}{HitomiConstants.NozomiExtension}"
            : $"{_http.BaseUrl}/{HitomiConstants.CompressedNozomiPrefix}/{area}/{tag}-{language}{HitomiConstants.NozomiExtension}";

        var bytes = await _http.GetBytesOrNullAsync(url, ct);
        return bytes is null ? new List<int>() : HitomiBinaryIndex.ParseNozomiIds(bytes);
    }

    /// <summary>获取画廊信息（galleries/{id}.js）。</summary>
    public async Task<GalleryInfo> GetGalleryInfoAsync(int id, CancellationToken ct = default)
    {
        var body = await _http.GetStringAsync($"{_http.BaseUrl}/galleries/{id}.js", ct);
        var json = body.StartsWith("var galleryinfo = ", StringComparison.Ordinal)
            ? body["var galleryinfo = ".Length..]
            : body;
        return JsonSerializer.Deserialize<GalleryInfo>(json, JsonOptions)
               ?? throw new JmException($"解析画廊信息失败: {id}");
    }

    /// <summary>
    /// 关键词搜索：无冒号时走 B-tree（sha256 前 4 字节定位 galleriesindex 数据段），
    /// 带冒号（如 tag:xxx / language:xxx）时按命名空间读取对应 nozomi 列表。
    /// 与原版 get_gallery_ids_for_query 一致；失败返回空列表。
    /// </summary>
    public async Task<List<int>> GetGalleryIdsForQueryAsync(string query, CancellationToken ct = default)
    {
        var normalized = query.Replace("_", " ");
        var colonIndex = normalized.IndexOf(':');
        if (colonIndex >= 0)
        {
            var ns = normalized[..colonIndex];
            var tag = normalized[(colonIndex + 1)..];
            return ns switch
            {
                "female" or "male" => await GetNozomiIdsAsync("tag", normalized, "all", ct),
                "language" => await GetNozomiIdsAsync(null, tag, "index", ct),
                _ => await GetNozomiIdsAsync(ns, tag, "all", ct),
            };
        }

        var key = HitomiBinaryIndex.HashTerm(normalized);
        var version = await GetGalleryIndexVersionAsync(ct);
        if (version.Length == 0)
        {
            return new List<int>();
        }

        var indexUrl = $"{_http.BaseUrl}/{HitomiConstants.GalleryIndexDir}/galleries.{version}.index";
        var root = await GetNodeAtAddressAsync(indexUrl, 0, ct);
        if (root is null)
        {
            return new List<int>();
        }

        var data = await BSearchAsync(indexUrl, key, root, ct);
        if (data is null)
        {
            return new List<int>();
        }

        var dataUrl = $"{_http.BaseUrl}/{HitomiConstants.GalleryIndexDir}/galleries.{version}.data";
        try
        {
            var bytes = await _http.GetRangeAsync(dataUrl, data.Value.Offset, data.Value.Length, ct);
            return HitomiBinaryIndex.ParseGalleryIdsFromData(bytes);
        }
        catch
        {
            return new List<int>();
        }
    }

    private async Task<HitomiIndexNode?> GetNodeAtAddressAsync(string indexUrl, long address, CancellationToken ct)
    {
        try
        {
            var bytes = await _http.GetRangeAsync(indexUrl, address, HitomiConstants.MaxNodeSize, ct);
            return HitomiBinaryIndex.DecodeNode(bytes);
        }
        catch
        {
            return null;
        }
    }

    private async Task<(long Offset, int Length)?> BSearchAsync(
        string indexUrl, byte[] key, HitomiIndexNode node, CancellationToken ct)
    {
        if (node.Keys.Count == 0)
        {
            return null;
        }

        var (found, index) = HitomiBinaryIndex.LocateKey(key, node);
        if (found)
        {
            return node.Datas[index];
        }
        if (HitomiBinaryIndex.IsLeaf(node))
        {
            return null;
        }

        var next = await GetNodeAtAddressAsync(indexUrl, node.SubNodeAddresses[index], ct);
        return next is null ? null : await BSearchAsync(indexUrl, key, next, ct);
    }
}

