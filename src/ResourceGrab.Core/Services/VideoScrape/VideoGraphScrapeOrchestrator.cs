using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// 阶段1: 图引擎编排器。
// 单条条目的刮削流程：解析番号/内容类型 → 按 contentRoutes ∩ 启用源 ∩ 已注册源
// 组装抓取节点 → VideoFetchGraphScheduler 调度（缓存注入 + 并发抓取）→
// VideoMetadataMerger 逐字段择优合并 → 复用老 VideoScrapeService 的
// ApplyMetadata / DownloadImagesAsync / WriteNfo / 报告方法落库。
// 批处理循环、进度回填语义与老 VideoScrapeService.ScrapeAsync 保持一致，
// 因此 VideoScrapeTaskExecutor 与 VideoTaskDashboard 无需感知引擎差异。
// ---------------------------------------------------------------------------

public sealed class VideoGraphScrapeOrchestrator
{
    private readonly VideoLibraryService _library;
    private readonly ConfigService? _configService;
    private readonly VideoScrapeAdvancedSettings _advanced;
    private readonly IVideoScrapeSourceRegistry _registry;
    private readonly VideoFetchGraphScheduler _scheduler;
    private readonly VideoMetadataMerger _merger;
    private readonly VideoScrapeService _applyService;
    private readonly VideoSourceHealthService? _health;
    private readonly ILogger? _logger;

    /// <summary>单条条目刮削完成事件（成功/失败/未匹配都会触发），供 UI 刷新。</summary>
    public event Action<VideoItem>? ItemChanged;

    public VideoGraphScrapeOrchestrator(
        VideoLibraryService library,
        ConfigService configService,
        VideoScrapeAdvancedSettings advanced,
        IVideoScrapeSourceRegistry registry,
        VideoFetchGraphScheduler scheduler,
        VideoMetadataMerger merger,
        VideoScrapeService applyService,
        VideoSourceHealthService? health = null,
        ILogger? logger = null)
    {
        _library = library;
        _configService = configService;
        _advanced = advanced;
        _registry = registry;
        _scheduler = scheduler;
        _merger = merger;
        _applyService = applyService;
        _health = health;
        _logger = logger;
    }

    private VideoScrapeSettings Settings => _configService?.Current.VideoScraping ?? new VideoScrapeSettings();

    public async Task ScrapeAsync(IEnumerable<string> ids, Action<VideoScrapeProgress>? progressCallback,
        CancellationToken ct, bool skipSucceeded = false)
    {
        var items = ids.Select(_library.GetById).Where(i => i != null).Cast<VideoItem>().ToList();
        var alreadyDone = 0;
        if (skipSucceeded)
        {
            alreadyDone = items.RemoveAll(i => i.ScrapeStatus == Models.ScrapeStatus.Success);
            if (alreadyDone > 0)
                _logger?.Info($"[ScrapeGraph] 跳过 {alreadyDone} 条已成功条目");
        }
        var progress = new VideoScrapeProgress(items.Count + alreadyDone);
        if (progressCallback != null) progress.Changed += progressCallback;
        if (alreadyDone > 0)
        {
            progress.AddSkipped(alreadyDone);
            progress.Log($"跳过 {alreadyDone} 条已成功条目");
        }
        progress.Publish();

        var degree = Math.Clamp(Settings.Concurrency, 1, 4);
        try
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct };
            await Parallel.ForEachAsync(items, options, async (item, token) => await ScrapeItemAsync(item, progress, skipSucceeded, token));
        }
        finally
        {
            _library.Flush();
        }
    }

    private async Task ScrapeItemAsync(VideoItem item, VideoScrapeProgress progress, bool useCache, CancellationToken ct)
    {
        var itemSw = System.Diagnostics.Stopwatch.StartNew();
        if (item.Number.Length == 0)
        {
            var reparsed = VideoNumberParser.Parse(item.FilePath);
            if (reparsed.Number.Length > 0)
            {
                item.Number = reparsed.Number;
                item.Part = reparsed.Part;
                _library.Update(item, deferSave: true);
                progress.Log($"{item.FileName}: 重新解析番号 {item.Number}");
                _logger?.Info($"[ScrapeGraph] {item.FileName} 番号为空，已重新解析为 {item.Number}");
            }
        }
        if (item.Number.Length == 0)
        {
            item.ScrapeStatus = Models.ScrapeStatus.Skipped;
            progress.RecordSkipped();
            progress.RecordItem(new VideoTaskItemResult { FileName = item.FileName, Outcome = "跳过", Detail = "未识别番号", ElapsedMs = (int)itemSw.ElapsedMilliseconds });
            progress.Log($"{item.FileName}: 未识别番号");
            _logger?.Warn($"[ScrapeGraph] 跳过 {item.FileName}: 未识别番号 (path={item.FilePath})");
            _library.Update(item, deferSave: true);
            ItemChanged?.Invoke(item);
            progress.Publish();
            return;
        }

        var kind = VideoNumberParser.Parse(item.FilePath).Kind.ToContentKind();
        var nodes = BuildNodes(kind);
        var cacheMode = useCache ? VideoScrapeCacheMode.Auto : VideoScrapeCacheMode.ForceRefreshAll;

        try
        {
            var request = new VideoScrapeRequest
            {
                Number = item.Number,
                ContentKind = kind,
                FilePath = item.FilePath,
                AvailableNodes = nodes,
            };
            var results = await _scheduler.ExecuteAsync(request, cacheMode, ct);
            var aggregate = _merger.Merge(item.Number, kind, results);
            var meta = aggregate.Metadata;
            // 来源健康统计：逐源记录 Outcome，供看板展示成功率/Block 率
            foreach (var attempt in aggregate.Attempts)
                _health?.Record(attempt.SourceId, attempt.Outcome);
            var hitSources = aggregate.Attempts
                .Where(a => a.Outcome is VideoSourceOutcome.Success or VideoSourceOutcome.CacheHit)
                .Select(a => a.SourceId).Distinct().ToList();

            if (hitSources.Count == 0 || string.IsNullOrWhiteSpace(meta.Title))
            {
                var hasErrors = aggregate.Attempts.Any(a => a.Outcome is
                    VideoSourceOutcome.HttpError or VideoSourceOutcome.Blocked or VideoSourceOutcome.ParseError);
                item.ScrapeStatus = hasErrors ? Models.ScrapeStatus.Failed : Models.ScrapeStatus.NoMatch;
                item.ScrapedAt = DateTime.UtcNow;
                var failDetail = string.Join("；", aggregate.Attempts
                    .Where(a => a.Outcome is VideoSourceOutcome.HttpError or VideoSourceOutcome.Blocked
                        or VideoSourceOutcome.ParseError && !string.IsNullOrEmpty(a.Reason))
                    .Select(a => $"{a.SourceId}: {a.Reason}")
                    .Distinct());
                if (hasErrors)
                {
                    progress.RecordFailed();
                    progress.RecordItem(new VideoTaskItemResult { Number = item.Number, FileName = item.FileName, Outcome = "失败", Detail = failDetail, ElapsedMs = (int)itemSw.ElapsedMilliseconds });
                    progress.Log($"{item.Number}: 所有来源均失败");
                }
                else
                {
                    progress.RecordNoMatch();
                    progress.RecordItem(new VideoTaskItemResult { Number = item.Number, FileName = item.FileName, Outcome = "未匹配", Detail = failDetail, ElapsedMs = (int)itemSw.ElapsedMilliseconds });
                    progress.Log($"{item.Number}: 未匹配");
                }
                _applyService.SaveFailureReport(item, aggregate.Attempts);
            }
            else
            {
                VideoScrapeService.ApplyMetadata(item, meta);
                await _applyService.DownloadImagesAsync(item, meta, ct);
                if (Settings.WriteNfo) VideoScrapeService.WriteNfo(item);
                item.ScrapeStatus = Models.ScrapeStatus.Success;
                item.ScrapeSource = string.Join("+", hitSources);
                item.ScrapedAt = DateTime.UtcNow;
                progress.RecordSuccess();
                progress.RecordItem(new VideoTaskItemResult
                {
                    Number = item.Number,
                    FileName = item.FileName,
                    Outcome = "成功",
                    Sources = item.ScrapeSource,
                    Title = meta.Title,
                    Detail = $"演员{meta.Actors.Count} 标签{meta.Tags.Count}",
                    ElapsedMs = (int)itemSw.ElapsedMilliseconds,
                });
                progress.Log($"{item.Number}: 命中 {item.ScrapeSource}");
                _logger?.Info($"[ScrapeGraph] {item.Number} 成功 sources={item.ScrapeSource} actors={meta.Actors.Count} tags={meta.Tags.Count}");
                _applyService.SaveAggregateReport(item, aggregate);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.ScrapeStatus = Models.ScrapeStatus.Failed;
            progress.RecordFailed();
            progress.RecordItem(new VideoTaskItemResult { Number = item.Number, FileName = item.FileName, Outcome = "失败", Detail = ex.Message, ElapsedMs = (int)itemSw.ElapsedMilliseconds });
            progress.Log($"{item.Number}: {ex.Message}");
            _logger?.Error($"[ScrapeGraph] {item.Number} 刮削失败", ex);
        }
        _library.Update(item, deferSave: true);
        ItemChanged?.Invoke(item);
        progress.Publish();
    }

    /// <summary>
    /// 组装抓取节点：contentRoutes 里该内容类型的来源顺序 ∩ 启用的源 ∩ 已注册的源；
    /// 源配置了 Language 的生成多语言节点。路由内全被过滤时退回全部已启用源，避免空转。
    /// </summary>
    private List<VideoFetchNode> BuildNodes(VideoContentKind kind)
    {
        var nodes = new List<VideoFetchNode>();
        var candidates = _advanced.GetSourcesForKind(kind);
        if (candidates.Count == 0)
            candidates = _registry.Sources.Select(s => s.Id).ToList();

        foreach (var sourceId in candidates)
        {
            if (!_advanced.IsSourceEnabled(sourceId)) continue;
            var source = _registry.Get(sourceId);
            if (source is null) continue;

            var cfg = _advanced.SourceConfigs.TryGetValue(sourceId, out var sourceConfig) ? sourceConfig : null;
            var language = !string.IsNullOrWhiteSpace(cfg?.Language) ? cfg!.Language : null;
            nodes.Add(new VideoFetchNode { SourceId = sourceId, Language = language, Tier = cfg?.Tier ?? 2 });
        }

        if (nodes.Count == 0)
        {
            foreach (var source in _registry.Sources)
            {
                if (!_advanced.IsSourceEnabled(source.Id)) continue;
                var tier = _advanced.SourceConfigs.TryGetValue(source.Id, out var cfg2) ? cfg2.Tier : 2;
                nodes.Add(new VideoFetchNode { SourceId = source.Id, Tier = tier });
            }
        }
        return nodes;
    }
}
