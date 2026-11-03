namespace ResourceGrab.Core.Sources.ShortDrama;

/// <summary>
/// 短剧源统一接口。
/// <para>
/// 与 <see cref="IVideoSource"/> 的分工：视频源面向"一个文件 = 一部片"，短剧是"一部剧 = N 集"，
/// 且播放地址必须逐集现取（签名短时有效），因此取流单独一个方法而不是塞进详情里。
/// </para>
/// </summary>
public interface IShortDramaSource
{
    /// <summary>源唯一标识（配置文件里用它做 key），如 "xigua"。</summary>
    string Id { get; }

    /// <summary>展示名称，如 "西瓜短剧"。</summary>
    string DisplayName { get; }

    /// <summary>
    /// 取列表。渠道类来源用 <paramref name="scope"/> 指定频道 slug 或标签 slug
    /// （<see cref="DramaFeedKind.Channel"/> / <see cref="DramaFeedKind.Tag"/>）。
    /// </summary>
    Task<ShortDramaListing> GetFeedAsync(
        DramaFeedKind kind, int page, string? scope = null, CancellationToken ct = default);

    /// <summary>关键词搜索（page 从 1 开始）。</summary>
    Task<ShortDramaListing> SearchAsync(string keyword, int page, CancellationToken ct = default);

    /// <summary>剧集详情（含完整选集，但不含任何流地址）。</summary>
    Task<ShortDramaDetail?> GetDetailAsync(string dramaId, CancellationToken ct = default);

    /// <summary>
    /// 取某一集的 m3u8 直链。每次调用都会重新请求播放帧页面——
    /// 站点签名约 2 小时过期，缓存这里会直接变成"点播放就 403"。
    /// </summary>
    Task<DramaPlayFrame?> GetPlayFrameAsync(string dramaId, int episode, CancellationToken ct = default);

    /// <summary>榜单（TOP 20 单页）。</summary>
    Task<IReadOnlyList<ShortDramaSummary>> GetRankAsync(
        DramaRankKind kind, CancellationToken ct = default);

    /// <summary>标签索引。</summary>
    Task<IReadOnlyList<ShortDramaTag>> GetTagsAsync(CancellationToken ct = default);

    /// <summary>当前生效的线路（用于 UI 线路卡展示）；源不支持时返回 null。</summary>
    string? LineHost => null;

    /// <summary>重新读取官方发布页换线；返回发现的线路列表（不支持时返回空）。</summary>
    Task<IReadOnlyList<string>> RefreshLinesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
}
