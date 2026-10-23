namespace ResourceGrab.Core.Sources;

/// <summary>
/// 写真源的「随机」扩展点：实现该接口且 <see cref="ComicSourceInfo.Kind"/> 为
/// <see cref="ResourceKind.Photobook"/> 的源，会在「写真」入口下多出一个「随机」导航项。
/// <para>
/// 与 <see cref="ITaxonomySource"/> 一样是可选的按源能力扩展点：UI 侧直接做接口探测，
/// 因此 <see cref="ComicSourceInfo"/> 无需再声明一个平行的布尔开关（避免两处漂移）。
/// </para>
/// </summary>
public interface IRandomPhotobookSource
{
    /// <summary>
    /// 取若干随机图集。站点若忽略数量参数，返回条数以站点为准（调用方按实际条数渲染）。
    /// </summary>
    Task<SearchResult> GetRandomGalleriesAsync(int count, CancellationToken ct = default);
}
