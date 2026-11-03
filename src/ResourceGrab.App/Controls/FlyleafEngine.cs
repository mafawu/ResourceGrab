using System.IO;
using FlyleafLib;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.App.Controls;

/// <summary>
/// Flyleaf 引擎引导：启动一次全局 Engine（FFmpeg 原生库目录解析 + 后台预热）。
/// FFmpeg 查找顺序：FLYLEAF_FFMPEG 环境变量 → 程序旁 ffmpeg\ → 同级 Release\ffmpeg（调试便利）。
/// 找不到时返回失败原因，播放器进入错误态并提示，而不是静默黑屏。
/// </summary>
public static class FlyleafEngine
{
    private static readonly object _lock = new();
    private static bool _started;
    private static string? _error;
    private static bool _missingFfmpeg;

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
            if (_error is not null)
            {
                // 缺 FFmpeg 是可恢复的：用户可能刚把库放进来（调试时尤其常见），
                // 重试时重新探测；其他启动错误仍按失败缓存，避免每次点击都重试。
                if (_missingFfmpeg && FindFfmpegDir() is not null)
                {
                    _error = null;
                    _missingFfmpeg = false;
                }
                else
                {
                    return false;
                }
            }
            try
            {
                var dir = FindFfmpegDir();
                if (dir is null)
                {
                    _missingFfmpeg = true;
                    _error = "缺少 FFmpeg 播放库：请把 ffmpeg 目录放到程序目录，或运行 publish.ps1";
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
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "ffmpeg"),
            baseDir,
        };

        // 开发/调试便利：dotnet build 不会把 FFmpeg 共享库复制进 Debug 输出，
        // 而 publish.ps1 会放到 bin\Release\ffmpeg。Debug 版启动时复用同级 Release 的库，
        // 避免"源码能编译、播放却报 FFmpeg 缺失"。发布包旁边有 ffmpeg 时仍优先用自己的。
        foreach (var sibling in new[] { "Release", "release", "release-standalone" })
        {
            try { candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", sibling, "ffmpeg"))); }
            catch { }
        }
        try { candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "ffmpeg"))); }
        catch { }

        foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
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
