namespace ResourceGrab.Core.Sources.ShortDrama;

// ---------------------------------------------------------------------------
// 短剧领域模型：只描述"一部剧 / 一集"是什么，不含任何站点细节。
// 站点差异全部收敛在 IShortDramaSource 的实现与解析器里。
// ---------------------------------------------------------------------------

/// <summary>列表来源。短剧没有「章节」层级，只有「剧 → 集」两层。</summary>
public enum DramaFeedKind
{
    /// <summary>精选推荐（/recommend）。</summary>
    Recommend,

    /// <summary>最近上新（/newest）。</summary>
    Newest,

    /// <summary>频道列表（/ai-duanju/ 等），需指定频道 slug。</summary>
    Channel,

    /// <summary>标签列表（/tag/{slug}/），需指定标签 slug。</summary>
    Tag,
}

/// <summary>榜单种类。站点目前提供三张榜，均为 TOP 20 单页。</summary>
public enum DramaRankKind
{
    Hot,
    Recommend,
    Potential,
}

/// <summary>列表卡片（列表页 / 搜索 / 榜单通用）。</summary>
public sealed class ShortDramaSummary
{
    /// <summary>站点侧剧集 id（data-track-id），如 "5033"。</summary>
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    /// <summary>详情页绝对地址（/detail/{id}/ 补全域名后）。</summary>
    public string DetailUrl { get; init; } = "";

    /// <summary>封面绝对地址。⚠️ 站点给的是带 auth_key 的签名地址，约 2 小时过期，不可长期缓存。</summary>
    public string CoverUrl { get; init; } = "";

    /// <summary>评分文本（"8.4"）；源站无评分时为空。</summary>
    public string ScoreText { get; init; } = "";

    /// <summary>更新态文本（"更新至6集" / "全7集"）。</summary>
    public string EpisodeText { get; init; } = "";

    /// <summary>角标（"新上" / "热播" / "高分" / "原创"）；无则空。</summary>
    public string Badge { get; init; } = "";

    /// <summary>类型名（"AI成人短剧" / "AI成人漫剧" …）。</summary>
    public string ChannelName { get; init; } = "";

    /// <summary>标签（取前若干个，用于卡片副信息）。</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>排行榜名次（仅榜单来源有值；0 表示不是榜单条目）。</summary>
    public int Rank { get; init; }

    /// <summary>榜单热度文本（"5525"）；非榜单为空。</summary>
    public string HeatText { get; init; } = "";

    /// <summary>简介（搜索结果与排行榜行会带，列表卡片也带一段）。</summary>
    public string Description { get; init; } = "";
}

/// <summary>一集。</summary>
public sealed class ShortDramaEpisode
{
    /// <summary>集号，从 1 开始（站点 data-ep-id）。</summary>
    public int Number { get; init; }

    /// <summary>播放页绝对地址（/video/{id}/ 或 /video/{id}/ep-{n}/）。</summary>
    public string PageUrl { get; init; } = "";

    /// <summary>展示用时长（"4:21"）；来自播放帧，未取流时为空。</summary>
    public string DurationText { get; init; } = "";

    /// <summary>该集 m3u8 直链；只在调用 GetEpisodeStreamAsync 后填充（签名短时有效，不落缓存）。</summary>
    public string? StreamUrl { get; set; }
}

/// <summary>剧集详情。</summary>
public sealed class ShortDramaDetail
{
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    public string DetailUrl { get; init; } = "";

    public string CoverUrl { get; init; } = "";

    public string Description { get; init; } = "";

    public string ScoreText { get; init; } = "";

    /// <summary>元信息原样文本（"4天前更新至6集 · 3.7w次播放 · 2026-10-29上线"）。</summary>
    public string FactsText { get; init; } = "";

    /// <summary>更新态（"更新至6集" / "全7集"）。</summary>
    public string EpisodeText { get; init; } = "";

    /// <summary>上线日期文本（"2026-10-29"）。</summary>
    public string ReleaseText { get; init; } = "";

    /// <summary>单集时长（秒）；站点 data-track-duration，未知为 0。</summary>
    public int DurationSeconds { get; init; }

    /// <summary>总集数（选集格数量）。</summary>
    public int TotalEpisodes { get; init; }

    public string ChannelName { get; init; } = "";

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<ShortDramaEpisode> Episodes { get; init; } = [];
}

/// <summary>一页列表。</summary>
public sealed class ShortDramaListing
{
    /// <summary>当前页（从 1 开始）。</summary>
    public int Page { get; init; } = 1;

    /// <summary>总页数；站点未给出时按「当前页有数据即认为有下一页」处理，这里为 null。</summary>
    public int? TotalPages { get; init; }

    /// <summary>总条数（仅搜索结果页给出，其它为 0）。</summary>
    public int TotalCount { get; init; }

    public IReadOnlyList<ShortDramaSummary> Items { get; init; } = [];

    /// <summary>同页内嵌的频道/标签入口（用于导航补全）；通常为空。</summary>
    public IReadOnlyList<ShortDramaTag> Tags { get; init; } = [];
}

/// <summary>标签（标签索引页 / 卡片标签链接）。</summary>
public sealed record ShortDramaTag(string Slug, string Name);

/// <summary>播放帧解析结果：一集的取流信息。</summary>
public sealed record DramaPlayFrame(
    string DramaId,
    int Episode,
    string StreamUrl,
    string DurationText,
    string? NextEpisodePath,
    string? PrevEpisodePath);
