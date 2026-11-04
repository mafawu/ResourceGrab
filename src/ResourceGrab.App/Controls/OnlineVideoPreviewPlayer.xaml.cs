using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyleafLib;
using FlyleafLib.Controls.WPF;
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
/// 对外接口与旧 VLC 版一致，VideoView / 短剧页共用；小窗模式也在这里统一实现。
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

    /// <summary>海报缺失/解码失败时的兜底底色（null = 不显示兜底，保持原有纯黑行为）。</summary>
    public static readonly DependencyProperty PosterFallbackBrushProperty = DependencyProperty.Register(
        nameof(PosterFallbackBrush), typeof(Brush), typeof(OnlineVideoPreviewPlayer),
        new PropertyMetadata(null, static (d, _) => ((OnlineVideoPreviewPlayer)d).UpdatePosterFallback()));

    /// <summary>兜底层中间的字（一般是剧名首字）。</summary>
    public static readonly DependencyProperty PosterFallbackTextProperty = DependencyProperty.Register(
        nameof(PosterFallbackTextValue), typeof(string), typeof(OnlineVideoPreviewPlayer),
        new PropertyMetadata("", static (d, _) => ((OnlineVideoPreviewPlayer)d).UpdatePosterFallback()));

    /// <summary>当前视频宽高比（宽/高）；0 表示尚未得知，竖屏片 &lt; 1。</summary>
    public static readonly DependencyProperty VideoAspectRatioProperty = DependencyProperty.Register(
        nameof(VideoAspectRatio), typeof(double), typeof(OnlineVideoPreviewPlayer),
        new PropertyMetadata(0d));

    public static readonly DependencyProperty ShowPlayButtonProperty = DependencyProperty.Register(
        nameof(ShowPlayButton), typeof(bool), typeof(OnlineVideoPreviewPlayer),
        new PropertyMetadata(false, static (d, _) => ((OnlineVideoPreviewPlayer)d).UpdatePlayButton()));

    /// <summary>可播放流地址（HLS m3u8 或 mp4 直链）；空表示无流可播。</summary>
    public string StreamUrl
    {
        get => (string)GetValue(StreamUrlProperty);
        set => SetValue(StreamUrlProperty, value);
    }

    /// <summary>海报兜底底色；null 时保持原行为（纯黑）。</summary>
    public Brush? PosterFallbackBrush
    {
        get => (Brush?)GetValue(PosterFallbackBrushProperty);
        set => SetValue(PosterFallbackBrushProperty, value);
    }

    /// <summary>海报兜底层中间显示的字。</summary>
    public string PosterFallbackTextValue
    {
        get => (string)GetValue(PosterFallbackTextProperty);
        set => SetValue(PosterFallbackTextProperty, value);
    }

    /// <summary>当前视频宽高比（宽/高）；0 表示未知，&lt; 1 为竖屏。</summary>
    public double VideoAspectRatio
    {
        get => (double)GetValue(VideoAspectRatioProperty);
        private set => SetValue(VideoAspectRatioProperty, value);
    }

    /// <summary>视频尺寸确定后触发（宿主据此调整播放器框比例）。</summary>
    public event EventHandler<double>? VideoAspectChanged;

    /// <summary>点击「竖屏/横屏」按钮：宿主切换手动比例覆盖。</summary>
    public event EventHandler? AspectToggleRequested;

    /// <summary>设置菜单选择「自动识别画面比例」：宿主清除手动覆盖。</summary>
    public event EventHandler? AspectAutoRequested;

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
    /// <summary>Loading 期间收到 Pause（切 Tab 离场）：首帧就绪后立刻暂停，避免后台出声。</summary>
    private bool _pauseOnFirstFrame;

    /// <summary>是否处于小窗模式（FlyleafHost 已 detach 到独立置顶窗口）。</summary>
    private bool _isMini;
    /// <summary>小窗是否置顶；默认置顶，用户可在小窗顶栏切换。</summary>
    private bool _miniTopMost = true;
    /// <summary>上一次小窗尺寸（用户拖动缩放过就沿用，避免每次回到默认大小）。</summary>
    private Size? _miniLastSize;
    /// <summary>小窗顶部独立控制条窗口：放在视频上方，不覆盖画面。</summary>
    private Window? _miniHeader;
    private const double MiniHeaderHeight = 34;
    /// <summary>短剧模式：显示上下集/选集/倍速/页面全屏/设置，隐藏音量滑条与停止。</summary>
    private bool _dramaMode;
    /// <summary>是否显示音量滑条（短剧模式隐藏；退出小窗时据此恢复）。</summary>
    private bool _showVolumeSlider = true;
    /// <summary>画面比例按钮当前状态：true=竖屏；由宿主 SetAspectState 同步。</summary>
    private bool _aspectPortrait = true;
    /// <summary>画面比例是否为自动识别；设置菜单的勾选项据此显示。</summary>
    private bool _aspectAuto = true;
    /// <summary>窄播放器（竖屏 9:16 或小窗）使用紧凑控制条，避免按钮被裁掉。</summary>
    private bool _compactBar;
    /// <summary>极窄（&lt;360px）再隐藏时长/选集/上下集，只保留播放、进度、静音、比例、设置、全屏。</summary>
    private bool _narrowBar;
    /// <summary>当前播放速度（1.0 = 原速）。</summary>
    private double _playbackSpeed = 1.0;

    private int _volume = 100;
    private bool _muted;
    /// <summary>代码内回填滑条值时抑制回写配置，避免初始化时误写。</summary>
    private bool _settingVolumeFromCode;

    /// <summary>控制条自动隐藏计时器：播放/暂停态 5 秒无操作后隐藏进度条与按钮。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _controlBarHideTimer = new()
    {
        Interval = TimeSpan.FromSeconds(5),
    };

    /// <summary>音量悬浮条的延迟关闭：给鼠标从图标移到浮层留出时间。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _volumePopupTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(320),
    };

    /// <summary>播放状态同步计时器：500ms 读一次 CurTime/Duration/IsPlaying，驱动进度条与状态机。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _uiSyncTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(500),
    };

    public OnlineVideoPreviewPlayer()
    {
        InitializeComponent();
        VideoHost.Background = Brushes.Black;
        InitVolumeFromConfig();
        Unloaded += (_, _) =>
        {
            _controlBarHideTimer.Stop();
            _volumePopupTimer.Stop();
            VolumePopup.IsOpen = false;
            // 短剧详情页切 Tab 时只 Pause、不销毁 Player：保留流/解码器/播放位置，
            // 回到页面还是同一集同一进度。小窗模式下则完全不动，继续在独立窗口播放。
            if (_isMini) return;
            if (KeepAliveOnUnload) return;
            DisposePlayer();
        };
        // 页面把播放器隐藏/移出可视树时，小窗必须继续独立播放：
        // 切到漫画等其它 Tab 时页面会 Unloaded/IsVisible=false，但小窗是独立置顶窗口，
        // 不能在这里收回或暂停，否则用户一切 Tab 小窗就消失。
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            if (_isMini) return;
            // 切顶栏 Tab 时先于 Unloaded 触发：此时 Player/HwndHost 还活着，先 Pause 才能保住位置。
            if (KeepAliveOnUnload) Pause();
        };
        // 播放画面在 FlyleafHost 的独立窗口里，滚轮不会冒泡到页面；挂转发让滚轮能滚动页面
        AttachWheelForwarding();
        Loaded += (_, _) =>
        {
            AttachWheelForwarding();
            if (_player is not null && _state is PlayerState.Playing or PlayerState.Paused or PlayerState.Loading)
                _uiSyncTimer.Start();
        };
        _controlBarHideTimer.Tick += (_, _) =>
        {
            _controlBarHideTimer.Stop();
            if (!_inPlayback) return;
            ControlBar.Visibility = Visibility.Collapsed;
            VolumePopup.IsOpen = false;
        };
        _uiSyncTimer.Tick += (_, _) => SyncPlaybackUi();

        // 音量浮层锚在音量图标上；Popup 属于独立顶层窗口，不能依赖模板内部的 ElementName 绑定。
        VolumePopup.PlacementTarget = MuteToggleButton;
        _volumePopupTimer.Tick += (_, _) =>
        {
            _volumePopupTimer.Stop();
            if (VolumePopup.Child is UIElement child && child.IsMouseOver) return;
            VolumePopup.IsOpen = false;
            if (_inPlayback) RestartControlBarTimer();
        };
    }

    /// <summary>
    /// 把 FlyleafHost 的 Surface / Overlay 窗口上的滚轮转发给页面最近的 ScrollViewer：
    /// 这两个窗口是独立顶层窗口，滚轮不会冒泡回主窗口，导致鼠标停在播放画面上无法滚动页面。
    /// Ctrl/Shift+滚轮不转发，保留 Flyleaf 的缩放/旋转手势。
    /// </summary>
    private void AttachWheelForwarding()
    {
        try
        {
            if (VideoHost?.Surface is { } surface)
            {
                surface.PreviewMouseWheel -= ForwardWheelToPage;
                surface.PreviewMouseWheel += ForwardWheelToPage;
            }
            if (VideoHost?.Overlay is { } overlay)
            {
                overlay.PreviewMouseWheel -= ForwardWheelToPage;
                overlay.PreviewMouseWheel += ForwardWheelToPage;
            }
        }
        catch { /* 宿主尚未就绪时忽略，Loaded/播放时会再试 */ }
    }

    private void ForwardWheelToPage(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        var scroller = FindAncestorScrollViewer(this);
        if (scroller is null) return;
        scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject start)
    {
        for (var node = start; node is not null; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer scroller) return scroller;
        }
        return null;
    }

    // ── 状态切换 ─────────────────────────────────────────────────────

    private const string DefaultErrorText = "播放失败，视频可能已下架或网络受限";

    private void SetErrorText(string? text)
    {
        ErrorText.Text = string.IsNullOrWhiteSpace(text) ? DefaultErrorText : text;
    }

    private void SetState(PlayerState state)
    {
        // 自然播完/出错时先把画面收回页面，避免页面回到海报态后小窗还挂着一个黑窗口
        if (_isMini && state is (PlayerState.Idle or PlayerState.Error)) ExitMiniWindow();
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
            VolumePopup.IsOpen = false;
        }
        PauseToggleButton.Content = state == PlayerState.Paused ? "►" : "❚❚";
        UpdateMiniButton();
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

    // 控制条/小窗顶栏实际活在 FlyleafHost 的 Overlay 独立窗口里，页面根的鼠标事件收不到，
    // 所以视频内容层也挂一份：页面内和小窗里移动鼠标都能唤出控制条。
    private void VideoContent_MouseEnter(object sender, MouseEventArgs e) => ShowControlBarTemporarily();

    private void VideoContent_MouseMove(object sender, MouseEventArgs e) => ShowControlBarTemporarily();

    private void VideoContent_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_inPlayback) RestartControlBarTimer();
    }

    private void UpdatePoster()
    {
        var poster = PosterUrl;
        var isUrl = poster.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(poster) || (!isUrl && !File.Exists(poster)))
        {
            PosterImage.Source = null;
            UpdatePosterFallback();
            return;
        }
        ImageLoader.SetSource(PosterImage, poster);
        // 图片是异步解码的，失败时 Source 保持 null，稍后还要重算兜底
        UpdatePosterFallback();
        Dispatcher.BeginInvoke(new Action(UpdatePosterFallback),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 海报兜底层：站点封面 CDN 目前返回加密字节（解不出图），没有兜底就是一块纯黑。
    /// 只有调用方显式给了 <see cref="PosterFallbackBrush"/> 才显示，不影响既有视频页。
    /// </summary>
    private void UpdatePosterFallback()
    {
        if (PosterFallback is null) return;
        var brush = PosterFallbackBrush;
        PosterFallback.Background = brush;
        PosterFallback.Visibility = brush is not null && PosterImage.Source is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        PosterFallbackLabel.Text = PosterFallbackTextValue;
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
        // 小窗挂在独立顶层窗口上：先收回页面再停，避免留下无主的黑窗口
        if (_isMini) ExitMiniWindow();
        _openGeneration++; // 作废在途打开
        _pendingSeekMs = 0;
        _pendingPause = false;
        _pauseOnFirstFrame = false;
        _buffering = false;
        try { _player?.Stop(); } catch { /* 已停止/未初始化 */ }
        _uiSyncTimer.Stop();
        SetState(PlayerState.Idle);
    }

    /// <summary>
    /// 小窗内切换上/下一集：只停当前流，不收回小窗、不销毁 Surface。
    /// 调用方随后设置新的 StreamUrl 并 Play()，画面在同一个独立窗口里续上。
    /// </summary>
    public void StopForEpisodeSwitch()
    {
        if (!_isMini)
        {
            Stop();
            return;
        }

        _openGeneration++;
        _pendingSeekMs = 0;
        _pendingPause = false;
        _pauseOnFirstFrame = false;
        _buffering = false;
        try { _player?.Stop(); } catch { /* 已停止/未初始化 */ }
        _uiSyncTimer.Stop();
        // 保持 _isMini 与 VideoHost/Surface 不动；Play() 会重新 SetState(Loading)。
        ControlBar.Visibility = Visibility.Visible;
        RestartControlBarTimer();
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

    /// <summary>是否处于暂停态（读 Flyleaf 运行时状态）。</summary>
    public bool IsPaused
    {
        get
        {
            try { return _player?.Status == Status.Paused; }
            catch { return false; }
        }
    }

    // ── 小窗模式（360 浏览器式置顶小窗：同一个 Player，不重连） ─────────

    /// <summary>是否正处在小窗模式。</summary>
    public bool IsMiniWindow => _isMini;

    /// <summary>小窗顶栏显示的标题（页面在详情渲染时设置）。</summary>
    public string PlayerTitle { get; set; } = "";

    /// <summary>小窗模式开关后触发（宿主可据此调整外层布局）。</summary>
    public event EventHandler? MiniWindowChanged;

    /// <summary>短剧模式：上一集请求。</summary>
    public event EventHandler? PreviousEpisodeRequested;
    /// <summary>短剧模式：下一集请求。</summary>
    public event EventHandler? NextEpisodeRequested;
    /// <summary>短剧模式：选集请求（宿主把右侧选集滚进可视区）。</summary>
    public event EventHandler? EpisodeListRequested;
    /// <summary>短剧模式：页面全屏请求（宿主隐藏标题/侧栏/简介让播放区铺满）。</summary>
    public event EventHandler? PageFullScreenRequested;

    /// <summary>右上角来源水印（在线显示频道名，本地显示「本地」）。</summary>
    public string SourceName
    {
        get => _sourceName;
        set { _sourceName = value ?? ""; UpdateSourceWatermark(); }
    }
    private string _sourceName = "";

    /// <summary>小窗顶栏的集数（如「第 3 集」）；空则不显示。</summary>
    public string MiniSubtitle
    {
        get => _miniSubtitle;
        set { _miniSubtitle = value ?? ""; UpdateMiniEpisodeText(); }
    }
    private string _miniSubtitle = "";

    /// <summary>当前视频总时长（毫秒）；未知返回 0。</summary>
    public long DurationMs => _durationMs;

    /// <summary>当前播放速度（1.0 = 原速）。</summary>
    public double PlaybackSpeed => _playbackSpeed;

    /// <summary>是否处于电脑全屏（Flyleaf 把画面铺满显示器，控制条仍在）。</summary>
    public bool IsFullScreen
    {
        get
        {
            try { return VideoHost.IsFullScreen; }
            catch { return false; }
        }
    }

    /// <summary>
    /// true：控件从可视树卸载时保留 Player（短剧详情页切顶栏 Tab 用）。
    /// 页面 Unloaded 会先 Pause，回来复用同一 Player/流/解码器，不重新打开。
    /// </summary>
    public bool KeepAliveOnUnload { get; set; }

    /// <summary>
    /// 短剧页配置：打开上下集/选集/倍速/页面全屏/设置，隐藏音量滑条与停止按钮。
    /// 只改控制条显隐，不影响播放器行为。
    /// </summary>
    public void ConfigureForDrama()
    {
        _dramaMode = true;
        ControlBar.Padding = new Thickness(10, 6, 10, 6);
        CurrentTimeText.Margin = new Thickness(8, 0, 2, 0);
        DurationText.Margin = new Thickness(0, 0, 8, 0);
        SpeedButton.Content = $"{_playbackSpeed:0.0#}x";
        UpdateCompactBar();
        UpdateControlBarModeVisibility();
    }

    /// <summary>
    /// 同步画面比例按钮的文案。auto=true 表示跟随首帧自动识别；
    /// auto=false 表示用户手动锁定，按钮显示当前锁定方向。
    /// </summary>
    public void SetAspectState(bool portrait, bool auto)
    {
        _aspectPortrait = portrait;
        _aspectAuto = auto;
        if (RatioButton is null) return;
        RatioButton.Content = portrait
            ? (_compactBar ? "竖" : "竖屏")
            : (_compactBar ? "横" : "横屏");
        var other = portrait ? "横屏" : "竖屏";
        RatioButton.ToolTip = auto
            ? $"自动识别为{(portrait ? "竖屏" : "横屏")}，点击切换为{other}"
            : $"已手动锁定{(portrait ? "竖屏" : "横屏")}，点击切换为{other}；设置菜单可恢复自动识别";
    }

    /// <summary>换剧/重新载入时清掉上一部片子的画面比例，回到「未知 → 短剧默认竖屏」。</summary>
    public void ResetVideoAspect()
    {
        if (VideoAspectRatio <= 0) return;
        VideoAspectRatio = 0;
        VideoAspectChanged?.Invoke(this, 0);
    }

    private void RatioButton_Click(object sender, RoutedEventArgs e)
        => AspectToggleRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>上下集可用状态（短剧页在切集时调用）。</summary>
    public void SetEpisodeNavigation(bool canPrevious, bool canNext)
    {
        PrevEpisodeButton.IsEnabled = canPrevious;
        NextEpisodeButton.IsEnabled = canNext;
        MiniPrevButton.IsEnabled = canPrevious;
        MiniNextButton.IsEnabled = canNext;
    }

    /// <summary>暂停（不销毁流）。切 Tab 离开详情页时调用，回来后从原位置继续。</summary>
    public void Pause()
    {
        if (IsPlaying)
        {
            PauseInternal();
            return;
        }
        if (_state == PlayerState.Loading) _pauseOnFirstFrame = true;
    }

    /// <summary>播放/暂停切换（短剧页主按钮复用）。</summary>
    public void TogglePause()
    {
        if (_state is not (PlayerState.Playing or PlayerState.Paused)) return;
        if (IsPlaying)
        {
            PauseInternal();
        }
        else
        {
            try { _player?.Play(); } catch { }
            PauseToggleButton.Content = "❚❚";
            SetState(PlayerState.Playing);
        }
    }

    /// <summary>电脑全屏切换（Flyleaf 把 Surface 铺满当前显示器）。</summary>
    public void ToggleFullScreen()
    {
        try { VideoHost.IsFullScreen = !VideoHost.IsFullScreen; }
        catch (Exception ex) { _logger?.Warn($"[FlyPlayer] 全屏切换失败: {ex.Message}"); }
    }


    /// <summary>
    /// 进入小窗：把 FlyleafHost 的 Surface（视频画面）与 Overlay（控制条）detach 成独立置顶窗口。
    /// 复用同一个 Player（不 Stop、不重新 Open），网络连接、解码器、播放位置全部原样保留，
    /// 所以进入小窗不会黑一下、不会重新拉流。
    /// </summary>
    public void EnterMiniWindow()
    {
        if (_isMini) return;
        if (!IsVisible) return;
        VolumePopup.IsOpen = false;
        // 详情页标题栏直接点「小窗」时还没起播：先起播（TryPlay 同步进入 Loading），再搬窗口
        if (_state == PlayerState.Idle && !string.IsNullOrEmpty(StreamUrl)) TryPlay();
        if (_state is PlayerState.Idle or PlayerState.Error) return;
        if (!VideoHost.IsLoaded) return;

        try
        {
            var host = VideoHost;
            host.DetachedTopMost = _miniTopMost;
            host.DetachedShowInTaskbar = true;
            host.DetachedNoOwner = true;              // 主窗口最小化时小窗不跟着消失
            host.DetachedMinWidth = 260;
            host.DetachedMinHeight = 150;
            // 不设 DetachedMaxWidth/Height：小窗可一直放大到屏幕大小。
            host.DetachedMaxWidth = double.PositiveInfinity;
            host.DetachedMaxHeight = double.PositiveInfinity;
            host.DetachedRememberPosition = false;
            host.DetachedRememberSize = false;
            // 首次小窗居中于主程序窗口，而不是屏幕右下角；之后仍可拖动。
            // Flyleaf 会按 MinWidth/MinHeight 再放大固定尺寸，所以先做同样的等比修正，
            // 否则 236x420 的竖屏尺寸会被撑成 260x462，居中会偏。
            var miniSize = NormalizeMiniWindowSize(_miniLastSize ?? ComputeMiniWindowSize());
            host.DetachedFixedSize = miniSize;
            host.DetachedPosition = DetachedPositionOptions.Custom;
            host.DetachedFixedPosition = ComputeMiniWindowOrigin(miniSize);
            host.DetachedPositionMargin = new Thickness(0);
            host.DetachedDragMove = AvailableWindows.Both;   // 拖动画面/顶栏空白处移动
            host.DetachedResize = AvailableWindows.Both;     // 四边/四角拖动缩放
            host.ResizeSensitivity = 8;

            host.IsAttached = false;                  // ← 关键：只换宿主窗口，不碰 Player
            host.KeepRatioOnResize = false;           // 小窗自由缩放，不锁视频比例
            HookMiniSurface();
            ShowMiniHeader();                         // 顶栏放到视频上方，不覆盖画面
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[FlyPlayer] 进入小窗失败: {ex.Message}");
            CloseMiniHeader();
            _isMini = false;
            // 可能已经 detach 了一半：恢复回页面，别留下无主窗口
            try
            {
                VideoHost.KeepRatioOnResize = false;
                VideoHost.IsAttached = true;
            }
            catch { }
            return;
        }

        _isMini = true;
        MiniTitleText.Text = string.IsNullOrWhiteSpace(PlayerTitle) ? "小窗播放" : PlayerTitle;
        UpdateMiniPinVisual();
        MiniBar.Visibility = Visibility.Visible;
        MiniPlaceholder.Visibility = Visibility.Visible;
        // 小窗宽度有限：收起短剧专属按钮/音量滑条，收紧控制条内边距，给进度条留出空间
        ControlBar.Padding = new Thickness(8, 4, 8, 4);
        UpdateControlBarModeVisibility();
        UpdateMiniEpisodeText();
        UpdateSourceWatermark();
        ShowControlBarTemporarily();
        try
        {
            if (VideoHost.Surface is { } surface) surface.Title = $"小窗 · {MiniTitleText.Text}";
        }
        catch { /* 标题只是锦上添花 */ }
        MiniWindowChanged?.Invoke(this, EventArgs.Empty);
        _logger?.Info($"[FlyPlayer] 已进入小窗（同一 Player，位置 {CurrentPositionMs}ms）");
    }

    /// <summary>
    /// 退出小窗：把 Surface/Overlay 重新挂回页面里的 FlyleafHost。
    /// 同样不碰 Player，关闭小窗后从当前毫秒位置继续播，进度不丢也不重连。
    /// </summary>
    public void ExitMiniWindow()
    {
        if (!_isMini) return;
        _isMini = false;
        CloseMiniHeader();
        MiniPlaceholder.Visibility = Visibility.Collapsed;
        MiniEpisodeText.Visibility = Visibility.Collapsed;
        ControlBar.Padding = _compactBar
            ? new Thickness(6, 5, 6, 5)
            : _dramaMode ? new Thickness(10, 6, 10, 6) : new Thickness(12, 8, 12, 8);
        UpdateControlBarModeVisibility();
        UpdateSourceWatermark();
        try
        {
            // 先关等比缩放再挂回页面：Attach 内部会调 ResizeRatio，
            // 若此时还是 true 会按视频比例改页面里播放器控件的宽高。
            VideoHost.KeepRatioOnResize = false;
            VideoHost.IsAttached = true;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[FlyPlayer] 退出小窗失败: {ex.Message}");
        }
        ShowControlBarTemporarily();
        MiniWindowChanged?.Invoke(this, EventArgs.Empty);
        _logger?.Info($"[FlyPlayer] 已退出小窗（位置 {CurrentPositionMs}ms）");
    }

    /// <summary>默认小窗尺寸：横屏约 420 宽，竖屏约 420 高，且不超过工作区 85%。</summary>
    private Size ComputeMiniWindowSize()
    {
        var ratio = VideoAspectRatio > 0 ? VideoAspectRatio : 16.0 / 9.0;
        double width, height;
        if (ratio >= 1)
        {
            width = 420;
            height = width / ratio;
        }
        else
        {
            height = 420;
            width = height * ratio;
        }
        var maxWidth = Math.Max(320, SystemParameters.WorkArea.Width * 0.6);
        var maxHeight = Math.Max(240, SystemParameters.WorkArea.Height * 0.6);
        if (width > maxWidth) { width = maxWidth; height = width / ratio; }
        if (height > maxHeight) { height = maxHeight; width = height * ratio; }
        return new Size(Math.Round(Math.Max(180, width)), Math.Round(Math.Max(100, height)));
    }

    /// <summary>
    /// 按 FlyleafHost 的 Min/Max 约束预修正小窗尺寸（保持视频比例），
    /// 让“居中”使用的是最终窗口尺寸，而不是修正前的尺寸。
    /// </summary>
    private Size NormalizeMiniWindowSize(Size size)
    {
        var ratio = VideoAspectRatio > 0 ? VideoAspectRatio : 16.0 / 9.0;
        var minWidth = 260.0;
        var minHeight = 150.0;

        var width = Math.Max(size.Width, minWidth);
        var height = width / ratio;
        if (height < minHeight)
        {
            height = minHeight;
            width = height * ratio;
        }
        return new Size(Math.Round(width), Math.Round(height));
    }

    /// <summary>
    /// 小窗首次出现的位置：主程序窗口中心。Window 的 Left/Top 是 DIP，
    /// Flyleaf 的 DetachedFixedPosition 用物理像素，所以这里乘 DPI。
    /// 主程序不可用时退回工作区中心。
    /// </summary>
    private Point ComputeMiniWindowOrigin(Size miniSize)
    {
        var owner = Window.GetWindow(this) ?? Application.Current?.MainWindow;
        var dpi = owner is null ? VisualTreeHelper.GetDpi(this) : VisualTreeHelper.GetDpi(owner);
        double left, top;

        if (owner is null || double.IsNaN(owner.Left) || double.IsNaN(owner.Top))
        {
            var work = SystemParameters.WorkArea;
            left = (work.Left + (work.Width - miniSize.Width) / 2) * dpi.DpiScaleX;
            top = (work.Top + (work.Height - miniSize.Height) / 2) * dpi.DpiScaleY;
        }
        else
        {
            var ownerWidth = owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width;
            var ownerHeight = owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height;
            // Window.Left/Top/ActualWidth 都是 DIP；Flyleaf 的 FixedPosition 是物理像素。
            left = (owner.Left + (ownerWidth - miniSize.Width) / 2) * dpi.DpiScaleX;
            top = (owner.Top + (ownerHeight - miniSize.Height) / 2) * dpi.DpiScaleY;
        }

        // 物理像素下再限制在虚拟屏幕内，避免主窗口跨屏/贴边时小窗跑出可见区域。
        var virtualLeft = SystemParameters.VirtualScreenLeft * dpi.DpiScaleX;
        var virtualTop = SystemParameters.VirtualScreenTop * dpi.DpiScaleY;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth * dpi.DpiScaleX;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight * dpi.DpiScaleY;
        left = Math.Clamp(left, virtualLeft, Math.Max(virtualLeft, virtualRight - miniSize.Width * dpi.DpiScaleX));
        top = Math.Clamp(top, virtualTop, Math.Max(virtualTop, virtualBottom - miniSize.Height * dpi.DpiScaleY));
        return new Point(left, top);
    }

    /// <summary>
    /// 小窗顶栏改为独立窗口，贴在视频窗口上方；不再作为覆盖层压在画面上。
    /// </summary>
    private void ShowMiniHeader()
    {
        if (VideoHost.Surface is not { } surface) return;
        if (surface is Window surfaceWindow) surfaceWindow.Background = Brushes.Black;
        CloseMiniHeader();

        var header = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Height = MiniHeaderHeight,
            Width = Math.Max(180, surface.Width),
            Topmost = _miniTopMost,
            Title = "小窗控制",
        };
        try { header.Owner = surface; } catch { /* Owner 只是置顶辅助 */ }

        if (MiniBar.Parent is Panel parent) parent.Children.Remove(MiniBar);
        MiniBar.Visibility = Visibility.Visible;
        header.Content = MiniBar;
        _miniHeader = header;
        header.Show();

        surface.LocationChanged += MiniSurface_LocationChanged;
        surface.SizeChanged += MiniHeader_SurfaceSizeChanged;
        SyncMiniHeader();
        UpdateMiniHeaderNavigation();
    }

    private void CloseMiniHeader()
    {
        if (_miniHeader is null) return;
        if (VideoHost.Surface is { } surface)
        {
            surface.LocationChanged -= MiniSurface_LocationChanged;
            surface.SizeChanged -= MiniHeader_SurfaceSizeChanged;
        }
        _miniHeader.Content = null;
        try { _miniHeader.Close(); } catch { }
        _miniHeader = null;
        MiniBar.Visibility = Visibility.Collapsed;
    }

    private void MiniSurface_LocationChanged(object? sender, EventArgs e) => SyncMiniHeader();

    private void MiniHeader_SurfaceSizeChanged(object? sender, SizeChangedEventArgs e) => SyncMiniHeader();

    private void SyncMiniHeader()
    {
        if (_miniHeader is null || VideoHost.Surface is not { } surface) return;
        var width = Math.Max(180, surface.Width);
        _miniHeader.Left = surface.Left;
        _miniHeader.Top = surface.Top - MiniHeaderHeight;
        _miniHeader.Width = width;
        _miniHeader.Height = MiniHeaderHeight;
        var showEpisode = _isMini && _miniSubtitle.Length > 0;
        MiniEpisodeText.Visibility = showEpisode ? Visibility.Visible : Visibility.Collapsed;
        MiniTitleText.MaxWidth = Math.Max(24, width - (showEpisode ? 245 : 190));
        var narrow = width < 330;
        MiniPinButton.Content = _miniTopMost
            ? (narrow ? "已顶" : "已置顶")
            : (narrow ? "顶" : "置顶");
    }

    private void UpdateMiniHeaderNavigation()
    {
        MiniPrevButton.Visibility = _dramaMode ? Visibility.Visible : Visibility.Collapsed;
        MiniNextButton.Visibility = _dramaMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>记住用户拖动缩放过的小窗尺寸（仅本实例生命周期内）。</summary>
    private void HookMiniSurface()
    {
        if (VideoHost.Surface is not { } surface) return;
        surface.SizeChanged -= MiniSurface_SizeChanged;
        surface.SizeChanged += MiniSurface_SizeChanged;
    }

    private void MiniSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isMini) return;
        if (e.NewSize.Width >= 180 && e.NewSize.Height >= 100)
            _miniLastSize = e.NewSize;
    }

    private void UpdateMiniPinVisual()
    {
        var narrow = (_miniHeader?.Width ?? 420) < 330;
        MiniPinButton.Content = _miniTopMost
            ? (narrow ? "已顶" : "已置顶")
            : (narrow ? "顶" : "置顶");
        try
        {
            MiniPinButton.Foreground = _miniTopMost
                ? (Brush)FindResource("PrimaryLightBrush")
                : Brushes.White;
        }
        catch { MiniPinButton.Foreground = Brushes.White; }
    }

    /// <summary>
    /// 按「短剧模式 / 小窗」重排控制条：短剧显示上下集/选集/倍速/页面全屏/设置，
    /// 小窗只留最基础的播放/进度/静音，避免 420px 宽塞满。
    /// </summary>
    private void UpdateControlBarModeVisibility()
    {
        var drama = _dramaMode && !_isMini;
        var moreMode = drama && _compactBar;
        PrevEpisodeButton.Visibility = drama && !_compactBar ? Visibility.Visible : Visibility.Collapsed;
        NextEpisodeButton.Visibility = drama && !_compactBar ? Visibility.Visible : Visibility.Collapsed;
        EpisodeListButton.Visibility = drama && !_compactBar ? Visibility.Visible : Visibility.Collapsed;
        // 竖屏把倍速、比例、全屏、选集、上下集全部收进「更多」，只留播放/时间/进度/音量/小窗。
        SpeedButton.Visibility = drama && !_compactBar ? Visibility.Visible : Visibility.Collapsed;
        RatioButton.Visibility = drama && !_compactBar ? Visibility.Visible : Visibility.Collapsed;
        PageFullScreenButton.Visibility = drama && !_compactBar ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Visibility = drama ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Content = moreMode ? "更多" : "⚙";
        SettingsButton.FontFamily = moreMode
            ? new System.Windows.Media.FontFamily("Microsoft YaHei UI")
            : new System.Windows.Media.FontFamily("Segoe UI Symbol");
        SettingsButton.FontSize = moreMode ? 10.5 : 13;
        SettingsButton.ToolTip = moreMode ? "更多：选集 / 上下集 / 画面比例 / 倍速 / 全屏" : "播放设置";
        StopButton.Visibility = _dramaMode ? Visibility.Collapsed : Visibility.Visible;
        _showVolumeSlider = !_dramaMode;
        VolumeSlider.Visibility = !_isMini && _showVolumeSlider ? Visibility.Visible : Visibility.Collapsed;
        // 当前时间与总时长都保留，竖屏也显示完整时间对。
        DurationText.Visibility = Visibility.Visible;
        if (!_compactBar) VolumePopup.IsOpen = false;
        UpdateMiniButton();
        UpdateMiniHeaderNavigation();
    }

    /// <summary>
    /// 竖屏视频只有 ~390px 宽，完整控制条会溢出。窄于 560px 时切换紧凑排布：
    /// 上/下一集只留箭头、倍速收进设置菜单、缩短边距；保留时间/进度/选集/静音/比例/设置/全屏。
    /// </summary>
    private void UpdateCompactBar()
    {
        var width = ActualWidth;
        var compact = width > 0 && width < 560;
        var narrow = width > 0 && width < 360;
        if (compact == _compactBar && narrow == _narrowBar) return;
        _compactBar = compact;
        _narrowBar = narrow;

        ControlBar.Padding = compact
            ? new Thickness(4, 5, 4, 5)
            : _dramaMode ? new Thickness(10, 6, 10, 6) : new Thickness(12, 8, 12, 8);
        CurrentTimeText.FontSize = compact ? 9.5 : 10.5;
        DurationText.FontSize = compact ? 9.5 : 10.5;
        CurrentTimeText.Margin = compact
            ? new Thickness(2, 0, 2, 0)
            : new Thickness(_dramaMode ? 8 : 10, 0, 2, 0);
        DurationText.Margin = compact
            ? new Thickness(0, 0, 3, 0)
            : new Thickness(0, 0, _dramaMode ? 8 : 10, 0);

        PrevEpisodeButton.Content = compact ? "‹" : "‹ 上一集";
        NextEpisodeButton.Content = compact ? "›" : "下一集 ›";
        var pad = compact ? new Thickness(4, 3, 4, 3) : new Thickness(7, 3, 7, 3);
        PrevEpisodeButton.Padding = pad;
        NextEpisodeButton.Padding = pad;
        EpisodeListButton.Padding = pad;
        RatioButton.Padding = pad;
        PageFullScreenButton.Padding = pad;
        SettingsButton.Padding = pad;
        SettingsButton.FontSize = compact ? 12 : 13;

        SetAspectState(_aspectPortrait, _aspectAuto);
        UpdateControlBarModeVisibility();
        UpdateQualityButton();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateCompactBar();
    }

    private void UpdateSourceWatermark()
    {
        SourceWatermark.Text = _sourceName;
        SourceWatermark.Visibility = !_isMini && _sourceName.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateMiniEpisodeText()
    {
        MiniEpisodeText.Text = _miniSubtitle;
        MiniEpisodeText.Visibility = _isMini && _miniSubtitle.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void PrevEpisode_Click(object sender, RoutedEventArgs e)
        => PreviousEpisodeRequested?.Invoke(this, EventArgs.Empty);

    private void NextEpisode_Click(object sender, RoutedEventArgs e)
        => NextEpisodeRequested?.Invoke(this, EventArgs.Empty);

    private void EpisodeList_Click(object sender, RoutedEventArgs e)
        => EpisodeListRequested?.Invoke(this, EventArgs.Empty);

    private void PageFullScreenButton_Click(object sender, RoutedEventArgs e)
        => PageFullScreenRequested?.Invoke(this, EventArgs.Empty);

    private void SpeedButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var rate in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
        {
            var item = new MenuItem
            {
                Header = $"{rate:0.0#}x",
                IsCheckable = true,
                IsChecked = Math.Abs(rate - _playbackSpeed) < 0.01,
            };
            var value = rate;
            item.Click += (_, _) => SetPlaybackSpeed(value);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = SpeedButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void SetPlaybackSpeed(double rate)
    {
        _playbackSpeed = Math.Clamp(rate, 0.25, 4.0);
        SpeedButton.Content = $"{_playbackSpeed:0.0#}x";
        try
        {
            if (_player is not null) _player.Speed = _playbackSpeed;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[FlyPlayer] 设置倍速失败: {ex.Message}");
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dramaMode && _compactBar)
        {
            ShowDramaMoreMenu();
            return;
        }

        var menu = new ContextMenu();

        var speedMenu = new MenuItem { Header = "播放速度" };
        foreach (var rate in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
        {
            var item = new MenuItem
            {
                Header = $"{rate:0.0#}x",
                IsCheckable = true,
                IsChecked = Math.Abs(_playbackSpeed - rate) < 0.01,
            };
            item.Click += (_, _) => SetPlaybackSpeed(rate);
            speedMenu.Items.Add(item);
        }
        menu.Items.Add(speedMenu);

        var toggleAspect = new MenuItem
        {
            Header = _aspectPortrait ? "切换为横屏" : "切换为竖屏",
        };
        toggleAspect.Click += (_, _) => AspectToggleRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(toggleAspect);

        var autoAspect = new MenuItem
        {
            Header = "自动识别画面比例",
            IsCheckable = true,
            IsChecked = _aspectAuto,
        };
        autoAspect.Click += (_, _) => AspectAutoRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(autoAspect);
        menu.Items.Add(new Separator());

        var pageFull = new MenuItem { Header = "页面全屏" };
        pageFull.Click += (_, _) => PageFullScreenRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(pageFull);

        var osFull = new MenuItem { Header = "电脑全屏" };
        osFull.Click += (_, _) => ToggleFullScreen();
        menu.Items.Add(osFull);

        var mini = new MenuItem { Header = "小窗播放" };
        mini.Click += (_, _) => EnterMiniWindow();
        menu.Items.Add(mini);

        menu.PlacementTarget = SettingsButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 竖屏紧凑条的「更多」：只保留播放/时间/进度/音量/小窗，其余操作全部收在这里。
    /// </summary>
    private void ShowDramaMoreMenu()
    {
        var menu = new ContextMenu();

        var prev = new MenuItem { Header = "上一集", IsEnabled = PrevEpisodeButton.IsEnabled };
        prev.Click += (_, _) => PreviousEpisodeRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(prev);

        var next = new MenuItem { Header = "下一集", IsEnabled = NextEpisodeButton.IsEnabled };
        next.Click += (_, _) => NextEpisodeRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(next);

        var episodes = new MenuItem { Header = "选集" };
        episodes.Click += (_, _) => EpisodeListRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(episodes);
        menu.Items.Add(new Separator());

        var ratio = new MenuItem { Header = "画面比例" };
        var toggleAspect = new MenuItem { Header = _aspectPortrait ? "切换为横屏" : "切换为竖屏" };
        toggleAspect.Click += (_, _) => AspectToggleRequested?.Invoke(this, EventArgs.Empty);
        ratio.Items.Add(toggleAspect);
        var autoAspect = new MenuItem
        {
            Header = "自动识别",
            IsCheckable = true,
            IsChecked = _aspectAuto,
        };
        autoAspect.Click += (_, _) => AspectAutoRequested?.Invoke(this, EventArgs.Empty);
        ratio.Items.Add(autoAspect);
        menu.Items.Add(ratio);

        var speed = new MenuItem { Header = "播放速度" };
        foreach (var rate in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
        {
            var item = new MenuItem
            {
                Header = $"{rate:0.0#}x",
                IsCheckable = true,
                IsChecked = Math.Abs(_playbackSpeed - rate) < 0.01,
            };
            item.Click += (_, _) => SetPlaybackSpeed(rate);
            speed.Items.Add(item);
        }
        menu.Items.Add(speed);
        menu.Items.Add(new Separator());

        var pageFull = new MenuItem { Header = "页面全屏" };
        pageFull.Click += (_, _) => PageFullScreenRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(pageFull);

        var osFull = new MenuItem { Header = "电脑全屏" };
        osFull.Click += (_, _) => ToggleFullScreen();
        menu.Items.Add(osFull);

        if (_variants.Count >= 2)
        {
            menu.Items.Add(new Separator());
            var quality = new MenuItem { Header = "画质" };
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
                quality.Items.Add(item);
            }
            menu.Items.Add(quality);
        }

        menu.PlacementTarget = SettingsButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void UpdateMiniButton()
    {
        // 竖屏紧凑条把「小窗」作为一级操作直接露出；横屏仍从齿轮菜单进入。
        var ready = _state is PlayerState.Loading or PlayerState.Playing or PlayerState.Paused;
        var allowed = !_isMini && ready && (!_dramaMode || _compactBar);
        MiniButton.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MiniButton_Click(object sender, RoutedEventArgs e) => EnterMiniWindow();

    private void MiniPrev_Click(object sender, RoutedEventArgs e)
        => PreviousEpisodeRequested?.Invoke(this, EventArgs.Empty);

    private void MiniNext_Click(object sender, RoutedEventArgs e)
        => NextEpisodeRequested?.Invoke(this, EventArgs.Empty);

    private void MiniReturn_Click(object sender, RoutedEventArgs e) => ExitMiniWindow();

    private void MiniClose_Click(object sender, RoutedEventArgs e) => ExitMiniWindow();

    private void MiniPin_Click(object sender, RoutedEventArgs e)
    {
        _miniTopMost = !_miniTopMost;
        try { VideoHost.DetachedTopMost = _miniTopMost; } catch { }
        if (_miniHeader is not null) _miniHeader.Topmost = _miniTopMost;
        UpdateMiniPinVisual();
    }

    /// <summary>
    /// 接力续播（侧栏 ↔ 完整页）：从指定位置起播，paused=true 时起播后立即暂停。
    /// 两个页面各自持有播放器实例，交接时用此方法把位置与播放状态传给对方，
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
                // Flyleaf 3.x 的 CurTime setter 在当前播放状态下会把位置重置到开头；
                // 显式调用 SeekAccurate（单位毫秒）才是可靠跳转。
                _player.SeekAccurate((int)Math.Clamp(target, 0, Math.Max(0, _durationMs - 1)));
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
            SetErrorText("播放地址无效，请点「刷新地址」重试");
            SetState(PlayerState.Error);
            return;
        }
        if (!FlyleafEngine.EnsureStarted(_logger))
        {
            SetErrorText(FlyleafEngine.StartupError
                         ?? "播放组件未就绪：缺少 FFmpeg 共享库");
            SetState(PlayerState.Error);
            return;
        }
        EnsurePlayer();
        if (_player is null)
        {
            _logger?.Error("[FlyPlayer] 播放器实例创建失败");
            SetErrorText("播放器初始化失败，请重启程序");
            SetState(PlayerState.Error);
            return;
        }
        ApplyRequestHeaders();
        VideoHost.Player = _player;
        AttachWheelForwarding();
        SetErrorText(null);
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
            await Dispatcher.InvokeAsync(() =>
            {
                SetErrorText($"播放失败：{ex.Message}");
                SetState(PlayerState.Error);
            });
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

    /// <summary>
    /// 把当前视频的宽高比读出来（首帧后才有值）。
    /// 短剧大量是竖屏 720×1280，宿主需要据此把播放器框收窄，否则竖屏片会被塞进一个超宽的框里、两侧全是黑边。
    /// </summary>
    private void UpdateVideoAspect()
    {
        try
        {
            var width = _player?.Video.Width ?? 0;
            var height = _player?.Video.Height ?? 0;
            if (width <= 0 || height <= 0) return;

            var ratio = (double)width / height;
            if (Math.Abs(ratio - VideoAspectRatio) < 0.01) return;

            VideoAspectRatio = ratio;
            _logger?.Info($"[FlyPlayer] 视频尺寸 {width}x{height}（比例 {ratio:F3}）");
            VideoAspectChanged?.Invoke(this, ratio);
        }
        catch { /* 取不到就保持未知，宿主按默认布局 */ }
    }

    private void EnsurePlayer()
    {
        if (_player is not null) return;
        var player = new Player(new FlyleafLib.Config());
        player.Config.Video.BackColor = Colors.Black;
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
            // Stop() 已同步切到 Idle，这里只处理自然播完。
            // 注意 Flyleaf 在 Pause() 时也会发 PlaybackStopped：必须用状态区分，
            // 否则暂停会被误判成播完，画面回到海报、看起来就像“停止”。
            Dispatcher.InvokeAsync(() =>
            {
                // 切 Tab 离场时宿主销毁 Surface 也可能触发本事件；此时已主动 Pause，
                // 不能把状态改成 Idle，否则回来会显示海报并从 0 重开。
                // 小窗挂在独立窗口上：页面 Unloaded 也会触发一次 PlaybackStopped，
                // 不能因此收回小窗；只有真正播到结尾才结束小窗。
                if (_isMini)
                {
                    if (_player?.Status == Status.Ended)
                    {
                        ExitMiniWindow();
                        SetState(PlayerState.Idle);
                    }
                    return;
                }
                if (KeepAliveOnUnload && !IsVisible) return;
                if (_state == PlayerState.Paused) return;
                if (_player?.Status == Status.Paused) return;
                if (_state == PlayerState.Playing)
                    SetState(PlayerState.Idle);
            });
        };
        // 缓冲事件从播放线程发起（Flyleaf 明确提示不可在该回调里暂停/停止），一律切回 UI 线程
        player.BufferingStarted += (_, _) => Dispatcher.InvokeAsync(() => SetBuffering(true));
        player.BufferingCompleted += (_, _) => Dispatcher.InvokeAsync(() => SetBuffering(false));
        _player = player;
        ApplyVolume();
        try { player.Speed = _playbackSpeed; } catch { /* 未打开时设置可能失败，首帧后会再同步 */ }
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
                DurationText.Text = "/ " + FormatTime(_durationMs);
                SeekSlider.Visibility = Visibility.Visible;
                DurationText.Visibility = Visibility.Visible;
            }
            if (_player.IsPlaying && _state == PlayerState.Loading)
            {
                SetState(PlayerState.Playing);
                UpdateVideoAspect();
                if (_pauseOnFirstFrame)
                {
                    _pauseOnFirstFrame = false;
                    PauseInternal();
                }
                if (_openStopwatch is not null)
                {
                    _logger?.Info($"[FlyPlayer] 首帧耗时: {_openStopwatch.ElapsedMilliseconds}ms {StreamUrl}");
                    _openStopwatch = null;
                }
            }
            if (_state is PlayerState.Playing or PlayerState.Paused)
            {
                if (Math.Abs(_player.Speed - _playbackSpeed) > 0.01)
                {
                    try { _player.Speed = _playbackSpeed; } catch { }
                }
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
        DurationText.Text = "/ --:--";
    }

    private static string FormatTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    /// <summary>
    /// 点击进度条定位。WPF 的 IsMoveToPointEnabled 在 Flyleaf 的 Overlay 里偶尔只改 Value 不触发可靠
    /// 的 hit-test，这里直接按点击位置算目标毫秒并 SeekAccurate；点在 Thumb 上则放行给拖动逻辑。
    /// </summary>
    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_player is null || _durationMs <= 0 || SeekSlider.ActualWidth <= 0) return;
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.Thumb) return;
        }

        var ratio = Math.Clamp(e.GetPosition(SeekSlider).X / SeekSlider.ActualWidth, 0, 1);
        var target = ratio * _durationMs;
        _sliderUpdating = true;
        SeekSlider.Value = target;
        _sliderUpdating = false;
        SeekTo(target);
        e.Handled = true;
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sliderUpdating || _player is null) return;
        if (_seekDragging) return; // 拖动中不实时 seek，松手时一次定位
        SeekTo(e.NewValue);
    }

    /// <summary>按毫秒跳转。用 SeekAccurate；CurTime setter 在 3.x 会重置到开头。</summary>
    private void SeekTo(double milliseconds)
    {
        if (_player is null) return;
        try
        {
            var max = _durationMs > 0 ? _durationMs - 1 : int.MaxValue;
            var target = (int)Math.Clamp(milliseconds, 0, Math.Max(0, max));
            _player.SeekAccurate(target);
            _handoffSeekApplied = true; // 用户手动定位优先于接力定位
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[FlyPlayer] seek 失败: {ex.Message}");
        }
    }

    private void SeekSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        => _seekDragging = true;

    private void SeekSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _seekDragging = false;
        SeekTo(SeekSlider.Value);
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
        var show = _variants.Count >= 2 && (!_compactBar || !_dramaMode);
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
        _settingVolumeFromCode = true;
        VolumeSlider.Value = _volume;
        VolumeFlyoutSlider.Value = _volume;
        VolumePopupText.Text = $"{_volume}%";
        _settingVolumeFromCode = false;
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

    // ── 竖屏音量悬浮条 ──────────────────────────────

    private void VolumeHover_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_compactBar || _isMini) return;
        _volumePopupTimer.Stop();
        _settingVolumeFromCode = true;
        VolumeFlyoutSlider.Value = _volume;
        VolumePopupText.Text = $"{_volume}%";
        _settingVolumeFromCode = false;
        VolumePopup.IsOpen = true;
        _controlBarHideTimer.Stop();
    }

    private void VolumeHover_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!VolumePopup.IsOpen) return;
        _volumePopupTimer.Stop();
        _volumePopupTimer.Start();
    }

    private void VolumePopup_MouseEnter(object sender, MouseEventArgs e)
    {
        _volumePopupTimer.Stop();
        _controlBarHideTimer.Stop();
    }

    private void VolumePopup_MouseLeave(object sender, MouseEventArgs e)
    {
        _volumePopupTimer.Stop();
        _volumePopupTimer.Start();
    }

    private void VolumeFlyoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settingVolumeFromCode) return;
        _volume = (int)Math.Round(e.NewValue);
        if (_volume > 0 && _muted) _muted = false;
        ApplyVolume();
    }

    private void VolumeFlyoutSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
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
        // Unloaded 可能来自切页/关窗；小窗先挂回去，避免顶层 Surface 窗口泄漏
        var wasMini = _isMini;
        if (wasMini)
        {
            try { ExitMiniWindow(); } catch { /* 窗口已在销毁中，忽略 */ }
            // 挂回失败（Owner 已销毁等）时至少把独立窗口藏起来，不留黑窗
            if (!VideoHost.IsAttached && VideoHost.Surface is { } miniSurface)
            {
                try { miniSurface.Hide(); } catch { }
            }
        }
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
