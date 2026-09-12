using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Common;
using ResourceGrab.Core;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
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
        new PropertyMetadata("", static (d, _) => ((OnlineVideoPreviewPlayer)d).UpdateStreamUrl()));

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

    /// <summary>播放请求 UA：与详情抓取/下载共用统一常量（CDN 可能把签名绑定到 UA）。</summary>
    private const string BrowserUserAgent = VideoConstants.UserAgent;

    private Player? _player;
    private PlayerState _state = PlayerState.Idle;
    private long _pendingSeekMs;
    private bool _pendingPause;
    private bool _handoffSeekApplied;
    private bool _inPlayback;
    private readonly ILogger? _logger = App.Services.GetService<ILogger>();

    /// <summary>HLS 档位列表（按码率降序），每次换片只探测一次并缓存。</summary>
    private IReadOnlyList<VideoVariantInfo> _variants = Array.Empty<VideoVariantInfo>();
    /// <summary>当前使用的档位下标；-1 表示无档位（用主 playlist 原地址）。</summary>
    private int _variantIndex = -1;
    /// <summary>本次 StreamUrl 是否已探测过档位（换片时重置）。</summary>
    private bool _variantsProbed;

    /// <summary>是否处于缓冲中（Flyleaf 事件驱动，只影响提示层，不改播放状态）。</summary>
    private bool _buffering;

    private int _volume = 100;
    private bool _muted;
    /// <summary>代码内回填滑条值时抑制回写配置，避免初始化时误写。</summary>
    private bool _settingVolumeFromCode;

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
        InitVolumeFromConfig();
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
        UpdateBufferingLayer();
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
        _pendingSeekMs = 0;
        _pendingPause = false;
        _buffering = false;
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

    /// <summary>是否处于暂停态（读 Flyleaf 运行时状态；接力续播据此判断是否恢复暂停）。</summary>
    public bool IsPaused
    {
        get
        {
            try { return _player?.Status == Status.Paused; }
            catch { return false; }
        }
    }

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
        if (_pendingSeekMs > 0)
        {
            if (_durationMs <= 0 || _player is null) return; // 时长未知，等下一次
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
        }
        _handoffSeekApplied = true;
        // 位置为 0 时同样要落实暂停（切画质/接力顿在起点的场景）
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
    /// 打开并起播：HLS 主 playlist 先探一次档位（只探一次，列表缓存供画质菜单复用），
    /// 默认直接打开最高档，跳过播放器逐档探测的十几秒；探测失败/无档/MP4 则用原地址。
    /// 中途被新打开或 Stop 超过的代际直接丢弃。
    /// </summary>
    private async Task OpenAndPlayAsync(int generation, Uri streamUri)
    {
        var openUrl = StreamUrl;
        if (VideoDownloadService.IsHls(StreamUrl))
        {
            if (!_variantsProbed)
            {
                _variantsProbed = true;
                _variants = await ProbeVariantsAsync();
                if (generation != _openGeneration) return; // 已被超车
                // ParseVariants 已按码率降序，下标 0 即最高档
                _variantIndex = _variants.Count > 0 ? 0 : -1;
                UpdateQualityButton();
            }
            if (_variantIndex >= 0 && _variantIndex < _variants.Count)
                openUrl = _variants[_variantIndex].Uri;
        }
        if (generation != _openGeneration || _player is null) return;
        _activeOpenGeneration = generation;
        _activeOpenUrl = openUrl;
        var label = _variantIndex >= 0 && _variantIndex < _variants.Count
            ? $"档位 {_variants[_variantIndex].Label}"
            : "主 playlist";
        try
        {
            _player.OpenAsync(openUrl);
            _player.Play();
            _logger?.Info($"[FlyPlayer] 已调用 OpenAsync + Play()（{label}）");
        }
        catch (Exception ex)
        {
            if (generation != _openGeneration) return;
            _logger?.Error("[FlyPlayer] 起播失败", ex);
            await Dispatcher.InvokeAsync(() => SetState(PlayerState.Error));
        }
    }

    /// <summary>探测 HLS 档位列表（按码率降序）；失败返回空，调用方回退主 playlist 原地址。</summary>
    private async Task<IReadOnlyList<VideoVariantInfo>> ProbeVariantsAsync()
    {
        try
        {
            var svc = App.Services.GetService<VideoDownloadService>();
            if (svc is null) return Array.Empty<VideoVariantInfo>();
            var variants = await svc.ProbeVariantsAsync(StreamUrl, Referer, GetProxyUrl(), "");
            _logger?.Info($"[FlyPlayer] 探测到 {variants.Count} 个清晰度档位");
            return variants;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[FlyPlayer] 档位探测失败，用主 playlist 打开: {ex.Message}");
            return Array.Empty<VideoVariantInfo>();
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
            // 网络抖动容忍：瞬断/分片超时自动重连，避免一次抖动就落到错误态
            opt["reconnect"] = "1";
            opt["reconnect_streamed"] = "1";
            opt["reconnect_delay_max"] = "5";
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
        var player = new Player(new FlyleafLib.Config());
        player.OpenCompleted += (_, e) =>
        {
            // 非当前代际（切走/重播后旧打开的回执）：直接丢弃，不刷状态
            if (_activeOpenGeneration != _openGeneration) return;
            // 同一实例上重新打开（切画质/换片）时旧一次 OpenAsync 的取消回执会晚到，
            // Url 不一致即视为旧回执丢弃，否则会把已起播的新流误判成 Error
            if (!string.Equals(e.Url, _activeOpenUrl, StringComparison.Ordinal)) return;
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
        // 缓冲事件从播放线程发起（Flyleaf 明确提示不可在该回调里暂停/停止），一律切回 UI 线程
        player.BufferingStarted += (_, _) => Dispatcher.InvokeAsync(() => SetBuffering(true));
        player.BufferingCompleted += (_, _) => Dispatcher.InvokeAsync(() => SetBuffering(false));
        _player = player;
        ApplyVolume();
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
        SetState(PlayerState.Paused);
    }

    private void PauseToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null) return;
        // 加载/空闲态不接受切换，避免在未就绪时误触发 Play
        if (_state is not (PlayerState.Playing or PlayerState.Paused)) return;
        if (IsPlaying)
        {
            PauseInternal();
        }
        else
        {
            try { _player.Play(); } catch { }
            PauseToggleButton.Content = "❚❚";
            SetState(PlayerState.Playing);
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
    /// <summary>当前正在打开的地址：同一实例上重新打开时，旧一次 OpenAsync 的取消回执会晚到，
    /// 用回执里的 Url 比对即可识别并丢弃，避免把已起播的新流误判为打开失败。</summary>
    private string? _activeOpenUrl;

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

    // ── 画质切换 ─────────────────────────────────────────────────────

    /// <summary>StreamUrl 变化：作废已探测的档位，新视频要重新探测。</summary>
    private void UpdateStreamUrl()
    {
        _variants = Array.Empty<VideoVariantInfo>();
        _variantIndex = -1;
        _variantsProbed = false;
        UpdateQualityButton();
        UpdatePlayButton();
    }

    /// <summary>只有多档可切时才显示画质按钮；按钮文字为当前档位。</summary>
    private void UpdateQualityButton()
    {
        var show = _variants.Count >= 2;
        QualityButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && _variantIndex >= 0 && _variantIndex < _variants.Count)
            QualityLabel.Text = _variants[_variantIndex].Label;
    }

    /// <summary>画质菜单：列出全部档位与码率，当前档位打勾。</summary>
    private void QualityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_variants.Count < 2) return;
        var menu = new ContextMenu();
        for (var i = 0; i < _variants.Count; i++)
        {
            var variant = _variants[i];
            var item = new MenuItem
            {
                Header = variant.BandwidthBps > 0
                    ? $"{variant.Label} · {variant.BandwidthBps / 1000}kbps"
                    : variant.Label,
                IsCheckable = true,
                IsChecked = i == _variantIndex,
            };
            var index = i;
            item.Click += (_, _) => SwitchVariant(index);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = QualityButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>切档重开：保留当前位置与暂停态，用目标档位地址重新打开（复用开播流程，不重新探测）。</summary>
    private void SwitchVariant(int index)
    {
        if (index < 0 || index >= _variants.Count || index == _variantIndex) return;
        var position = CurrentPositionMs;
        var paused = _state == PlayerState.Paused;
        _variantIndex = index;
        _pendingSeekMs = Math.Max(0, position);
        _pendingPause = paused;
        _handoffSeekApplied = false;
        _logger?.Info($"[FlyPlayer] 切换画质 → {_variants[index].Label}（{position}ms, paused={paused}）");
        TryPlay();
    }

    // ── 音量 / 静音 ──────────────────────────────────────────────────

    /// <summary>首次显示时从配置恢复音量与静音（播放器实例可能还没创建，先只刷图标）。</summary>
    private void InitVolumeFromConfig()
    {
        var settings = TryGetPlaybackSettings();
        _volume = Math.Clamp(settings?.Volume ?? 100, 0, 100);
        _muted = settings?.Muted ?? false;
        _settingVolumeFromCode = true;
        VolumeSlider.Value = _volume;
        _settingVolumeFromCode = false;
        ApplyVolume();
    }

    private VideoPlaybackSettings? TryGetPlaybackSettings()
    {
        try { return App.Services.GetService<ConfigService>()?.Current.VideoPlayback; }
        catch { return null; }
    }

    /// <summary>把当前音量/静音灌进播放器并刷新图标（播放器未创建时只刷图标）。</summary>
    private void ApplyVolume()
    {
        if (_player is not null)
        {
            try
            {
                _player.Audio.Volume = Math.Clamp(_volume, 0, 100);
                _player.Audio.Mute = _muted;
            }
            catch (Exception ex)
            {
                _logger?.Warn($"[FlyPlayer] 音量设置失败: {ex.Message}");
            }
        }
        UpdateMuteGlyph();
    }

    private void UpdateMuteGlyph()
    {
        VolumeOnIcon.Visibility = _muted ? Visibility.Collapsed : Visibility.Visible;
        VolumeOffIcon.Visibility = _muted ? Visibility.Visible : Visibility.Collapsed;
        MuteToggleButton.ToolTip = _muted ? "取消静音" : "静音";
    }

    private void MuteToggle_Click(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        ApplyVolume();
        PersistPlaybackSettings();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settingVolumeFromCode) return;
        _volume = (int)Math.Round(e.NewValue);
        // 拖音量隐含取消静音（与主流播放器一致）
        if (_volume > 0 && _muted) _muted = false;
        ApplyVolume();
    }

    private void VolumeSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        => PersistPlaybackSettings();

    /// <summary>音量/静音写回 config.json：拖动结束与点静音各一次，不在拖动过程中频繁落盘。</summary>
    private void PersistPlaybackSettings()
    {
        try
        {
            var cfg = App.Services.GetService<ConfigService>();
            if (cfg is null) return;
            cfg.Current.VideoPlayback.Volume = Math.Clamp(_volume, 0, 100);
            cfg.Current.VideoPlayback.Muted = _muted;
            cfg.Save();
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[FlyPlayer] 音量持久化失败: {ex.Message}");
        }
    }

    // ── 缓冲提示 ─────────────────────────────────────────────────────

    private void SetBuffering(bool buffering)
    {
        _buffering = buffering;
        UpdateBufferingLayer();
    }

    /// <summary>仅在播放/暂停态显示缓冲提示；加载态有自己的 LoadingLayer，不重复显示。</summary>
    private void UpdateBufferingLayer()
    {
        BufferingLayer.Visibility = _buffering && _inPlayback
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DisposePlayer()
    {
        _uiSyncTimer.Stop();
        _controlBarHideTimer.Stop();
        _buffering = false;
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
