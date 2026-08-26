namespace ResourceGrab.Core.Sources;

/// <summary>排行周期（今日 / 本週 / 本月 / 本年）。</summary>
public class RankPeriodInfo
{
    /// <summary>周期 id（站点侧参数，如 wnacg 的 day/week/month/year）。</summary>
    public string Id { get; init; } = "";

    /// <summary>展示名称，如 "本週"。</summary>
    public string Name { get; init; } = "";
}

/// <summary>排行浏览能力：实现该接口的源可在"排行"导航中展示。</summary>
public interface IRankSource
{
    /// <summary>该源支持的排行周期（按展示顺序）。</summary>
    IReadOnlyList<RankPeriodInfo> GetRankPeriods();

    /// <summary>按周期浏览排行（page 从 1 开始）。</summary>
    Task<SearchResult> GetRankAsync(string periodId, int page, CancellationToken ct = default);
}
