namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// M11: 演员来源合并器 — 多源并集去重
// ---------------------------------------------------------------------------

public sealed class VideoActorMerger
{
    private readonly IEnumerable<IVideoActorSource> _sources;

    public VideoActorMerger(IEnumerable<IVideoActorSource> sources) => _sources = sources;

    public async Task<VideoActorMetadata?> MergeAsync(string actorName, CancellationToken ct)
    {
        var tasks = _sources.Select(async source =>
        {
            try { return await source.FetchAsync(actorName, ct); }
            catch { return ActorSourceFetchResult.Failure(source.Id, ActorSourceOutcome.ParseError, "Exception"); }
        });

        var results = await Task.WhenAll(tasks);
        var successful = results.Where(r => r.Outcome == ActorSourceOutcome.Success && r.Metadata is not null).ToList();
        if (successful.Count == 0) return null;

        var merged = new VideoActorMetadata { Name = actorName };

        // 标量字段择优
        foreach (var r in successful)
        {
            var meta = r.Metadata!;
            if (meta.Birthday is not null && merged.Birthday is null) { merged.Birthday = meta.Birthday; merged.FieldSources["birthday"] = r.SourceId; }
            if (meta.Birthplace is not null && merged.Birthplace is null) { merged.Birthplace = meta.Birthplace; merged.FieldSources["birthplace"] = r.SourceId; }
            if (meta.Height is not null && merged.Height is null) { merged.Height = meta.Height; merged.FieldSources["height"] = r.SourceId; }
        }

        // 集合字段并集
        var aliasSources = new List<string>();
        var imageSources = new List<string>();
        foreach (var r in successful)
        {
            var meta = r.Metadata!;
            foreach (var alias in meta.Aliases)
                if (!merged.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase)) { merged.Aliases.Add(alias); if (!aliasSources.Contains(r.SourceId)) aliasSources.Add(r.SourceId); }
            foreach (var img in meta.ImageUrls)
                if (!merged.ImageUrls.Contains(img, StringComparer.OrdinalIgnoreCase)) { merged.ImageUrls.Add(img); if (!imageSources.Contains(r.SourceId)) imageSources.Add(r.SourceId); }
            foreach (var pair in meta.SourceUrls) merged.SourceUrls.TryAdd(pair.Key, pair.Value);
        }
        if (aliasSources.Count > 0) merged.FieldSources["aliases"] = string.Join("+", aliasSources);
        if (imageSources.Count > 0) merged.FieldSources["images"] = string.Join("+", imageSources);

        return merged;
    }
}
