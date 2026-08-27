using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M3: VideoMetadataMerger — 字段级多源合并器
// ---------------------------------------------------------------------------

public sealed class VideoMetadataMerger
{
    private readonly VideoScrapeAdvancedSettings _config;

    public VideoMetadataMerger(VideoScrapeAdvancedSettings config) => _config = config;

    /// <summary>
    /// 从多个来源的 FetchResult 合并为最终的 VideoScrapeAggregate。
    /// </summary>
    public VideoScrapeAggregate Merge(string number, VideoContentKind contentKind, IReadOnlyList<VideoSourceFetchResult> results)
    {
        var successful = results.Where(r => r.Outcome == VideoSourceOutcome.Success && r.Metadata is not null).ToList();
        var merged = new VideoScrapeMetadata { Source = "aggregate" };
        var fieldSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var attempts = results.Select(r => new VideoSourceAttempt(
            r.SourceId, r.Language, r.Outcome, r.Error, r.ElapsedMs, DateTimeOffset.UtcNow)).ToList();

        if (successful.Count == 0)
        {
            return new VideoScrapeAggregate
            {
                Number = number,
                Metadata = merged,
                FieldSources = fieldSources,
                Attempts = attempts
            };
        }

        // --- 标量字段：按 fieldPriorities 择优 ---
        MergeScalar(merged, fieldSources, successful, contentKind, VideoMetadataFields.Title, (m, v) => m.Title = v);
        MergeScalar(merged, fieldSources, successful, contentKind, VideoMetadataFields.OriginalTitle, (m, v) => m.OriginalTitle = v);
        MergeScalar(merged, fieldSources, successful, contentKind, VideoMetadataFields.Description, (m, v) => m.Description = v);
        MergeScalar(merged, fieldSources, successful, contentKind, VideoMetadataFields.Series, (m, v) => m.Series = v);
        MergeScalar(merged, fieldSources, successful, contentKind, VideoMetadataFields.Studio, (m, v) => m.Studio = v);
        MergeScalar(merged, fieldSources, successful, contentKind, VideoMetadataFields.Publisher, (m, v) => m.Publisher = v);
        MergeScalar(merged, fieldSources, successful, contentKind, VideoMetadataFields.Director, (m, v) => m.Director = v);
        MergeScalar(merged, fieldSources, successful, contentKind, VideoMetadataFields.CoverUrl, (m, v) => m.CoverUrl = v);

        // --- 数值标量 ---
        MergeNumericScalar(merged, fieldSources, successful, contentKind, VideoMetadataFields.Score,
            (m) => m.Score, (m, v) => m.Score = v, v => v > 0);
        MergeNumericScalar(merged, fieldSources, successful, contentKind, "scoreVotes",
            (m) => m.ScoreVotes, (m, v) => m.ScoreVotes = v, v => v > 0);
        MergeDateScalar(merged, fieldSources, successful, contentKind, VideoMetadataFields.ReleaseDate,
            m => m.ReleaseDate, (m, v) => m.ReleaseDate = v);
        MergeIntScalar(merged, fieldSources, successful, contentKind, VideoMetadataFields.RuntimeMinutes,
            m => m.RuntimeMinutes, (m, v) => m.RuntimeMinutes = v, v => v > 0);

        // --- 集合字段：并集去重 ---
        MergeCollection(merged, fieldSources, successful, contentKind, VideoMetadataFields.Actors, m => m.Actors);
        MergeCollection(merged, fieldSources, successful, contentKind, VideoMetadataFields.Tags, m => m.Tags);
        MergeCollectionDistinct(merged, fieldSources, successful, contentKind, VideoMetadataFields.PreviewImageUrls, m => m.PreviewImageUrls);
        MergeCollectionDistinct(merged, fieldSources, successful, contentKind, VideoMetadataFields.RelatedNumbers, m => m.RelatedNumbers);

        // --- 布尔字段：OR ---
        MergeBoolOr(merged, fieldSources, successful, contentKind, VideoMetadataFields.HasMagnet, m => m.HasMagnet, (m, v) => m.HasMagnet = v);
        MergeBoolOr(merged, fieldSources, successful, contentKind, VideoMetadataFields.HasChineseSubtitle, m => m.HasChineseSubtitle, (m, v) => m.HasChineseSubtitle = v);

        // --- 标题特殊处理：中文标题 + 日文原题 ---
        HandleTitleSpecial(merged, fieldSources, contentKind);

        // --- SourceUrls：所有来源都收集 ---
        foreach (var r in successful)
        {
            foreach (var pair in r.Metadata!.SourceUrls)
                merged.SourceUrls.TryAdd(pair.Key, pair.Value);
        }

        // --- Reviews：所有来源并集 ---
        foreach (var r in successful)
            foreach (var review in r.Metadata!.Reviews)
                if (!merged.Reviews.Contains(review)) merged.Reviews.Add(review);

        return new VideoScrapeAggregate
        {
            Number = number,
            Metadata = merged,
            FieldSources = fieldSources,
            Attempts = attempts
        };
    }

    // -----------------------------------------------------------------------
    // 内部合并方法
    // -----------------------------------------------------------------------

    private void MergeScalar(
        VideoScrapeMetadata merged,
        Dictionary<string, string> fieldSources,
        List<VideoSourceFetchResult> successful,
        VideoContentKind kind,
        string field,
        Action<VideoScrapeMetadata, string> setter)
    {
        var priority = _config.GetFieldPriority(field, kind);
        var candidates = BuildCandidateChain(priority, successful);

        foreach (var (sourceId, metadata) in candidates)
        {
            var value = GetScalarValue(metadata, field);
            if (!string.IsNullOrWhiteSpace(value))
            {
                setter(merged, value);
                fieldSources[field] = sourceId;
                return;
            }
        }
    }

    private void MergeNumericScalar<T>(
        VideoScrapeMetadata merged,
        Dictionary<string, string> fieldSources,
        List<VideoSourceFetchResult> successful,
        VideoContentKind kind,
        string field,
        Func<VideoScrapeMetadata, T> getter,
        Action<VideoScrapeMetadata, T> setter,
        Func<T, bool> isValid)
    {
        var priority = _config.GetFieldPriority(field, kind);
        var candidates = BuildCandidateChain(priority, successful);

        foreach (var (sourceId, metadata) in candidates)
        {
            var value = getter(metadata);
            if (isValid(value))
            {
                setter(merged, value);
                fieldSources[field] = sourceId;
                return;
            }
        }
    }

    private void MergeDateScalar(
        VideoScrapeMetadata merged,
        Dictionary<string, string> fieldSources,
        List<VideoSourceFetchResult> successful,
        VideoContentKind kind,
        string field,
        Func<VideoScrapeMetadata, DateTime?> getter,
        Action<VideoScrapeMetadata, DateTime?> setter)
    {
        var priority = _config.GetFieldPriority(field, kind);
        var candidates = BuildCandidateChain(priority, successful);

        foreach (var (sourceId, metadata) in candidates)
        {
            var value = getter(metadata);
            if (value.HasValue)
            {
                setter(merged, value);
                fieldSources[field] = sourceId;
                return;
            }
        }
    }

    private void MergeIntScalar(
        VideoScrapeMetadata merged,
        Dictionary<string, string> fieldSources,
        List<VideoSourceFetchResult> successful,
        VideoContentKind kind,
        string field,
        Func<VideoScrapeMetadata, int> getter,
        Action<VideoScrapeMetadata, int> setter,
        Func<int, bool> isValid)
    {
        var priority = _config.GetFieldPriority(field, kind);
        var candidates = BuildCandidateChain(priority, successful);

        foreach (var (sourceId, metadata) in candidates)
        {
            var value = getter(metadata);
            if (isValid(value))
            {
                setter(merged, value);
                fieldSources[field] = sourceId;
                return;
            }
        }
    }

    private void MergeCollection(
        VideoScrapeMetadata merged,
        Dictionary<string, string> fieldSources,
        List<VideoSourceFetchResult> successful,
        VideoContentKind kind,
        string field,
        Func<VideoScrapeMetadata, List<string>> getter)
    {
        var allSources = new List<string>();
        foreach (var r in successful)
        {
            var items = getter(r.Metadata!);
            foreach (var item in items)
            {
                var trimmed = item.Trim();
                if (trimmed.Length == 0) continue;
                if (!merged.Actors.Contains(trimmed, StringComparer.OrdinalIgnoreCase) ||
                    field != VideoMetadataFields.Actors)
                {
                    // 对于非 actors 字段，用 merged 自己的列表做去重
                    var targetList = getter(merged);
                    if (!targetList.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    {
                        targetList.Add(trimmed);
                        if (!allSources.Contains(r.SourceId))
                            allSources.Add(r.SourceId);
                    }
                }
                else
                {
                    // actors 字段去重
                    if (!merged.Actors.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    {
                        merged.Actors.Add(trimmed);
                        if (!allSources.Contains(r.SourceId))
                            allSources.Add(r.SourceId);
                    }
                }
            }
        }

        if (allSources.Count > 0)
            fieldSources[field] = string.Join("+", allSources);
    }

    private void MergeCollectionDistinct(
        VideoScrapeMetadata merged,
        Dictionary<string, string> fieldSources,
        List<VideoSourceFetchResult> successful,
        VideoContentKind kind,
        string field,
        Func<VideoScrapeMetadata, List<string>> getter)
    {
        var allSources = new List<string>();
        foreach (var r in successful)
        {
            var items = getter(r.Metadata!);
            var targetList = getter(merged);
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                if (!targetList.Contains(item, StringComparer.OrdinalIgnoreCase))
                {
                    targetList.Add(item);
                    if (!allSources.Contains(r.SourceId))
                        allSources.Add(r.SourceId);
                }
            }
        }

        if (allSources.Count > 0)
            fieldSources[field] = string.Join("+", allSources);
    }

    private void MergeBoolOr(
        VideoScrapeMetadata merged,
        Dictionary<string, string> fieldSources,
        List<VideoSourceFetchResult> successful,
        VideoContentKind kind,
        string field,
        Func<VideoScrapeMetadata, bool> getter,
        Action<VideoScrapeMetadata, bool> setter)
    {
        foreach (var r in successful)
        {
            if (getter(r.Metadata!))
            {
                setter(merged, true);
                if (!fieldSources.ContainsKey(field))
                    fieldSources[field] = r.SourceId;
                else if (!fieldSources[field].Contains(r.SourceId))
                    fieldSources[field] += "+" + r.SourceId;
            }
        }
    }

    /// <summary>标题特殊处理：中文优先，同时保存日文原题。</summary>
    private void HandleTitleSpecial(VideoScrapeMetadata merged, Dictionary<string, string> fieldSources, VideoContentKind kind)
    {
        // 如果已有中文标题且已有日文原题，不做额外处理
        if (!string.IsNullOrWhiteSpace(merged.OriginalTitle) && !LooksJapanese(merged.Title))
            return;

        // 如果标题是日文且有其他来源的非日文标题可用，已经在 MergeScalar 阶段处理。
        // 这里只需要确保 OriginalTitle 在标题被替换为中文时正确保存。
        if (LooksJapanese(merged.Title) && fieldSources.TryGetValue(VideoMetadataFields.Title, out var titleSource))
        {
            // 标题是日文的，检查是否有 airav/iqqtv 等来源能提供中文标题
            // 这个逻辑由 MergeScalar 的优先级处理，此处只做 OriginalTitle 补充
            if (string.IsNullOrWhiteSpace(merged.OriginalTitle))
            {
                merged.OriginalTitle = merged.Title;
                // OriginalTitle 来源与 Title 相同
                if (!fieldSources.ContainsKey(VideoMetadataFields.OriginalTitle))
                    fieldSources[VideoMetadataFields.OriginalTitle] = titleSource;
            }
        }
    }

    // -----------------------------------------------------------------------
    // 工具方法
    // -----------------------------------------------------------------------

    /// <summary>按优先级构建候选链：优先级中的来源先排列，然后是其余成功来源。</summary>
    private static List<(string SourceId, VideoScrapeMetadata Metadata)> BuildCandidateChain(
        IReadOnlyList<string> priority, IReadOnlyList<VideoSourceFetchResult> successful)
    {
        var result = new List<(string, VideoScrapeMetadata)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 优先级来源先排
        foreach (var srcId in priority)
        {
            var match = successful.FirstOrDefault(r =>
                string.Equals(r.SourceId, srcId, StringComparison.OrdinalIgnoreCase));
            if (match is not null && seen.Add(match.SourceId))
                result.Add((match.SourceId, match.Metadata!));
        }

        // 剩余来源补充
        foreach (var r in successful)
        {
            if (seen.Add(r.SourceId))
                result.Add((r.SourceId, r.Metadata!));
        }

        return result;
    }

    private static string? GetScalarValue(VideoScrapeMetadata meta, string field) => field switch
    {
        "title" => meta.Title,
        "original_title" => meta.OriginalTitle,
        "description" => meta.Description,
        "series" => meta.Series,
        "studio" => meta.Studio,
        "publisher" => meta.Publisher,
        "director" => meta.Director,
        "cover_url" => meta.CoverUrl,
        _ => null
    };

    internal static bool LooksJapanese(string value) =>
        value.Any(ch => ch is >= '\u3041' and <= '\u3096' or >= '\u30A1' and <= '\u30FF');
}
