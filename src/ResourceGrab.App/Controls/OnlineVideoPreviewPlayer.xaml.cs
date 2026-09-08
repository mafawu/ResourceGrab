using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Common;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Services.VideoScrape;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 在线视频预览/全片播放器（源无关）：吃 OnlineVideoDetail 的 StreamUrl + Referer 契约。
/// 基于 Flyleaf（FFmpeg/DirectComposition）：无空域问题，鼠标事件/滚动/裁剪全部走 WPF；
/// Referer/UA 经 Demuxer.FormatOpt 直接透传给每个分片请求，不再需要 HLS 本地中继。
/// 对外接口与旧 VLC 版一致，VideoView/Popout 零改动。
/// </summary>
public partial class OnlineVideoPreviewPlayer : UserControl
{
    public static readonly DependencyProperty StreamUrlProperty = DependencyProperty.Register(
        nameof(StreamUrl), typeof(string), typeof(OnlineVideoPreviewPlayer),
        new PropertyMetadata("", static (d, _) => ((OnlineVideoPreviewPlayer)d).UpdatePlayButton()));

    public static readonly DependencyProperty RefererProperty = DependencyProperty.Register(
        nameof(Referer), typeof(string), typeof(OnlineVideoPreviewPlayer), new PropertyMetadata(""));

    public static readonly DependencyProperty PosterUrlProperty = DependencyProperty.Register(
        nameof(PosterUrl), typeof(string), typeof(OnlineVideoPreviewPlayer),
        new PropertyMetadata("", static (d, _) => ((OnlineVideoPreviewPlayer)d).UpdatePoster()));

    public static readonly DependencyProperty ShowPlayButtonProperty = DependencyProperty.Register(
        nameof(ShowPlayButton), typeof(bool), typeof(OnlineVideoPreviewPlayer),
        new PropertyMetadata(false, static (d, _) => ((OnlineVideoPreviewPlayer)d).UpdatePlayButton()));

    /// <summary>可播放流地址（HLS m3u8 或 mp4 直链）；空表示无流可播。</summary>
    public string StreamUrl
    {
        get => (string)GetValue(StreamUrlProperty);
        set => SetValue(StreamUrlProperty, value);
    }

    /// <summary>播放该流需要的 Referer 头（经 Demuxer headers 透传给 playlist 与每个分片）。</summary>
    public string Referer
    {
        get => (string)GetValue(RefererProperty);
        set => SetValue(RefererProperty, value);
    }

    /// <summary>空闲态海报图（http 或本地路径，经 ImageLoader 加载）。</summary>
    public string PosterUrl
    {
        get => (string)GetValue(PosterUrlProperty);
        set => SetValue(PosterUrlProperty, value);
    }

    /// <summary>是否展示播放入口（详情有流时 true；纯词条详情 false，只作封面展示）。</summary>
    public bool ShowPlayButton
    {
        get => (bool)GetValue(ShowPlayButtonProperty);
        set => SetValue(ShowPlayButtonProperty, value);
    }

    private enum PlayerState { Idle, Loading, Playing, Paused, Error }

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private Player? _player;
    private PlayerState _state = PlayerState.Idle;
    private bool _pausedGuess;
    private long _pendingSeekMs;
    private bool _pendingPause;
    private bool _handoffSeekApplied;
    private bool _inPlayback;
    private readonly ILogger? _logger = App.Services.GetService<ILogger>();

    /// <summary>控制条自动隐藏计时器：播放/暂停态 5 秒无操作后隐藏进度条与按钮。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _controlBarHideTimer = new()
    {
        Interval = TimeSpan.FromSeconds(5),
    };

    /// <summary>播放状态同步计时器：500ms 读一次 CurTime/Duration/IsPlaying，驱动进度条与状态机。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _uiSyncTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(500),
    };

    public OnlineVideoPreviewPlayer()
    {
        InitializeComponent();
        Unloaded += (_, _) => DisposePlayer();
        _controlBarHideTimer.Tick += (_, _) =>
        {
            _controlBarHideTimer.Stop();
            if (_inPlayback) ControlBar.Visibility = Visibility.Collapsed;
        };
        _uiSyncTimer.Tick += (_, _) => SyncPlaybackUi();
    }

    // ── 状态切换 ─────────────────────────────────────────────────────

    private void SetState(PlayerState state)
    {
        _state = state;
        VideoHost.Visibility = state == PlayerState.Idle ? Visibility.Collapsed : Visibility.Visible;
        PosterLayer.Visibility = state == PlayerState.Idle ? Visibility.Visible : Visibility.Collapsed;
        LoadingLayer.Visibility = state == PlayerState.Loading ? Visibility.Visible : Visibility.Collapsed;
        ErrorLayer.Visibility = state == PlayerState.Error ? Visibility.Visible : Visibility.Collapsed;
        _inPlayback = state is PlayerState.Playing or PlayerState.Paused;
        if (_inPlayback)
        {
            // 进入播放/暂停先显示控制条，5 秒无操作自动隐藏（鼠标进入视频区域重新显示）
            ControlBar.Visibility = Visibility.Visible;
            RestartControlBarTimer();
        }
        else
        {
            _controlBarHideTimer.Stop();
            ControlBar.Visibility = Visibility.Collapsed;
        }
        PauseToggleButton.Content = state == PlayerState.Paused ? "►" : "❚❚";
        if (state == PlayerState.Loading) ResetSeekBar();
    }

    // ── 控制条自动隐藏（DirectComposition 无空域，WPF 鼠标事件正常送达） ──

    private void ShowControlBarTemporarily()
    {
        if (!_inPlayback) return;
        ControlBar.Visibility = Visibility.Visible;
        RestartControlBarTimer();
    }

    private void RestartControlBarTimer()
    {
        _controlBarHideTimer.Stop();
        _controlBarHideTimer.Start();
    }

    private void PlayerRoot_MouseEnter(object sender, MouseEventArgs e) => ShowControlBarTemporarily();

    private void PlayerRoot_MouseMove(object sender, MouseEventArgs e) => ShowControlBarTemporarily();

    private void PlayerRoot_MouseLeave(object sender, MouseEventArgs e)
    {
        // 鼠标离开视频区域：5 秒后隐藏控制条
        if (_inPlayback) RestartControlBarTimer();
    }

    /// <summary>鼠标在控制条上操作时保持显示并重置计时。</summary>
    private void ControlBar_MouseMove(object sender, MouseEventArgs e) => ShowControlBarTemporarily();

    private void UpdatePoster()
    {
        var poster = PosterUrl;
        var isUrl = poster.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(poster) || (!isUrl && !File.Exists(poster)))
        {
            PosterImage.Source = null;
            return;
        }
        ImageLoader.SetSource(PosterImage, poster);
    }

    private void UpdatePlayButton()
    {
        PlayHost.Visibility = ShowPlayButton ? Visibility.Visible : Visibility.Collapsed;
        PlayHost.ToolTip = StreamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            ? "播放影片" : "播放预览";
    }

    // ── 播放控制 ─────────────────────────────────────────────────────

    /// <summary>停止播放并回到海报态（切换详情、关闭侧栏、切页时调用）。</summary>
    public void Stop()
    {
        _openGeneration++; // 作废在途打开
        _pausedGuess = false;
        _pendingSeekMs = 0;
        _pendingPause = false;
        try { _player?.Stop(); } catch { /* 已停止/未初始化 */ }
        _uiSyncTimer.Stop();
        SetState(PlayerState.Idle);
    }

    /// <summary>公开播入口：外部（详情自动起播、独立播放窗口）设好 StreamUrl 后调用。</summary>
    public void Play() => TryPlay();

    /// <summary>当前播放位置（毫秒）；未起播返回 0。</summary>
    public long CurrentPositionMs
    {
        get
        {
            try { return (_player?.CurTime ?? 0) / 10000; }
            catch { return 0; }
        }
    }

    /// <summary>是否正在播放。</summary>
    public bool IsPlaying
    {
        get
        {
            try { return _player?.IsPlaying == true; }
            catch { return false; }
        }
    }

    /// <summary>是否处于暂停态（本地跟踪：Flyleaf 未暴露 IsPaused，以用户暂停意图为准）。</summary>
    public bool IsPaused => _pausedGuess && !IsPlaying && (_player?.Video.IsOpened == true);

    /// <summary>
    /// 接力续播（小窗放大 / 回迁）：从指定位置起播，paused=true 时起播后立即暂停。
    /// 主界面与小窗是两个播放器实例，交接时用此方法把位置与播放状态传给对方，
    /// 起播时长就绪后经 ApplyHandoffSeek 用 CurTime（毫秒）精确 seek。
    /// </summary>
    public void PlayFrom(long positionMs, bool paused)
    {
        _pendingSeekMs = Math.Max(0, positionMs);
        _pendingPause = paused;
        _handoffSeekApplied = false;
        TryPlay();
    }

    /// <summary>
    /// 接力续播的精确 seek：等时长已知后把播放定位到交接位置（幂等）。
    /// </summary>
    private void ApplyHandoffSeek()
    {
        if (_handoffSeekApplied) return;
        if (_pendingSeekMs <= 0) { _handoffSeekApplied = true; return; }
        if (_durationMs <= 0 || _player is null) return; // 时长未知，等下一次
        _handoffSeekApplied = true;
        var target = Math.Min(_pendingSeekMs, _durationMs - 1);
        _pendingSeekMs = 0;
        _logger?.Info($"[FlyPlayer] 接力续播定位：{target}ms");
        try
        {
            _player.CurTime = target * 10000;
            _sliderUpdating = true;
            if (_durationMs > 0) SeekSlider.Value = Math.Min(target, _durationMs);
            _sliderUpdating = false;
            CurrentTimeText.Text = FormatTime(target);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[FlyPlayer] 接力续播 seek 失败: {ex.Message}");
        }
        if (_pendingPause)
        {
            _pendingPause = false;
            PauseInternal();
        }
    }

    private void PlayHost_Click(object sender, MouseButtonEventArgs e) => TryPlay();

    private void Retry_Click(object sender, RoutedEventArgs e) => TryPlay();

    private void TryPlay()
    {
        var generation = ++_openGeneration;
        _logger?.Info($"[FlyPlayer] TryPlay: StreamUrl={(string.IsNullOrEmpty(StreamUrl) ? "(空)" : StreamUrl)}");
        if (string.IsNullOrEmpty(StreamUrl)) return;
        if (!Uri.TryCreate(StreamUrl, UriKind.Absolute, out var streamUri))
        {
            _logger?.Error($"[FlyPlayer] 无效的流地址（无法构造 Uri）: {StreamUrl}");
            SetState(PlayerState.Error);
            return;
        }
        if (!FlyleafEngine.EnsureStarted(_logger))
        {
            SetState(PlayerState.Error);
            return;
        }
        EnsurePlayer();
        if (_player is null)
        {
            _logger?.Error("[FlyPlayer] 播放器实例创建失败");
            SetState(PlayerState.Error);
            return;
        }
        ApplyRequestHeaders();
        VideoHost.Player = _player;
        SetState(PlayerState.Loading);
        _openStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _uiSyncTimer.Start();
        _ = OpenAndPlayAsync(generation, streamUri);
    }

    /// <summary>
    /// 打开并起播：HLS 主 playlist 先探一次档位，直接打开最高档，跳过播放器逐档探测的十几秒；
    /// 探测失败/单档/MP4 则用原地址。中途被新打开或 Stop 超过的代际直接丢弃。
    /// </summary>
    private async Task OpenAndPlayAsync(int generation, Uri streamUri)
    {
        var openUrl = StreamUrl;
        if (VideoDownloadService.IsHls(StreamUrl))
        {
            var fresh = await ProbeBestVariantAsync();
            if (generation != _openGeneration) return; // 已被超车
            if (fresh is not null) openUrl = fresh;
        }
        if (generation != _openGeneration || _player is null) return;
        _activeOpenGeneration = generation;
        try
        {
            _player.OpenAsync(openUrl);
            _player.Play();
            _logger?.Info($"[FlyPlayer] 已调用 OpenAsync + Play() {(openUrl == StreamUrl ? "（主 playlist）" : "（直连最高档）")}");
        }
        catch (Exception ex)
        {
            if (generation != _openGeneration) return;
            _logger?.Error("[FlyPlayer] 起播失败", ex);
            await Dispatcher.InvokeAsync(() => SetState(PlayerState.Error));
        }
    }

    /// <summary>探测最高档位地址；失败返回 null（调用方用主 playlist）。</summary>
    private async Task<string?> ProbeBestVariantAsync()
    {
        try
        {
            var svc = App.Services.GetService<VideoDownloadService>();
            if (svc is null) return null;
            var proxy = GetProxyUrl();
            var variants = await svc.ProbeVariantsAsync(StreamUrl, Referer, proxy, "");
            var best = variants.FirstOrDefault();
            if (best is null) return null;
            _logger?.Info($"[FlyPlayer] 直连最高档: {best.Label}");
            return best.Uri;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[FlyPlayer] 档位探测失败，用主 playlist 打开: {ex.Message}");
            return null;
        }
    }

    /// <summary>按当前 StreamUrl/Referer/代理组装 Demuxer 请求头（playlist 与每个分片请求都会带上）。</summary>
    private void ApplyRequestHeaders()
    {
        if (_player is null) return;
        try
        {
            var opt = _player.Config.Demuxer.FormatOpt;
            var headers = "";
            if (!string.IsNullOrEmpty(Referer)) headers += $"Referer: {Referer}\r\n";
            headers += $"User-Agent: {BrowserUserAgent}\r\n";
            opt["headers"] = headers;
            // 部分站点分片伪装成图片扩展名，必须放行（仅作用于本次播放）
            opt["allowed_extensions"] = "ALL";
            opt["allowed_segment_extensions"] = "ALL";
            opt["extension_picky"] = "0";
            // 流分析只取开头：TS 流结构规整，2 秒/2MB 足够定编码参数，不必全量探测
            opt["analyzeduration"] = "2000000";
            opt["probesize"] = "2097152";
            if (GetProxyUrl() is { } proxy) opt["http_proxy"] = proxy;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[FlyPlayer] 请求头组装失败（继续用默认头播放）: {ex.Message}");
        }
    }

    /// <summary>是否 HLS 流（AbsolutePath 判断，带 query 的地址也不漏）。</summary>
    private static bool IsHlsUrl(Uri uri) =>
        uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    private void EnsurePlayer()
    {
        if (_player is not null) return;
        var player = new Player(new Config());
        player.OpenCompleted += (_, e) =>
        {
            // 非当前代际（切走/重播后旧打开的回执）：直接丢弃，不刷状态
            if (_activeOpenGeneration != _openGeneration) return;
            if (!e.Success)
            {
                _logger?.Error($"[FlyPlayer] 打开失败: {e.Error}");
                Dispatcher.InvokeAsync(() =>
                {
                    if (_activeOpenGeneration == _openGeneration && _state == PlayerState.Loading)
                        SetState(PlayerState.Error);
                });
            }
            else if (_openStopwatch is not null)
            {
                _logger?.Info($"[FlyPlayer] 打开完成（解复用就绪）: {_openStopwatch.ElapsedMilliseconds}ms");
            }
        };
        player.PlaybackStopped += (_, _) =>
        {
            // Stop() 已同步切到 Idle，这里只处理自然播完
            Dispatcher.InvokeAsync(() =>
            {
                if (_state is PlayerState.Playing or PlayerState.Paused)
                    SetState(PlayerState.Idle);
            });
        };
        _player = player;
    }

    /// <summary>500ms 同步：时长/位置/播放态 → 滑条与时间文本；首个 Playing 触发接力 seek。</summary>
    private void SyncPlaybackUi()
    {
        if (_player is null) return;
        try
        {
            if (_player.Video.IsOpened && _player.Duration > 0)
            {
                _durationMs = _player.Duration / 10000;
                SeekSlider.Maximum = _durationMs;
                DurationText.Text = FormatTime(_durationMs);
                SeekSlider.Visibility = Visibility.Visible;
                DurationText.Visibility = Visibility.Visible;
            }
            if (_player.IsPlaying && _state == PlayerState.Loading)
            {
                SetState(PlayerState.Playing);
                if (_openStopwatch is not null)
                {
                    _logger?.Info($"[FlyPlayer] 首帧耗时: {_openStopwatch.ElapsedMilliseconds}ms {StreamUrl}");
                    _openStopwatch = null;
                }
            }
            if (_state is PlayerState.Playing or PlayerState.Paused)
            {
                var posMs = _player.CurTime / 10000;
                if (!_seekDragging)
                {
                    _sliderUpdating = true;
                    if (_durationMs > 0) SeekSlider.Value = Math.Min(posMs, _durationMs);
                    _sliderUpdating = false;
                    CurrentTimeText.Text = FormatTime(posMs);
                }
                ApplyHandoffSeek();
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[FlyPlayer] 状态同步失败: {ex.Message}");
        }
    }

    private void PauseInternal()
    {
        try { _player?.Pause(); } catch { }
        _pausedGuess = true;
        SetState(PlayerState.Paused);
    }

    private void PauseToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null) return;
        if (IsPlaying)
        {
            PauseInternal();
        }
        else
        {
            _pausedGuess = false;
            try { _player.Play(); } catch { }
            PauseToggleButton.Content = "❚❚";
        }
    }

    /// <summary>停止播放并回到海报态（控制条"■"按钮）。</summary>
    private void StopButton_Click(object sender, RoutedEventArgs e) => Stop();

    // ── 进度条 / 跳转 ────────────────────────────────────────────────

    private bool _sliderUpdating;
    private bool _seekDragging;
    private long _durationMs;
    /// <summary>打开耗时打点：TryPlay 起播 → 首次进入 Playing，定位初始化到底慢在哪一段。</summary>
    private System.Diagnostics.Stopwatch? _openStopwatch;
    /// <summary>打开代际：切走/重播时自增，在途的旧打开完成后直接丢弃（避免 Cancelled 误报 Error）。</summary>
    private int _openGeneration;
    /// <summary>当前这次打开所属代际（OpenAsync 前一刻取值，供 OpenCompleted 回执比对）。</summary>
    private int _activeOpenGeneration;

    private void ResetSeekBar()
    {
        _sliderUpdating = true;
        SeekSlider.Value = 0;
        _sliderUpdating = false;
        _durationMs = 0;
        CurrentTimeText.Text = "0:00";
        DurationText.Text = "--:--";
    }

    private static string FormatTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sliderUpdating || _player is null) return;
        if (_seekDragging) return; // 拖动中不实时 seek，松手时一次定位
        try { _player.CurTime = (long)e.NewValue * 10000; } catch { }
    }

    private void SeekSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        => _seekDragging = true;

    private void SeekSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _seekDragging = false;
        if (_player is null) return;
        try
        {
            _player.CurTime = (long)SeekSlider.Value * 10000;
            _handoffSeekApplied = true; // 用户手动定位优先于接力定位
        }
        catch { }
    }

    private void DisposePlayer()
    {
        _uiSyncTimer.Stop();
        _controlBarHideTimer.Stop();
        try { _player?.Stop(); } catch { }
        try { _player?.Dispose(); } catch { }
        _player = null;
        try { VideoHost.Player = null!; } catch { }
    }

    /// <summary>
    /// 从配置读取应用代理（VideoScraping.Proxy），归一化为 http(s):// 形式。
    /// 经 Demuxer http_proxy 传入，否则直连被墙会立刻失败。
    /// </summary>
    private string? GetProxyUrl()
    {
        try
        {
            var proxy = App.Services.GetService<ConfigService>()?.Current.VideoScraping?.Proxy;
            if (string.IsNullOrWhiteSpace(proxy)) return null;
            proxy = proxy!.Trim();
            if (!proxy.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !proxy.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                proxy = "http://" + proxy;
            return proxy;
        }
        catch
        {
            return null;
        }
    }
}
