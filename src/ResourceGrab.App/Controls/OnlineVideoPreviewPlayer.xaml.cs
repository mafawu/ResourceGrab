using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LibVLCSharp.Shared;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Common;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Services;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 在线视频预览/全片播放器（源无关）：任何源只要在 OnlineVideoDetail 里填了
/// StreamUrl（m3u8 / mp4 直链）+ Referer，即可复用此控件播放。
/// 源不感知播放器；播放失败、重试、暂停等均在本控件内闭环。
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

    /// <summary>播放该流需要的 Referer 头（经 libvlc :http-referrer 传入）。</summary>
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

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private string? _currentRelayUrl;
    private bool _playPending;
    private long _pendingSeekMs;
    private bool _pendingPause;
    private bool _handoffSeekApplied;
    private bool _inPlayback;
    private static bool _libVlcInitialized;
    private readonly ILogger? _logger = App.Services.GetService<ILogger>();

    /// <summary>控制条自动隐藏计时器：播放/暂停态 5 秒无操作后隐藏进度条与按钮。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _controlBarHideTimer = new()
    {
        Interval = TimeSpan.FromSeconds(5),
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
    }

    // ── 状态切换 ─────────────────────────────────────────────────────

    private void SetState(PlayerState state)
    {
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

    // ── 控制条自动隐藏 ──────────────────────────────────────────────────

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
        // 与 StartPlayback 用同一个 IsHlsUrl 判断，避免带 query 的地址在两处结论不一致
        PlayHost.ToolTip = System.Uri.TryCreate(StreamUrl, UriKind.Absolute, out var u) && IsHlsUrl(u)
            ? "播放影片" : "播放预览";
    }

    // ── 播放控制 ─────────────────────────────────────────────────────

    /// <summary>停止播放并回到海报态（切换详情、关闭侧栏、切页时调用）。保留 LibVLC 实例供下次播放。</summary>
    public void Stop()
    {
        _playPending = false;
        try { _mediaPlayer?.Stop(); } catch { /* 已停止/未初始化 */ }
        if (_currentRelayUrl is not null)
        {
            HlsLocalRelay.Instance.Unregister(_currentRelayUrl);
            _currentRelayUrl = null;
        }
        SetState(PlayerState.Idle);
    }

    /// <summary>公开播放入口：外部（详情自动起播、独立播放窗口）设好 StreamUrl 后调用。
    /// HWND 未就绪时由 TryPlay 内部置 _playPending，宿主加载后自动起播。</summary>
    public void Play() => TryPlay();

    /// <summary>当前播放位置（毫秒）；未起播返回 0。</summary>
    public long CurrentPositionMs => _mediaPlayer?.Time ?? 0;

    /// <summary>是否正在播放。</summary>
    public bool IsPlaying => _mediaPlayer?.State == VLCState.Playing;

    /// <summary>是否处于暂停态。</summary>
    public bool IsPaused => _mediaPlayer?.State == VLCState.Paused;

    /// <summary>
    /// 接力续播（小窗放大 / 回迁）：从指定位置起播，paused=true 时起播后立即暂停。
    /// 主界面与小窗是两个播放器实例（LibVLCSharp.WPF 的 VideoView 不支持跨窗口迁移），
    /// 交接时用此方法把位置与播放状态传给对方，从同一位置继续播。
    /// 注意：定位不用 VLC 的 :start-time 输入选项（其单位是秒，传毫秒会错位到流末尾），
    /// 而是起播后经 <see cref="ApplyHandoffSeek"/> 用 MediaPlayer.Time（毫秒）精确 seek。
    /// </summary>
    public void PlayFrom(long positionMs, bool paused)
    {
        _pendingSeekMs = Math.Max(0, positionMs);
        _pendingPause = paused;
        _handoffSeekApplied = false;
        TryPlay();
    }

    /// <summary>
    /// 接力续播的精确 seek：等时长已知（LengthChanged）后把播放定位到交接位置。
    /// 起播即 seek 可能被 LengthChanged 重设时长干扰，故挂在 Playing/LengthChanged 上幂等执行。
    /// </summary>
    private void ApplyHandoffSeek()
    {
        if (_handoffSeekApplied) return;
        if (_pendingSeekMs <= 0) { _handoffSeekApplied = true; return; }
        if (_durationMs <= 0) return; // 时长未知（直播/未到 LengthChanged），等下一次
        _handoffSeekApplied = true;
        var target = Math.Min(_pendingSeekMs, _durationMs - 1);
        _pendingSeekMs = 0;
        _logger?.Info($"[OnlineVideoPreviewPlayer] 接力续播定位：{target}ms");
        try
        {
            _mediaPlayer!.Time = target;
            _sliderUpdating = true;
            if (_durationMs > 0) SeekSlider.Value = Math.Min(target, _durationMs);
            _sliderUpdating = false;
            CurrentTimeText.Text = FormatTime(target);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[OnlineVideoPreviewPlayer] 接力续播 seek 失败: {ex.Message}");
        }
        if (_pendingPause)
        {
            _pendingPause = false;
            _mediaPlayer?.Pause();
            SetState(PlayerState.Paused);
        }
    }

    private void PlayHost_Click(object sender, MouseButtonEventArgs e) => TryPlay();

    private void Retry_Click(object sender, RoutedEventArgs e) => TryPlay();

    private void TryPlay()
    {
        _logger?.Info($"[OnlineVideoPreviewPlayer] TryPlay: StreamUrl={(string.IsNullOrEmpty(StreamUrl) ? "(空)" : StreamUrl)}, VideoHost.IsLoaded={VideoHost.IsLoaded}");
        if (string.IsNullOrEmpty(StreamUrl)) return;
        if (!VideoHost.IsLoaded)
        {
            // VideoView 的 HWND 尚未创建：先亮出宿主，Loaded 后再起播。
            // 注意 Visibility 折叠不会卸载元素，只有首次显示才走这条路。
            _playPending = true;
            SetState(PlayerState.Loading);
            _logger?.Info("[OnlineVideoPreviewPlayer] VideoHost 尚未加载，等待 Loaded 后再起播");
            return;
        }
        StartPlayback();
    }

    private void VideoHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (_playPending)
        {
            _playPending = false;
            StartPlayback();
        }
    }

    private void StartPlayback()
    {
        if (string.IsNullOrEmpty(StreamUrl)) return;
        _logger?.Info($"[OnlineVideoPreviewPlayer] StartPlayback: StreamUrl={StreamUrl}, Referer={(string.IsNullOrEmpty(Referer) ? "(空)" : Referer)}");
        EnsurePlayer();
        if (_mediaPlayer is null || _libVlc is null)
        {
            _logger?.Error("[OnlineVideoPreviewPlayer] StartPlayback 中止：MediaPlayer 或 LibVLC 未初始化（详见上方初始化日志）");
            return;
        }

        _currentMedia?.Dispose();
        // 远程 URL 必须用 Uri 重载；字符串重载会被 LibVLCSharp 当作本地文件路径，
        // 导致 VLC 去本地找 "bin\Release\https:\host\..." 而打不开。
        if (!System.Uri.TryCreate(StreamUrl, UriKind.Absolute, out var mediaUri))
        {
            _logger?.Error($"[OnlineVideoPreviewPlayer] 无效的流地址（无法构造 Uri）: {StreamUrl}");
            SetState(PlayerState.Error);
            return;
        }

        // 切换/重试时清理上一次可能残留的中继注册，避免映射堆积
        if (_currentRelayUrl is not null)
        {
            HlsLocalRelay.Instance.Unregister(_currentRelayUrl);
            _currentRelayUrl = null;
        }

        Media media;
        if (IsHlsUrl(mediaUri))
        {
            // HLS：LibVLC 3.x 的 adaptive 模块在拉子播放列表/分片时不会继承 media 级的
            // :http-referrer，强制校验 Referer 的 CDN（如 surrit.com）会对子请求返回 403，
            // 表现为 "Failed to create demuxer"。改用本地中继代发请求并注入 Referer/代理。
            try
            {
                var proxy = GetProxyUrl();
                _currentRelayUrl = HlsLocalRelay.Instance.Register(mediaUri, Referer, proxy);
                _logger?.Info($"[OnlineVideoPreviewPlayer] HLS 走本地中继: {_currentRelayUrl} (原 {StreamUrl})");
                media = new Media(_libVlc, new Uri(_currentRelayUrl));
                media.AddOption(":network-caching=1500");
                // 强制解复用器解析本地 m3u8：VLC 对 localhost http 的 .m3u8 会因 Content-Type/嗅探
                // 误选 mjpeg/avcodec 等解复用器而报 "cannot peek"，显式指定可避免。
                // 注意 VLC 3.x 的 HLS 由 adaptive 模块处理，模块名是 "adaptive" 而非 "hls"，
                // 写 :demux=hls 会因找不到该模块直接报 "Your input can't be opened"。
                media.AddOption(":demux=adaptive");
            }
            catch (Exception ex)
            {
                _logger?.Warn($"[OnlineVideoPreviewPlayer] 本地中继不可用，回退直连 HLS: {ex.Message}");
                _currentRelayUrl = null;
                media = BuildDirectMedia(_libVlc, mediaUri);
            }
        }
        else
        {
            media = BuildDirectMedia(_libVlc, mediaUri);
        }

        _currentMedia = media;
        _logger?.Info($"[OnlineVideoPreviewPlayer] 已创建 Media（State={media.State}），准备 Play。VideoHost.MediaPlayer 已绑定={ReferenceEquals(VideoHost.MediaPlayer, _mediaPlayer)}");

        VideoHost.MediaPlayer = _mediaPlayer;
        SetState(PlayerState.Loading);
        _mediaPlayer.Play(media);
        _logger?.Info($"[OnlineVideoPreviewPlayer] 已调用 Play()。PlayerState={_mediaPlayer.State}");
    }

    /// <summary>
    /// 是否 HLS 流。用 Uri 的 AbsolutePath 判断而非字符串后缀：带 query string 的地址
    /// （如 ?token=xxx）字符串不以 .m3u8 结尾，用后缀判断会漏，进而错误地走直连分支。
    /// </summary>
    private static bool IsHlsUrl(Uri uri) =>
        uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    /// <summary>直连式 Media（mp4 直链或 HLS 回退）：直接把 Referer/UA/代理传给 LibVLC。</summary>
    private Media BuildDirectMedia(LibVLC libVlc, Uri mediaUri)
    {
        var media = new Media(libVlc, mediaUri);
        if (!string.IsNullOrEmpty(Referer))
            media.AddOption($":http-referrer={Referer}");
        media.AddOption(":network-caching=1500");
        // 部分在线源会拦截 VLC 默认 UA，带浏览器 UA 提高兼容性
        media.AddOption(":http-user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        // 在线源通常经代理访问外网；LibVLC 默认不走应用/系统代理，必须显式传入，
        // 否则 VLC 直连被墙会立刻 EncounteredError。
        var proxy = GetProxyUrl();
        if (!string.IsNullOrEmpty(proxy))
            media.AddOption($":http-proxy={proxy}");
        _logger?.Info($"[OnlineVideoPreviewPlayer] 使用代理: {(string.IsNullOrEmpty(proxy) ? "(无)" : proxy)}");
        return media;
    }

    private void PauseToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is null) return;
        if (_mediaPlayer.State == VLCState.Playing)
        {
            _mediaPlayer.Pause();
            SetState(PlayerState.Paused); // 暂停态也常驻控制条，可继续或停止
        }
        else
        {
            _mediaPlayer.Play();
            PauseToggleButton.Content = "❚❚";
        }
    }

    /// <summary>停止播放并回到海报态（控制条"⏹"按钮）。</summary>
    private void StopButton_Click(object sender, RoutedEventArgs e) => Stop();

    // ── 进度条 / 跳转 ────────────────────────────────────────────────

    private bool _sliderUpdating;
    private bool _seekDragging;
    private long _durationMs;

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
        if (_sliderUpdating || _mediaPlayer is null) return;
        if (_seekDragging)
        {
            // 拖动中只刷新时间预览，松手后一次性 seek，避免连续跳转卡顿
            CurrentTimeText.Text = FormatTime((long)e.NewValue);
            return;
        }
        SeekTo((long)e.NewValue);
    }

    private void SeekSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        => _seekDragging = true;

    private void SeekSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _seekDragging = false;
        SeekTo((long)SeekSlider.Value);
    }

    private void SeekTo(long positionMs)
    {
        if (_mediaPlayer is null || _durationMs <= 0 || positionMs <= 0) return;
        _mediaPlayer.Time = Math.Min(positionMs, _durationMs - 1);
        CurrentTimeText.Text = FormatTime(positionMs);
    }

    // ── LibVLC 生命周期 ──────────────────────────────────────────────

    private void EnsurePlayer()
    {
        if (_mediaPlayer is not null) return;
        try
        {
            if (!_libVlcInitialized)
            {
                var libvlcDir = FindLibVlcDirectory();
                if (libvlcDir is null)
                    throw new FileNotFoundException(
                        "找不到 LibVLC 原生库（libvlc.dll）。请确认 libvlc 目录（含 win-x64/win-x86 子目录）随程序一起部署。");
                _logger?.Info($"[OnlineVideoPreviewPlayer] 使用 LibVLC 目录: {libvlcDir}");
                LibVLCSharp.Shared.Core.Initialize(libvlcDir);
                _libVlcInitialized = true;
            }
            _libVlc = new LibVLC();
            _libVlc.Log += (_, e) =>
            {
                if (e.Level is LibVLCSharp.Shared.LogLevel.Warning or LibVLCSharp.Shared.LogLevel.Error)
                    _logger?.Info($"[VLC:{e.Level}:{e.Module}] {e.Message}");
            };
            _mediaPlayer = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true };
            // VLC 事件在线程池触发，一律切回 UI 线程改状态
            _mediaPlayer.Playing += (_, _) => Dispatcher.InvokeAsync(() =>
            {
                _logger?.Info($"[OnlineVideoPreviewPlayer] VLC Playing 事件。PlayerState={_mediaPlayer.State}");
                SetState(PlayerState.Playing);
                ApplyHandoffSeek();
            });
            _mediaPlayer.EncounteredError += (_, _) => Dispatcher.InvokeAsync(() =>
            {
                _logger?.Error($"[OnlineVideoPreviewPlayer] VLC EncounteredError。PlayerState={_mediaPlayer.State}, MediaState={_currentMedia?.State}");
                SetState(PlayerState.Error);
            });
            _mediaPlayer.EndReached += (_, _) => Dispatcher.InvokeAsync(() => SetState(PlayerState.Idle));
            _mediaPlayer.LengthChanged += (_, e) => Dispatcher.InvokeAsync(() =>
            {
                // 时长已知（点播）才显示进度滑条与总时长；直播流 length<=0 自动隐藏
                _durationMs = e.Length;
                if (_durationMs > 0 && _mediaPlayer.State is VLCState.Playing or VLCState.Paused)
                {
                    SeekSlider.Maximum = _durationMs; // 滑条上限=时长（毫秒），否则进度会被钳在旧上限
                    DurationText.Text = FormatTime(_durationMs);
                }
                SeekSlider.Visibility = _durationMs > 0 ? Visibility.Visible : Visibility.Collapsed;
                DurationText.Visibility = _durationMs > 0 ? Visibility.Visible : Visibility.Collapsed;
                // 时长就绪后执行接力续播定位（起播时可能还拿不到时长，这里兜底）
                ApplyHandoffSeek();
            });
            _mediaPlayer.TimeChanged += (_, e) => Dispatcher.InvokeAsync(() =>
            {
                if (_seekDragging) return;
                _sliderUpdating = true;
                if (_durationMs > 0) SeekSlider.Value = Math.Min(e.Time, _durationMs);
                _sliderUpdating = false;
                CurrentTimeText.Text = FormatTime(e.Time);
            });
        }
        catch (Exception ex)
        {
            _logger?.Error("[OnlineVideoPreviewPlayer] LibVLC 初始化失败", ex);
            Dispatcher.Invoke(() => SetState(PlayerState.Error));
        }
    }

    /// <summary>
    /// 定位 LibVLC 原生库所在目录。VideoLAN.LibVLC.Windows 把原生库放在
    /// libvlc/win-x64（或 win-x86）子目录，而 LibVLCSharp 默认只在 exe 同级目录找
    /// libvlc.dll，所以必须显式传入目录。依次尝试常见部署位置，按当前进程架构优先。
    /// </summary>
    private static string? FindLibVlcDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        string rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? RuntimeInformation.OSArchitecture switch
            {
                Architecture.X86 => "win-x86",
                Architecture.Arm64 => "win-arm64",
                _ => "win-x64",
            }
            : "win-x64";

        var candidates = new List<string>
        {
            Path.Combine(baseDir, "libvlc", rid),
            Path.Combine(baseDir, "libvlc", "win-x64"),
            Path.Combine(baseDir, "libvlc", "win-x86"),
            Path.Combine(baseDir, "libvlc"),
            baseDir,
        };

        foreach (var dir in candidates)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "libvlc.dll")))
                    return dir;
            }
            catch
            {
                // 忽略无权限/路径非法，继续尝试下一个候选
            }
        }
        return null;
    }

    private void DisposePlayer()
    {
        _playPending = false;
        _controlBarHideTimer.Stop();
        try { _mediaPlayer?.Stop(); } catch { }
        if (_currentRelayUrl is not null)
        {
            HlsLocalRelay.Instance.Unregister(_currentRelayUrl);
            _currentRelayUrl = null;
        }
        VideoHost.MediaPlayer = null;
        _currentMedia?.Dispose();
        _currentMedia = null;
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
        _libVlc?.Dispose();
        _libVlc = null;
    }

    /// <summary>
    /// 从配置读取应用代理（VideoScraping.Proxy），归一化为 http(s):// 形式。
    /// LibVLC 默认不走应用/系统代理，播放在线流必须显式通过 :http-proxy 传入。
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
