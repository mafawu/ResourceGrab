using System.IO;
using FlyleafLib;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.App.Controls;

/// <summary>
/// Flyleaf 引擎引导：启动一次全局 Engine（FFmpeg 原生库目录解析 + 后台预热）。
/// FFmpeg 查找顺序：程序旁 ffmpeg\（发布包自带）→ FLYLEAF_FFMPEG 环境变量 → BaseDirectory\ffmpeg。
/// 找不到时返回失败原因，播放器进入错误态并提示，而不是静默黑屏。
/// </summary>
public static class FlyleafEngine
{
    private static readonly object _lock = new();
    private static bool _started;
    private static string? _error;

    public static bool IsReady
    {
        get { lock (_lock) return _started; }
    }

    public static string? StartupError
    {
        get { lock (_lock) return _error; }
    }

    /// <summary>后台预热（App 启动时调用）：FFmpeg 动态库加载首次很慢，不能留到点播放时等。</summary>
    public static Task PreWarmAsync(ILogger? logger) => Task.Run(() => EnsureStarted(logger));

    /// <summary>确保 Engine 已启动；已启动直接返回 true。</summary>
    public static bool EnsureStarted(ILogger? logger)
    {
        lock (_lock)
        {
            if (_started) return true;
            if (_error is not null) return false;
            try
            {
                var dir = FindFfmpegDir();
                if (dir is null)
                {
                    _error = "找不到 FFmpeg 共享库（exe 旁应有 ffmpeg\\ 目录，含 avformat-*.dll）";
                    logger?.Error("[Flyleaf] " + _error);
                    return false;
                }
                logger?.Info($"[Flyleaf] FFmpeg 目录: {dir}");
                Engine.Start(new EngineConfig()
                {
                    FFmpegPath = dir,
                    UIRefresh = true,
                    UIRefreshInterval = 250,
                });
                _started = true;
                logger?.Info("[Flyleaf] Engine 已启动");
                return true;
            }
            catch (Exception ex)
            {
                _error = $"Flyleaf Engine 启动失败：{ex.Message}";
                logger?.Error("[Flyleaf] " + _error, ex);
                return false;
            }
        }
    }

    private static string? FindFfmpegDir()
    {
        var env = Environment.GetEnvironmentVariable("FLYLEAF_FFMPEG");
        if (!string.IsNullOrEmpty(env) && HasFfmpeg(env)) return env;
        var baseDir = AppContext.BaseDirectory;
        foreach (var dir in new[] { Path.Combine(baseDir, "ffmpeg"), baseDir })
        {
            try
            {
                if (HasFfmpeg(dir)) return dir;
            }
            catch { }
        }
        return null;
    }

    private static bool HasFfmpeg(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                && Directory.GetFiles(dir, "avformat-*.dll").Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
