using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.Core.Services.VideoScrape;

// ---------------------------------------------------------------------------
// 阶段3: 来源健康统计。
// 图引擎每完成一次聚合就把逐源 Outcome 记入此服务；看板据此展示每个源的
// 成功/未匹配/被拦/网络失败计数，Blocked 高的源提示"需要代理/指纹"。
// 内存滚动窗口 + 去抖落盘，重启后仍可读最近状态。
// ---------------------------------------------------------------------------

public sealed class VideoSourceHealthService
{
    public sealed class SourceHealth
    {
        [JsonPropertyName("sourceId")] public string SourceId { get; set; } = "";
        [JsonPropertyName("success")] public int Success { get; set; }
        [JsonPropertyName("noMatch")] public int NoMatch { get; set; }
        [JsonPropertyName("blocked")] public int Blocked { get; set; }
        [JsonPropertyName("httpError")] public int HttpError { get; set; }
        [JsonPropertyName("other")] public int Other { get; set; }
        [JsonPropertyName("lastAt")] public DateTimeOffset LastAt { get; set; }

        public int Total => Success + NoMatch + Blocked + HttpError + Other;
    }

    private sealed class HealthFile
    {
        [JsonPropertyName("sources")] public List<SourceHealth> Sources { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _path;
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, SourceHealth> _bySource = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _flushTimer;

    public VideoSourceHealthService(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger;
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var file = JsonSerializer.Deserialize<HealthFile>(File.ReadAllText(_path), JsonOpts);
            if (file?.Sources is null) return;
            foreach (var health in file.Sources)
                _bySource[health.SourceId] = health;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[Health] 加载来源健康数据失败: {ex.Message}");
        }
    }

    /// <summary>记录一次来源尝试结果。</summary>
    public void Record(string sourceId, VideoSourceOutcome outcome)
    {
        if (string.IsNullOrEmpty(sourceId) || outcome is VideoSourceOutcome.CacheHit) return;

        lock (_lock)
        {
            var health = _bySource.GetOrAdd(sourceId, _ => new SourceHealth { SourceId = sourceId });
            switch (outcome)
            {
                case VideoSourceOutcome.Success: health.Success++; break;
                case VideoSourceOutcome.NoMatch: health.NoMatch++; break;
                case VideoSourceOutcome.Blocked: health.Blocked++; break;
                case VideoSourceOutcome.HttpError: health.HttpError++; break;
                default: health.Other++; break;
            }
            health.LastAt = DateTimeOffset.UtcNow;
        }
        ScheduleFlush();
    }

    /// <summary>看板展示用快照，按尝试次数倒序。</summary>
    public IReadOnlyList<SourceHealth> GetSnapshot()
    {
        lock (_lock)
        {
            return _bySource.Values
                .OrderByDescending(s => s.Total)
                .ToList();
        }
    }

    private void ScheduleFlush()
    {
        _flushTimer?.Dispose();
        _flushTimer = new Timer(_ => Flush(), null, 3000, System.Threading.Timeout.Infinite);
    }

    private void Flush()
    {
        try
        {
            HealthFile file;
            lock (_lock)
            {
                if (_bySource.Count == 0) return;
                file = new HealthFile { Sources = _bySource.Values.ToList() };
            }
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[Health] 保存来源健康数据失败: {ex.Message}");
        }
    }
}
