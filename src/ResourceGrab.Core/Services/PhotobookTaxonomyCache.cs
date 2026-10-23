using System.Text.Json;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Sources;

namespace ResourceGrab.Core.Services;

/// <summary>
/// 写真维度条目列表（分类 / 标签 / Coser / 角色 / 系列 / 专题）的磁盘缓存。
/// <para>
/// 存在的理由：维度条目往往是「一次要拉几十个请求」的大列表
/// （lovecutes 的出演维度实测 2166 项、需要 136 个请求），不该每次进页面都干等网络。
/// 页面先用上次的结果立即出内容，再在后台刷新，刷新成功且内容有变化时才覆盖界面与缓存
/// —— 即 stale-while-revalidate。
/// </para>
/// <para>
/// 落盘 <c>config/photobook-taxonomy/{sourceId}_{kind}.json</c>，与视频侧的
/// <c>video-source-cache</c> 同级，延续「数据都在 exe 同级 config\、纯 JSON 无数据库」的便携约定。
/// </para>
/// <para>
/// 缓存只做「加速」不做「权威」：读失败、版本不符、文件损坏一律当作没有缓存，调用方正常走网络。
/// </para>
/// </summary>
public sealed class PhotobookTaxonomyCache
{
    /// <summary>缓存结构版本。条目的含义/解析规则变了就 +1，旧文件自动作废。</summary>
    private const string SchemaVersion = "v1";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _cacheDir;
    private readonly ILogger? _logger;

    public PhotobookTaxonomyCache(string cacheDir, ILogger? logger = null)
    {
        _cacheDir = cacheDir;
        _logger = logger;
        try
        {
            Directory.CreateDirectory(_cacheDir);
        }
        catch (Exception ex)
        {
            // 目录建不出来就退化成「不缓存」，不影响功能
            _logger?.Warn($"[TaxonomyCache] 缓存目录不可用: {ex.Message}");
        }
    }

    /// <summary>一份缓存内容。</summary>
    /// <param name="SourceId">所属写真源 Id。</param>
    /// <param name="Kind">维度。</param>
    /// <param name="SavedAt">写入时间（本地时间），用于在界面上注明「缓存于 …」。</param>
    /// <param name="Items">条目列表。</param>
    public sealed record Snapshot(string SourceId, TaxonomyKind Kind, DateTimeOffset SavedAt, IReadOnlyList<TaxonomyItem> Items);

    /// <summary>读缓存；没有 / 版本不符 / 损坏都返回 null。</summary>
    public Task<Snapshot?> LoadAsync(string sourceId, TaxonomyKind kind, CancellationToken ct = default)
        => Task.Run(() => Load(sourceId, kind), ct);

    /// <summary>写缓存（先写临时文件再原子替换，避免中途崩溃留下半截 JSON）。</summary>
    public Task SaveAsync(string sourceId, TaxonomyKind kind, IReadOnlyList<TaxonomyItem> items, CancellationToken ct = default)
        => Task.Run(() => Save(sourceId, kind, items), ct);

    private Snapshot? Load(string sourceId, TaxonomyKind kind)
    {
        var path = FilePath(sourceId, kind);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(path), JsonOpts);
            if (payload is null || payload.Schema != SchemaVersion || payload.Items is null)
            {
                _logger?.Warn($"[TaxonomyCache] {path} 版本或结构不符（schema={payload?.Schema}），忽略");
                return null;
            }

            return new Snapshot(sourceId, kind, payload.SavedAt, payload.Items);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[TaxonomyCache] 读取 {path} 失败: {ex.Message}");
            return null;
        }
    }

    private void Save(string sourceId, TaxonomyKind kind, IReadOnlyList<TaxonomyItem> items)
    {
        var path = FilePath(sourceId, kind);
        var tmp = path + ".tmp";
        try
        {
            var payload = new Payload
            {
                Schema = SchemaVersion,
                SourceId = sourceId,
                Kind = kind.ToString(),
                SavedAt = DateTimeOffset.Now,
                Items = items.ToList(),
            };
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[TaxonomyCache] 写入 {path} 失败: {ex.Message}");
            try { File.Delete(tmp); } catch { /* 清理失败无所谓 */ }
        }
    }

    private string FilePath(string sourceId, TaxonomyKind kind)
    {
        var safeId = new string(sourceId.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        if (safeId.Length == 0)
        {
            safeId = "unknown";
        }
        return Path.Combine(_cacheDir, $"{safeId}_{kind.ToString().ToLowerInvariant()}.json");
    }

    /// <summary>落盘结构。与 <see cref="TaxonomyItem"/> 分开，便于独立演进与校验 schema。</summary>
    private sealed class Payload
    {
        public string Schema { get; set; } = "";
        public string SourceId { get; set; } = "";
        public string Kind { get; set; } = "";
        public DateTimeOffset SavedAt { get; set; }
        public List<TaxonomyItem>? Items { get; set; }
    }
}
