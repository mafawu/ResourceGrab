using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace ResourceGrab.Core.Sources.ShortDrama;

/// <summary>
/// 西瓜短剧 HTML 解析器。**纯函数**：输入 HTML 字符串，输出领域模型，不做任何 IO，
/// 因此可以拿离线夹具做单测（站点改版时靠这些测试第一时间发现）。
/// </summary>
public static class XiguaDramaParser
{
    private static readonly HtmlParser Parser = new();

    // ------------------------------------------------------------------
    // 列表
    // ------------------------------------------------------------------

    public static IDocument ParseDocument(string html) => Parser.ParseDocument(html);

    /// <summary>解析列表页卡片。优先级：.hg-card-grid.is-active 作用域 → 全页，再按 ID 去重。</summary>
    public static IReadOnlyList<ShortDramaSummary> ParseCards(string html, string baseUrl)
        => ParseCards(ParseDocument(html), baseUrl);

    public static IReadOnlyList<ShortDramaSummary> ParseCards(IDocument doc, string baseUrl)
    {
        var scoped = doc.QuerySelectorAll(
            $"{XiguaDramaConstants.ActiveGridSelector} {XiguaDramaConstants.CardSelector}");
        var nodes = scoped.Length > 0 ? scoped : doc.QuerySelectorAll(XiguaDramaConstants.CardSelector);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<ShortDramaSummary>(nodes.Length);
        foreach (var card in nodes)
        {
            var id = card.GetAttribute("data-track-id");
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
            list.Add(ToSummary(card, baseUrl));
        }
        return list;
    }

    private static ShortDramaSummary ToSummary(IElement card, string baseUrl)
    {
        var id = card.GetAttribute("data-track-id") ?? "";
        var href = card.QuerySelector(XiguaDramaConstants.CardTitleSelector)?.GetAttribute("href")
                   ?? card.QuerySelector("a[href^='/detail/']")?.GetAttribute("href")
                   ?? XiguaDramaConstants.DetailPath(id);

        var cover = card.QuerySelector(XiguaDramaConstants.CardCoverSelector);
        var coverUrl = cover?.GetAttribute("data-src");
        if (string.IsNullOrWhiteSpace(coverUrl)) coverUrl = cover?.GetAttribute("src");

        var score = Text(card.QuerySelector(XiguaDramaConstants.CardScoreSelector));
        var episode = card.QuerySelector(XiguaDramaConstants.CardEpisodeSelector);

        return new ShortDramaSummary
        {
            Id = id,
            Title = Clean(card.GetAttribute("data-track-title"))
                    ?? Clean(card.QuerySelector(XiguaDramaConstants.CardTitleSelector)?.TextContent)
                    ?? "",
            DetailUrl = Absolute(baseUrl, href),
            CoverUrl = Absolute(baseUrl, coverUrl),
            ScoreText = TrimScore(score),
            // data-ep-base 是站点给的干净更新态；没有时才退回整段文本（含 "3小时前" 前缀）
            EpisodeText = Clean(episode?.GetAttribute("data-ep-base")) ?? StripLeadingTime(Text(episode)),
            Badge = Text(card.QuerySelector(XiguaDramaConstants.CardBadgeSelector)),
            ChannelName = Clean(card.GetAttribute("data-track-type-name")) ?? "",
            Tags = CollectTags(card, XiguaDramaConstants.CardTagSelector),
            Description = Clean(Text(card.QuerySelector(XiguaDramaConstants.CardDescSelector))) ?? "",
        };
    }

    /// <summary>解析分页：优先 data-pages，其次最后一条分页链接，最后搜索页的「第 x / y 页」。</summary>
    public static int? ParseTotalPages(IDocument doc)
    {
        var jump = doc.QuerySelector("[data-hg-pager-jump]")?.GetAttribute("data-pages");
        if (int.TryParse(jump, out var pages) && pages > 0) return pages;

        var max = 0;
        foreach (var a in doc.QuerySelectorAll("a.hg-pager__page[href]"))
        {
            var m = Regex.Match(a.GetAttribute("href") ?? "", @"/page/(\d+)/?$|/(\d+)/$");
            if (!m.Success) continue;
            var raw = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (int.TryParse(raw, out var n) && n > max) max = n;
        }
        if (max > 0) return max;

        var meta = doc.QuerySelector(".hg-search-page__meta")?.TextContent ?? "";
        var mm = Regex.Match(meta, @"第\s*\d+\s*/\s*(\d+)\s*页");
        return mm.Success && int.TryParse(mm.Groups[1].Value, out var total) ? total : null;
    }

    /// <summary>搜索结果总条数（仅搜索页有）。</summary>
    public static int ParseSearchTotal(IDocument doc)
    {
        var attr = doc.QuerySelector("[data-track-search-total]")?.GetAttribute("data-track-search-total");
        if (int.TryParse(attr, out var n) && n >= 0) return n;

        var meta = doc.QuerySelector(".hg-search-page__meta")?.TextContent ?? "";
        var m = Regex.Match(meta, @"共\s*(\d+)\s*个结果");
        return m.Success && int.TryParse(m.Groups[1].Value, out var total) ? total : 0;
    }

    // ------------------------------------------------------------------
    // 详情
    // ------------------------------------------------------------------

    public static ShortDramaDetail? ParseDetail(string html, string baseUrl)
        => ParseDetail(ParseDocument(html), baseUrl);

    public static ShortDramaDetail? ParseDetail(IDocument doc, string baseUrl)
    {
        var root = doc.QuerySelector(XiguaDramaConstants.DetailRootSelector);
        if (root is null) return null;

        var id = Clean(root.GetAttribute("data-track-media-id")) ?? "";
        if (id.Length == 0) return null;

        var episodes = new List<ShortDramaEpisode>();
        foreach (var a in doc.QuerySelectorAll(XiguaDramaConstants.DetailEpisodeLinksSelector))
        {
            if (!int.TryParse(a.GetAttribute("data-ep-id"), out var number) || number <= 0) continue;
            episodes.Add(new ShortDramaEpisode
            {
                Number = number,
                PageUrl = Absolute(baseUrl, a.GetAttribute("href")),
            });
        }

        var facts = Clean(Text(root.QuerySelector(XiguaDramaConstants.DetailFactsSelector))) ?? "";
        var epHead = Clean(Text(root.QuerySelector(XiguaDramaConstants.DetailEpHeadTextSelector))) ?? "";
        var durationText = root.GetAttribute("data-track-duration");
        _ = int.TryParse(durationText, out var durationSeconds);

        return new ShortDramaDetail
        {
            Id = id,
            Title = Clean(Text(root.QuerySelector(XiguaDramaConstants.DetailTitleSelector))) ?? "",
            DetailUrl = Absolute(baseUrl, XiguaDramaConstants.DetailPath(id)),
            CoverUrl = Absolute(baseUrl,
                root.QuerySelector(XiguaDramaConstants.DetailPosterSelector)?.GetAttribute("data-src")),
            Description = Clean(Text(root.QuerySelector(XiguaDramaConstants.DetailDescSelector))) ?? "",
            ScoreText = TrimScore(Text(root.QuerySelector(XiguaDramaConstants.DetailScoreSelector))),
            FactsText = facts,
            EpisodeText = BuildEpisodeText(epHead, facts),
            ReleaseText = Regex.Match(facts, @"(\d{4}-\d{2}-\d{2})").Groups[1].Value,
            DurationSeconds = durationSeconds,
            TotalEpisodes = episodes.Count,
            ChannelName = Clean(root.GetAttribute("data-track-type-name")) ?? "",
            Tags = CollectTags(root, XiguaDramaConstants.DetailTagsSelector),
            Episodes = episodes,
        };
    }

    /// <summary>
    /// 更新态：优先用选集摘要「共 6 集 · 已更新 6 集」→「全6集」/「更新至6集」；
    /// 摘要缺失时退回元信息行里的「更新至N集」。
    /// </summary>
    private static string BuildEpisodeText(string epHead, string facts)
    {
        var m = Regex.Match(epHead, @"共\s*(\d+)\s*集(?:.*?已更新\s*(\d+)\s*集)?");
        if (m.Success)
        {
            var total = int.Parse(m.Groups[1].Value);
            var updated = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : total;
            return updated >= total ? $"全{total}集" : $"更新至{updated}集";
        }

        var updatedMatch = Regex.Match(facts, @"更新至\s*(\d+)\s*集");
        if (updatedMatch.Success) return $"更新至{updatedMatch.Groups[1].Value}集";

        var fullMatch = Regex.Match(facts, @"全\s*(\d+)\s*集");
        return fullMatch.Success ? $"全{fullMatch.Groups[1].Value}集" : "";
    }

    // ------------------------------------------------------------------
    // 播放帧
    // ------------------------------------------------------------------

    /// <summary>
    /// 解析播放帧。**必须取第 1 个 slide**：同一页后面还有 8 个「猜你喜欢」推荐位，
    /// 它们的 data-play-src 是别的剧，取错会播成完全不相关的片子。
    /// </summary>
    public static DramaPlayFrame? ParsePlayFrame(string html, string dramaId, int episode)
    {
        var doc = ParseDocument(html);
        var slide = doc.QuerySelector(XiguaDramaConstants.PlaySlideSelector);
        if (slide is null) return null;

        var src = slide.GetAttribute("data-play-src");
        if (string.IsNullOrWhiteSpace(src)) return null;

        var section = doc.QuerySelector(XiguaDramaConstants.PlayRootSelector);
        return new DramaPlayFrame(
            DramaId: Clean(slide.GetAttribute("data-play-id")) ?? dramaId,
            Episode: episode,
            // AngleSharp 会把 &amp; 解码成 &，这里再兜一层是防手写夹具里的双重转义
            StreamUrl: WebUtility.HtmlDecode(src),
            DurationText: Clean(slide.GetAttribute("data-play-duration")) ?? "",
            NextEpisodePath: Clean(section?.GetAttribute("data-hg-next-url")),
            PrevEpisodePath: Clean(section?.GetAttribute("data-hg-prev-url")));
    }

    // ------------------------------------------------------------------
    // 榜单 / 标签 / 线路发布页
    // ------------------------------------------------------------------

    public static IReadOnlyList<ShortDramaSummary> ParseRank(string html, string baseUrl)
        => ParseRank(ParseDocument(html), baseUrl);

    public static IReadOnlyList<ShortDramaSummary> ParseRank(IDocument doc, string baseUrl)
    {
        var list = new List<ShortDramaSummary>();
        foreach (var row in doc.QuerySelectorAll(XiguaDramaConstants.RankRowSelector))
        {
            var id = row.GetAttribute("data-track-id") ?? "";
            if (id.Length == 0) continue;

            var titleAnchor = row.QuerySelector("h2.hg-rank-item__title a") ?? row.QuerySelector("a[href^='/detail/']");
            var cover = row.QuerySelector("a.hg-rank-item__cover img")?.GetAttribute("data-src");

            var tags = new List<string>();
            foreach (var span in row.QuerySelectorAll(".hg-rank-item__tags span"))
            {
                var t = Clean(span.TextContent);
                if (!string.IsNullOrEmpty(t) && t != "·" && !t.EndsWith('分')) tags.Add(t.TrimStart('·').Trim());
            }

            list.Add(new ShortDramaSummary
            {
                Id = id,
                Title = Clean(titleAnchor?.TextContent) ?? Clean(row.GetAttribute("data-track-title")) ?? "",
                DetailUrl = Absolute(baseUrl, XiguaDramaConstants.DetailPath(id)),
                CoverUrl = Absolute(baseUrl, cover),
                Rank = int.TryParse(Clean(row.QuerySelector(".hg-rank-num")?.TextContent), out var r) ? r : list.Count + 1,
                HeatText = Clean(Text(row.QuerySelector(".hg-rank-metric-value")))?.Replace("热度", "").Trim() ?? "",
                ChannelName = Clean(row.GetAttribute("data-track-type-name")) ?? "",
                Description = Clean(Text(row.QuerySelector(".hg-rank-item__desc"))) ?? "",
                Tags = tags
                    .Where(t => !t.StartsWith("AI", StringComparison.Ordinal) && t != "短剧")
                    .Distinct(StringComparer.Ordinal)
                    .Take(6)
                    .ToList(),
            });
        }
        return list;
    }

    /// <summary>标签索引页：<c>/tag/{slug}/</c> → (slug, 名称)。</summary>
    public static IReadOnlyList<ShortDramaTag> ParseTags(string html, string baseUrl)
        => ParseTags(ParseDocument(html));

    public static IReadOnlyList<ShortDramaTag> ParseTags(IDocument doc)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<ShortDramaTag>();
        foreach (var a in doc.QuerySelectorAll(XiguaDramaConstants.TagLinkSelector))
        {
            var href = a.GetAttribute("href") ?? "";
            var m = Regex.Match(href, @"/tag/([A-Za-z0-9_\-]+)/?");
            if (!m.Success) continue;
            var slug = m.Groups[1].Value;
            var name = Clean(a.TextContent) ?? slug;
            if (name.Length == 0 || !seen.Add(slug)) continue;
            list.Add(new ShortDramaTag(slug, name));
        }
        return list;
    }

    /// <summary>
    /// 从官方线路发布页（/go-home/）提取可用域名。
    /// 站点域名是"随机子域 + 发布页"形态，这是唯一可靠的换线来源。
    /// </summary>
    public static IReadOnlyList<string> ParseHosts(string html)
        => ParseHosts(ParseDocument(html));

    public static IReadOnlyList<string> ParseHosts(IDocument doc)
    {
        var hosts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? raw)
        {
            var value = Clean(raw);
            if (string.IsNullOrEmpty(value)) return;
            if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                value = "https://" + value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return;
            var origin = uri.GetLeftPart(UriPartial.Authority);
            if (seen.Add(origin)) hosts.Add(origin);
        }

        foreach (var p in doc.QuerySelectorAll("p.hg-gh-field")) Add(p.TextContent);
        foreach (var a in doc.QuerySelectorAll("a.hg-gh-addr__actions a[href^='https://']"))
            Add(a.GetAttribute("href"));
        return hosts;
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    private static List<string> CollectTags(IElement scope, string selector)
    {
        var list = new List<string>();
        foreach (var a in scope.QuerySelectorAll(selector))
        {
            var t = Clean(a.TextContent);
            if (!string.IsNullOrEmpty(t)) list.Add(t);
        }
        return list;
    }

    /// <summary>绝对化：baseUrl 为空时原样返回（夹具测试里不关心域名）。</summary>
    private static string Absolute(string? baseUrl, string? path)
    {
        var p = (path ?? "").Trim();
        if (p.Length == 0) return "";
        if (p.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return p;
        if (string.IsNullOrWhiteSpace(baseUrl)) return p;
        return p.StartsWith('/')
            ? baseUrl.TrimEnd('/') + p
            : baseUrl.TrimEnd('/') + "/" + p;
    }

    /// <summary>折叠空白并去首尾空；空串归一成 null。</summary>
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
        return collapsed.Length == 0 ? null : collapsed;
    }

    private static string Text(IElement? element) => element?.TextContent ?? "";

    private static string TrimScore(string raw)
    {
        var t = Clean(raw) ?? "";
        return t.EndsWith('分') ? t[..^1].Trim() : t;
    }

    /// <summary>卡片更新态在缺 data-ep-base 时会带 "3小时前" 前缀，截掉它。</summary>
    private static string StripLeadingTime(string raw)
    {
        var t = Clean(raw) ?? "";
        var m = Regex.Match(t, @"^\d+\s*(?:秒|分钟|小时|天|周|个月)前");
        return m.Success ? t[m.Length..].Trim() : t;
    }
}
