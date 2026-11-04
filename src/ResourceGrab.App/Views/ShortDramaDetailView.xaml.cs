using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Common;
using ResourceGrab.App.Services;
using ResourceGrab.App.ViewModels;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Services.ShortDrama;
using ResourceGrab.Core.Sources.ShortDrama;

namespace ResourceGrab.App.Views;

/// <summary>
/// 短剧播放/详情页：左边播放器 + 标题/操作/简介，右边选集与元信息，底部同类型推荐。
/// <para>
/// 在线模式走 m3u8（每次播放现取签名地址）；本地模式走 drama.json + 本地 mp4，完全不联网。
/// 两种模式共用同一套选集、进度、追剧/收藏与信息区。
/// </para>
/// </summary>
public partial class ShortDramaDetailView : UserControl
{
    private readonly IShortDramaSource? _source;
    private readonly ILogger? _logger;
    private readonly DramaMarkService? _marks;
    private readonly DramaWatchProgressService? _progress;

    private ShortDramaDetail? _detail;
    private LocalDrama? _local;
    private int _currentEpisode = 1;
    private int? _playingEpisode;
    private bool _descExpanded;
    private int _loadVersion;
    private int _episodeVersion;

    private bool _autoPlayNext = true;
    private bool _pageFocus;
    private bool _nearEnd;
    private bool _wasPlaying;
    private bool _switchingEpisode;
    private bool _layingOutStage;
    private bool _relatedHasItems;

    private readonly HashSet<int> _watched = [];
    private HashSet<int> _downloaded = [];
    private readonly List<(int Number, Button Button)> _episodeButtons = [];
    private readonly DispatcherTimer _watchTimer;

    private int _relatedVersion;
    private int _relatedPage = 1;
    private bool _following;
    private bool _favorited;

    /// <summary>播放器画面比例：自动识别 / 手动竖屏 / 手动横屏。</summary>
    private enum AspectMode { Auto, Portrait, Landscape }
    private AspectMode _aspectMode = AspectMode.Auto;

    /// <summary>返回列表。</summary>
    public event EventHandler? BackRequested;

    /// <summary>页面全屏开关（MainWindow 据此收起/恢复左侧导航）。</summary>
    public event EventHandler<bool>? PageFocusChanged;

    /// <summary>点击「猜你喜欢」卡片，请求打开另一部短剧（MainWindow 负责换页）。</summary>
    public event EventHandler<ShortDramaSummary>? RelatedOpenRequested;

    public ShortDramaDetailView()
    {
        InitializeComponent();
        _source = App.Services?.GetService<IShortDramaSource>();
        _logger = App.Services?.GetService<ILogger>();
        _marks = App.Services?.GetService<DramaMarkService>();
        _progress = App.Services?.GetService<DramaWatchProgressService>();

        PlayerHint.Visibility = Visibility.Collapsed;
        DownloadMenu.Opened += (_, _) => UpdateDownloadMenu();
        Player.ConfigureForDrama();
        Player.KeepAliveOnUnload = true;
        Player.SetAspectState(true, true);
        AutoPlayToggle.IsChecked = true;

        Player.VideoAspectChanged += (_, _) => OnVideoAspectChanged();
        Player.AspectToggleRequested += (_, _) => ToggleAspectMode();
        Player.AspectAutoRequested += (_, _) => UseAutoAspect();
        Player.PreviousEpisodeRequested += (_, _) => PlayPrevious();
        Player.NextEpisodeRequested += (_, _) => PlayNext();
        Player.EpisodeListRequested += (_, _) => FocusEpisodeList();
        Player.PageFullScreenRequested += (_, _) => SetPageFocus(!_pageFocus);
        Player.MiniWindowChanged += (_, _) =>
        {
            if (Player.IsMiniWindow && _pageFocus) SetPageFocus(false);
        };

        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchTimer.Tick += (_, _) => OnWatchTick();

        Loaded += (_, _) =>
        {
            ApplyPlayerStageLayout();
            _watchTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            SaveProgress();
            // 小窗是独立置顶窗口：切到漫画等 Tab 时页面会 Unloaded，
            // 但不能暂停播放，也不能停掉进度计时器，否则小窗会停在原地。
            if (Player.IsMiniWindow)
            {
                SetPageFocus(false);
                return;
            }
            _watchTimer.Stop();
            // 只暂停不 Stop：切换顶栏 Tab 时保留 Player、流地址、解码器与播放位置，
            // 回来还是同一集、同一进度；Stop 会丢流并回到海报。
            Player.Pause();
            SetPageFocus(false);
        };
        PageScroll.SizeChanged += (_, _) => ApplyPlayerStageLayout();
        SizeChanged += (_, _) =>
        {
            UpdateSideWidth();
            ApplyPlayerStageLayout();
        };
    }

    // ====================== 在线模式 ======================

    /// <summary>打开一部在线短剧。历史/收藏/推荐三个入口都调它。</summary>
    public async void LoadAsync(ShortDramaSummary summary)
        => await LoadCoreAsync(summary, 1, autoPlay: false);

    /// <summary>直接播放某一集（列表页悬停播放键）。</summary>
    public async void LoadAndPlayAsync(ShortDramaSummary summary, int episode)
        => await LoadCoreAsync(summary, Math.Max(1, episode), autoPlay: true);

    private async Task LoadCoreAsync(ShortDramaSummary summary, int startEpisode, bool autoPlay)
    {
        SaveProgress();
        SetPageFocus(false);
        _local = null;
        _detail = null;
        var version = ++_loadVersion;
        _episodeVersion++;
        _relatedVersion++;
        _relatedPage = 1;
        _currentEpisode = Math.Max(1, startEpisode);
        _playingEpisode = null;
        _nearEnd = false;
        _wasPlaying = false;
        _switchingEpisode = false;
        _watched.Clear();
        _downloaded.Clear();

        _aspectMode = AspectMode.Auto;
        Player.ResetVideoAspect();
        UpdateAspectUi();

        var saved = _progress?.Get(summary.Id);
        if (saved is not null)
        {
            _watched.UnionWith(saved.WatchedEpisodes.Where(n => n > 0));
            if (!autoPlay && startEpisode <= 1 && saved.LastEpisode > 1)
                _currentEpisode = saved.LastEpisode;
        }

        SourcePageButton.Visibility = Visibility.Visible;
        DeleteButton.Visibility = Visibility.Collapsed;
        TitleText.Text = summary.Title;
        CrumbText.Text = string.IsNullOrWhiteSpace(summary.ChannelName)
            ? $"短剧 / {summary.Title}"
            : $"短剧 / {summary.ChannelName} / {summary.Title}";
        MetaText.Inlines.Clear();
        MetaText.Inlines.Add(new Run("正在加载详情…") { Foreground = (Brush)FindResource("TextDisabledBrush") });
        DescText.Text = "";
        TagList.ItemsSource = null;
        EpisodePanel.Children.Clear();
        _episodeButtons.Clear();
        Player.Stop();
        Player.StreamUrl = "";
        Player.ShowPlayButton = false;
        Player.PosterUrl = summary.CoverUrl;
        Player.PosterFallbackBrush = DramaCoverPlaceholder.Build(summary.Id + summary.Title);
        Player.PosterFallbackTextValue = DramaCoverPlaceholder.FirstChar(summary.Title);
        Player.PlayerTitle = summary.Title;
        Player.SourceName = summary.ChannelName;
        Player.MiniSubtitle = MiniEpisodeLabel();
        DownloadButton.IsEnabled = false;
        RelatedSection.Visibility = Visibility.Collapsed;
        _relatedHasItems = false;
        LoadSidePlaceholders(summary.ScoreText, summary.EpisodeText);
        UpdateMarkButtonsFor(summary.Id);
        SetPlayerHint("正在加载详情…");

        if (_source is null)
        {
            SetPlayerHint("短剧源未注册");
            return;
        }

        try
        {
            var detail = await _source.GetDetailAsync(summary.Id);
            if (version != _loadVersion) return;
            if (detail is null)
            {
                SetPlayerHint("详情解析失败（站点可能改版）");
                return;
            }

            _detail = detail;
            Player.PlayerTitle = detail.Title;
            Player.SourceName = detail.ChannelName;
            Player.PosterFallbackBrush = DramaCoverPlaceholder.Build(detail.Id + detail.Title);
            Player.PosterFallbackTextValue = DramaCoverPlaceholder.FirstChar(detail.Title);
            ApplyDetail(detail);
            _ = LoadPosterAsync(detail, version);
            DownloadButton.IsEnabled = true;

            var totalEpisodes = Math.Max(detail.TotalEpisodes, detail.Episodes.Count);
            var target = autoPlay
                ? Math.Clamp(startEpisode, 1, Math.Max(1, totalEpisodes))
                : Math.Clamp(_currentEpisode, 1, Math.Max(1, totalEpisodes));
            await PlayEpisodeAsync(target, startPlayback: autoPlay, preloadOnly: !autoPlay);
            if (version != _loadVersion) return;
            _ = LoadRelatedAsync(detail);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDrama] 详情加载失败 {summary.Id}: {ex.Message}");
            ToastService.ShowError(ex, "详情加载失败：");
            SetPlayerHint(ex is ShortDramaUnavailableException unavailable ? unavailable.Message : "加载失败");
        }
    }

    // ====================== 本地模式 ======================

    /// <summary>打开本地库里的一部剧（离线，不联网）。</summary>
    public void LoadLocal(LocalDrama drama)
    {
        SaveProgress();
        SetPageFocus(false);
        _local = drama;
        _detail = null;
        _loadVersion++;
        _episodeVersion++;
        _relatedVersion++;
        _playingEpisode = null;
        _nearEnd = false;
        _wasPlaying = false;
        _switchingEpisode = false;
        _watched.Clear();
        _downloaded.Clear();

        _aspectMode = AspectMode.Auto;
        Player.ResetVideoAspect();
        UpdateAspectUi();

        var meta = drama.Metadata;
        var saved = _progress?.Get(meta.Id);
        if (saved is not null) _watched.UnionWith(saved.WatchedEpisodes.Where(n => n > 0));
        _currentEpisode = Math.Max(1, saved?.LastEpisode ?? 1);
        SourcePageButton.Visibility = Visibility.Collapsed;
        DeleteButton.Visibility = Visibility.Visible;
        Player.PlayerTitle = meta.Title;
        Player.SourceName = "本地";
        CrumbText.Text = $"短剧 / 本地 / {meta.Title}";
        TitleText.Text = meta.Title;
        DescText.Text = meta.Description;
        SetMetaText(meta.ScoreText, meta.EpisodeText, null, meta.ReleaseText, meta.ChannelName);
        TagList.ItemsSource = meta.Tags;
        ScoreValueText.Text = string.IsNullOrWhiteSpace(meta.ScoreText) ? "—" : $"{meta.ScoreText} 分";
        StateValueText.Text = string.IsNullOrWhiteSpace(meta.EpisodeText) ? "—" : meta.EpisodeText;
        DurationValueText.Text = meta.Episodes.FirstOrDefault()?.DurationText is { Length: > 0 } d ? $"{d} / 集" : "—";
        ReleaseValueText.Text = string.IsNullOrWhiteSpace(meta.ReleaseText) ? "—" : meta.ReleaseText;
        ViewsValueText.Text = "—";
        LocalValueText.Text = $"{drama.DownloadedCount} / {drama.TotalEpisodes} 集 · {FormatSize(drama.TotalBytes)}";
        SideNoteText.Text = "离线播放本地已下载文件，不消耗流量；播放地址无需刷新。";
        Player.PosterUrl = drama.CoverPath ?? "";
        Player.PosterFallbackBrush = DramaCoverPlaceholder.Build(meta.Id + meta.Title);
        Player.PosterFallbackTextValue = DramaCoverPlaceholder.FirstChar(meta.Title);
        DownloadButton.IsEnabled = false;
        RelatedSection.Visibility = Visibility.Collapsed;
        _relatedHasItems = false;
        UpdateMarkButtonsFor(meta.Id);

        Player.Stop();
        Player.StreamUrl = "";
        _downloaded = drama.Metadata.Episodes
            .Where(e => e.IsDownloaded)
            .Select(e => e.Number)
            .ToHashSet();
        BuildEpisodeButtons(drama.TotalEpisodes);
        EpisodeCountText.Text = $"共 {drama.TotalEpisodes} 集";

        var first = _currentEpisode > 0 && drama.ResolveEpisodeFile(_currentEpisode) is not null
            ? _currentEpisode
            : meta.Episodes.FirstOrDefault(e => e.IsDownloaded)?.Number;
        if (first is null or <= 0)
            first = Enumerable.Range(1, Math.Max(1, drama.TotalEpisodes)).FirstOrDefault(n => drama.ResolveEpisodeFile(n) is not null);
        if (first is null or <= 0) first = 1;
        PlayLocalEpisode(drama, first.Value, startPlayback: false);
        UpdateDownloadMenu();
    }

    // ====================== 渲染 ======================

    private void ApplyDetail(ShortDramaDetail detail)
    {
        TitleText.Text = detail.Title;
        CrumbText.Text = $"短剧 / {detail.ChannelName} / {detail.Title}";
        TitleText.ToolTip = detail.DetailUrl;
        DescText.Text = detail.Description;
        SetMetaText(detail.ScoreText, detail.EpisodeText,
            ExtractViews(detail.FactsText), detail.ReleaseText, detail.ChannelName);
        TagList.ItemsSource = detail.Tags;

        ScoreValueText.Text = string.IsNullOrWhiteSpace(detail.ScoreText) ? "—" : $"{detail.ScoreText} 分";
        StateValueText.Text = string.IsNullOrWhiteSpace(detail.EpisodeText) ? "—" : detail.EpisodeText;
        DurationValueText.Text = detail.DurationSeconds > 0
            ? $"{detail.DurationSeconds / 60}:{detail.DurationSeconds % 60:D2} / 集"
            : "—";
        ReleaseValueText.Text = string.IsNullOrWhiteSpace(detail.ReleaseText) ? "—" : detail.ReleaseText;
        ViewsValueText.Text = ExtractViews(detail.FactsText) ?? "—";
        SideNoteText.Text = "播放地址为 2 小时有效的签名链接，每次播放自动换取，不可离线分享。";

        _downloaded = LoadDownloadedEpisodes(detail);
        var totalEpisodes = Math.Max(detail.TotalEpisodes, detail.Episodes.Count);
        BuildEpisodeButtons(totalEpisodes);
        EpisodeCountText.Text = $"共 {totalEpisodes} 集";
        UpdateLocalInfo(detail, totalEpisodes);
        UpdateDownloadMenu();
        UpdateMarksForCurrent();
    }

    private void SetMetaText(string? score, string? status, string? views, string? release, string? channel)
    {
        MetaText.Inlines.Clear();
        if (!string.IsNullOrWhiteSpace(score))
        {
            MetaText.Inlines.Add(new Run($"★ {score}")
            {
                Foreground = (Brush)FindResource("WarningBrush"),
                FontWeight = FontWeights.SemiBold,
            });
        }

        void Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (MetaText.Inlines.Count > 0)
                MetaText.Inlines.Add(new Run("   ") { Foreground = (Brush)FindResource("TextDisabledBrush") });
            MetaText.Inlines.Add(new Run(text) { Foreground = (Brush)FindResource("TextSecondaryBrush") });
        }

        Add(status);
        Add(views);
        Add(string.IsNullOrWhiteSpace(release) ? null : $"{release} 上线");
        Add(channel);
    }

    private static string? ExtractViews(string? facts)
    {
        if (string.IsNullOrWhiteSpace(facts)) return null;
        foreach (var part in facts.Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains("播放", StringComparison.Ordinal)) return part;
        }
        return null;
    }

    private void LoadSidePlaceholders(string? score, string? status)
    {
        ScoreValueText.Text = string.IsNullOrWhiteSpace(score) ? "—" : $"{score} 分";
        StateValueText.Text = string.IsNullOrWhiteSpace(status) ? "—" : status;
        DurationValueText.Text = "—";
        ReleaseValueText.Text = "—";
        ViewsValueText.Text = "—";
        LocalValueText.Text = "—";
        EpisodeCountText.Text = "共 — 集";
        UpdateWatchedUi();
    }

    /// <summary>详情页海报：同样走「下载 → 解密 → 缓存」，优先吃缓存。</summary>
    private async Task LoadPosterAsync(ShortDramaDetail detail, int version)
    {
        var service = App.Services?.GetService<DramaCoverService>();
        if (service is null) return;

        var cached = service.TryGetCached(detail.Id);
        if (cached is not null)
        {
            Player.PosterUrl = cached;
            return;
        }

        try
        {
            var path = await service.EnsureAsync(detail.Id, detail.CoverUrl);
            if (version != _loadVersion || path is null) return;
            Player.PosterUrl = path;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDrama] 详情封面失败 {detail.Id}：{ex.Message}");
        }
    }

    private HashSet<int> LoadDownloadedEpisodes(ShortDramaDetail detail)
    {
        try
        {
            var library = App.Services?.GetService<ShortDramaLibraryService>();
            var folder = library?.FolderFor(detail.Title);
            if (folder is null || library is null) return [];
            var local = library.TryLoad(folder);
            return local is null
                ? []
                : local.Metadata.Episodes.Where(e => e.IsDownloaded).Select(e => e.Number).ToHashSet();
        }
        catch { return []; }
    }

    private void UpdateLocalInfo(ShortDramaDetail detail, int totalEpisodes)
    {
        if (_downloaded.Count == 0)
        {
            LocalValueText.Text = "未下载";
            return;
        }
        long bytes = 0;
        try
        {
            var library = App.Services?.GetService<ShortDramaLibraryService>();
            var local = library?.TryLoad(library.FolderFor(detail.Title));
            bytes = local?.TotalBytes ?? 0;
        }
        catch { /* 体积只是展示，读不到就显示集数 */ }
        LocalValueText.Text = bytes > 0
            ? $"{_downloaded.Count} / {totalEpisodes} 集 · {FormatSize(bytes)}"
            : $"{_downloaded.Count} / {totalEpisodes} 集";
    }

    // ====================== 选集 ======================

    private void BuildEpisodeButtons(int total)
    {
        EpisodePanel.Children.Clear();
        _episodeButtons.Clear();
        if (total <= 0) return;

        for (var i = 1; i <= total; i++)
        {
            var number = i;
            var text = new TextBlock
            {
                Text = $"{number:D2}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var content = new Grid();
            content.Children.Add(text);

            var button = new Button
            {
                Content = content,
                Width = 38,
                Height = 30,
                Margin = new Thickness(0, 0, 4, 4),
                Tag = number,
            };
            button.SetResourceReference(StyleProperty, "EpisodeButtonStyle");
            button.Click += (_, _) => SelectEpisode(number);
            EpisodePanel.Children.Add(button);
            _episodeButtons.Add((number, button));
        }

        RefreshEpisodeMarks();
    }

    private void RefreshEpisodeMarks()
    {
        var activeBg = (Brush)FindResource("PrimarySubtleBrush");
        var activeBorder = (Brush)FindResource("PrimaryBrush");
        var transparent = Brushes.Transparent;
        var accent = (Brush)FindResource("PrimaryBrush");
        var secondary = (Brush)FindResource("TextSecondaryBrush");

        foreach (var (number, button) in _episodeButtons)
        {
            var active = number == _currentEpisode;
            var watched = _watched.Contains(number);
            var downloaded = _downloaded.Contains(number);

            button.Background = active ? activeBg : transparent;
            button.BorderBrush = active ? activeBorder : transparent;
            button.Foreground = active ? accent : secondary;
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;

            var notes = new List<string> { $"第 {number} 集" };
            if (watched) notes.Add("已看");
            if (downloaded) notes.Add("已下载");
            button.ToolTip = string.Join(" · ", notes);
        }

        CurrentEpisodeText.Text = $"第 {_currentEpisode} 集";
        UpdateWatchedUi();
    }

    private string MiniEpisodeLabel()
        => $"第{Math.Max(1, _currentEpisode)}/{Math.Max(1, TotalEpisodes)}集";

    private void SelectEpisode(int number)
    {
        var total = TotalEpisodes;
        if (total <= 0) return;
        number = Math.Clamp(number, 1, total);
        if (_local is not null)
            PlayLocalEpisode(_local, number, startPlayback: true);
        else
            _ = PlayEpisodeAsync(number, startPlayback: true, preloadOnly: false);
    }

    private int TotalEpisodes => _episodeButtons.Count > 0
        ? _episodeButtons.Count
        : (_detail?.TotalEpisodes ?? _local?.TotalEpisodes ?? 0);

    private void PlayPrevious()
    {
        if (_currentEpisode <= 1)
        {
            ToastService.Show("已经是第一集");
            return;
        }
        SelectEpisode(_currentEpisode - 1);
    }

    private void PlayNext()
    {
        if (_currentEpisode >= TotalEpisodes)
        {
            ToastService.Show("已经是最后一集");
            return;
        }
        SelectEpisode(_currentEpisode + 1);
    }

    private void FocusEpisodeList()
    {
        if (_pageFocus) SetPageFocus(false);
        var match = _episodeButtons.FirstOrDefault(x => x.Number == _currentEpisode);
        if (match.Button is { } button)
        {
            button.BringIntoView();
            button.Focus();
        }
    }

    // ====================== 播放 ======================

    private async Task PlayEpisodeAsync(int number, bool startPlayback, bool preloadOnly)
    {
        if (_detail is null || _source is null) return;
        var total = Math.Max(1, TotalEpisodes);
        number = Math.Clamp(number, 1, total);
        var version = ++_episodeVersion;
        _currentEpisode = number;
        _switchingEpisode = true;
        _nearEnd = false;
        _wasPlaying = false;
        Player.MiniSubtitle = MiniEpisodeLabel();
        UpdateEpisodeUi();
        SaveProgress();
        SetPlayerHint($"正在获取第 {number} 集播放地址…");

        try
        {
            var frame = await _source.GetPlayFrameAsync(_detail.Id, number);
            if (version != _episodeVersion) return;
            if (frame is null || string.IsNullOrWhiteSpace(frame.StreamUrl))
            {
                SetPlayerHint("取流失败，可点「刷新地址」重试");
                return;
            }

            if (startPlayback)
            {
                if (Player.IsMiniWindow) Player.StopForEpisodeSwitch();
                else Player.Stop();
            }
            Player.StreamUrl = frame.StreamUrl;
            Player.ShowPlayButton = true;
            _playingEpisode = number;
            PlayerHint.Visibility = Visibility.Collapsed;
            UpdatePlayButtonText();
            if (startPlayback) Player.Play();
        }
        catch (Exception ex)
        {
            if (version != _episodeVersion) return;
            _logger?.Warn($"[ShortDrama] 取流失败 ep{number}: {ex.Message}");
            SetPlayerHint("取流失败（线路可能已失效）");
            ToastService.ShowError(ex, "取流失败：");
        }
        finally
        {
            if (version == _episodeVersion) _switchingEpisode = false;
        }
    }

    private void PlayLocalEpisode(LocalDrama drama, int number, bool startPlayback)
    {
        var total = Math.Max(1, drama.TotalEpisodes);
        number = Math.Clamp(number, 1, total);
        var path = drama.ResolveEpisodeFile(number);
        if (path is null)
        {
            _currentEpisode = number;
            UpdateEpisodeUi();
            SaveProgress();
            SetPlayerHint($"第 {number} 集还没下载");
            return;
        }

        _currentEpisode = number;
        _playingEpisode = number;
        _switchingEpisode = true;
        _nearEnd = false;
        _wasPlaying = false;
        Player.MiniSubtitle = MiniEpisodeLabel();
        SaveProgress();
        if (startPlayback)
        {
            if (Player.IsMiniWindow) Player.StopForEpisodeSwitch();
            else Player.Stop();
        }
        Player.StreamUrl = path;
        Player.PosterUrl = drama.CoverPath ?? "";
        Player.ShowPlayButton = true;
        PlayerHint.Visibility = Visibility.Collapsed;
        UpdateEpisodeUi();
        if (startPlayback) Player.Play();
        _switchingEpisode = false;
    }

    private void UpdateEpisodeUi()
    {
        Player.MiniSubtitle = MiniEpisodeLabel();
        RefreshEpisodeMarks();
        UpdatePlayButtonText();
        Player.SetEpisodeNavigation(_currentEpisode > 1, _currentEpisode < TotalEpisodes);
    }

    private void UpdatePlayButtonText()
    {
        var number = Math.Max(1, _currentEpisode);
        var playing = Player.IsPlaying;
        PlayButtonText.Text = playing
            ? "暂停"
            : Player.IsPaused ? $"继续第 {number} 集" : $"从第 {number} 集继续";
        PlayButtonIcon.Data = (Geometry)FindResource(playing ? "IconPause" : "IconPlay");
    }

    private void UpdateWatchedUi()
    {
        var total = TotalEpisodes;
        var count = total > 0 ? _watched.Count(n => n >= 1 && n <= total) : 0;
        WatchedText.Text = $"已看 {count}/{Math.Max(total, 0)}";
        WatchedBar.Value = total > 0 ? count * 100.0 / total : 0;
    }

    private void MarkWatched(int number)
    {
        if (number < 1) return;
        if (_watched.Add(number))
        {
            RefreshEpisodeMarks();
            UpdateWatchedUi();
            SaveProgress();
        }
    }

    /// <summary>保存「上次看到第几集 + 已看完集号」；切剧/切页/关窗时调用。</summary>
    private void SaveProgress()
    {
        var id = _detail?.Id ?? _local?.Id;
        if (string.IsNullOrWhiteSpace(id)) return;
        _progress?.Save(id, _currentEpisode, _watched);
    }

    private void OnWatchTick()
    {
        var total = TotalEpisodes;
        if (total <= 0) return;
        var duration = Player.DurationMs;
        var position = Player.CurrentPositionMs;

        if (duration > 1500 && position >= duration - 1500) _nearEnd = true;
        if (duration > 0 && position >= duration * 0.9 && _playingEpisode == _currentEpisode)
            MarkWatched(_currentEpisode);

        if (_switchingEpisode)
        {
            _wasPlaying = false;
            return;
        }

        if (_wasPlaying && !Player.IsPlaying && _nearEnd && position >= duration - 3000)
        {
            _nearEnd = false;
            MarkWatched(_currentEpisode);
            if (_autoPlayNext && _currentEpisode < total)
            {
                SelectEpisode(_currentEpisode + 1);
                return;
            }
            UpdatePlayButtonText();
        }

        if (Player.IsPlaying != _wasPlaying) UpdatePlayButtonText();
        _wasPlaying = Player.IsPlaying;
    }

    private void PlayerStage_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyPlayerStageLayout();

    private void OnVideoAspectChanged()
    {
        UpdateAspectUi();
        if (_aspectMode == AspectMode.Auto) ApplyPlayerStageLayout();
    }

    /// <summary>当前生效比例：自动跟随首帧；未知时短剧默认 9:16；手动时用标准 9:16 / 16:9。</summary>
    private double EffectiveAspectRatio()
    {
        if (_aspectMode == AspectMode.Portrait) return 9.0 / 16.0;
        if (_aspectMode == AspectMode.Landscape) return 16.0 / 9.0;
        var actual = Player.VideoAspectRatio;
        return actual > 0 ? actual : 9.0 / 16.0;
    }

    private bool IsPortraitEffective() => EffectiveAspectRatio() < 1.0;

    private void UpdateAspectUi() => Player.SetAspectState(IsPortraitEffective(), _aspectMode == AspectMode.Auto);

    /// <summary>「竖屏/横屏」按钮：在当前方向的反方向锁定，不再跟随首帧。</summary>
    private void ToggleAspectMode()
    {
        _aspectMode = IsPortraitEffective() ? AspectMode.Landscape : AspectMode.Portrait;
        UpdateAspectUi();
        ApplyPlayerStageLayout();
    }

    /// <summary>设置菜单「自动识别画面比例」：清除手动锁定，重新跟随首帧。</summary>
    private void UseAutoAspect()
    {
        _aspectMode = AspectMode.Auto;
        UpdateAspectUi();
        ApplyPlayerStageLayout();
    }

    /// <summary>
    /// 播放器舞台尺寸：横屏正常模式铺满左列；竖屏按新设计取
    /// min(可用视口高度 - 100, 760)，播放框按 9:16 居中，两侧留出舞台底色。
    /// 页面全屏时两种比例都尽量铺满可用高度。
    /// </summary>
    private void ApplyPlayerStageLayout()
    {
        var stageWidth = PlayerStage.ActualWidth;
        if (_layingOutStage) return;
        if (stageWidth <= 1) return;

        var ratio = EffectiveAspectRatio();
        var portrait = ratio < 1;
        var viewport = PageScroll.ViewportHeight > 1
            ? PageScroll.ViewportHeight
            : Math.Max(360, PageRoot.ActualHeight - 24);

        _layingOutStage = true;
        try
        {
            double stageHeight;
            double frameWidth;
            double frameHeight;
            var inner = Math.Max(120, stageWidth - 2);

            if (_pageFocus)
            {
                stageHeight = Math.Max(320, viewport - 12);
                if (!portrait)
                {
                    frameWidth = Math.Min(inner, (stageHeight - 2) * ratio);
                    frameHeight = frameWidth / ratio;
                }
                else
                {
                    frameHeight = stageHeight - 2;
                    frameWidth = frameHeight * ratio;
                    if (frameWidth > inner)
                    {
                        frameWidth = inner;
                        frameHeight = frameWidth / ratio;
                    }
                }
            }
            else if (!portrait)
            {
                stageHeight = Math.Clamp(inner / ratio, 300, 680);
                frameWidth = inner;
                frameHeight = stageHeight - 2;
            }
            else
            {
                stageHeight = Math.Clamp(viewport - 100, 420, 760);
                frameHeight = stageHeight - 2;
                frameWidth = frameHeight * ratio;
                if (frameWidth > inner)
                {
                    frameWidth = inner;
                    frameHeight = frameWidth / ratio;
                }
            }

            if (double.IsNaN(PlayerStage.Height) || Math.Abs(PlayerStage.Height - stageHeight) > 1)
                PlayerStage.Height = stageHeight;

            if (!portrait && !_pageFocus)
            {
                PlayerFrame.Width = double.NaN;
                PlayerFrame.Height = double.NaN;
                PlayerFrame.HorizontalAlignment = HorizontalAlignment.Stretch;
                PlayerFrame.VerticalAlignment = VerticalAlignment.Stretch;
            }
            else
            {
                PlayerFrame.Width = Math.Max(160, frameWidth);
                PlayerFrame.Height = Math.Max(90, frameHeight);
                PlayerFrame.HorizontalAlignment = HorizontalAlignment.Center;
                PlayerFrame.VerticalAlignment = VerticalAlignment.Center;
            }
        }
        finally
        {
            _layingOutStage = false;
        }
    }

    private void SetPlayerHint(string text)
    {
        PlayerHint.Text = text;
        PlayerHint.Visibility = Visibility.Visible;
    }

    // ====================== 页面全屏 ======================

    /// <summary>页面全屏：隐藏标题/简介/侧栏/推荐，只留播放器铺满内容区。</summary>
    public void SetPageFocus(bool on)
    {
        if (_pageFocus == on) return;
        _pageFocus = on;

        BreadcrumbRow.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        TitleSection.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        SynopsisSection.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        DramaSide.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        RelatedSection.Visibility = on || !_relatedHasItems ? Visibility.Collapsed : Visibility.Visible;
        ExitFocusButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SideColumn.Width = on ? new GridLength(0) : new GridLength(CurrentSideWidth());
        DramaMain.Margin = on ? new Thickness(0) : new Thickness(0, 0, 22, 0);
        PageRoot.Margin = on ? new Thickness(14, 10, 14, 12) : new Thickness(24, 12, 24, 14);
        PageScroll.VerticalScrollBarVisibility = on ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

        if (on) PageScroll.ScrollToTop();
        PageFocusChanged?.Invoke(this, on);
        Dispatcher.BeginInvoke(new Action(ApplyPlayerStageLayout), DispatcherPriority.Loaded);
    }

    public void ExitPageFocus() => SetPageFocus(false);

    private void ExitFocus_Click(object sender, RoutedEventArgs e) => SetPageFocus(false);

    private double CurrentSideWidth() => PageRoot.ActualWidth > 0 && PageRoot.ActualWidth < 1280 ? 292 : 318;

    private void UpdateSideWidth()
    {
        if (_pageFocus) return;
        var width = CurrentSideWidth();
        if (Math.Abs(SideColumn.Width.Value - width) > 1)
            SideColumn.Width = new GridLength(width);
    }

    // ====================== 猜你喜欢 ======================

    private async Task LoadRelatedAsync(ShortDramaDetail detail)
    {
        if (_source is null) return;
        var version = ++_relatedVersion;
        try
        {
            var listing = await _source.GetFeedAsync(DramaFeedKind.Recommend, _relatedPage);
            if (version != _relatedVersion) return;
            var items = listing.Items.Where(x => !string.Equals(x.Id, detail.Id, StringComparison.Ordinal)).ToList();
            if (items.Count == 0 && _relatedPage > 1)
            {
                _relatedPage = 1;
                listing = await _source.GetFeedAsync(DramaFeedKind.Recommend, 1);
                if (version != _relatedVersion) return;
                items = listing.Items.Where(x => !string.Equals(x.Id, detail.Id, StringComparison.Ordinal)).ToList();
            }

            var ranked = items
                .Select(x => (Item: x, Score: x.Tags.Intersect(detail.Tags, StringComparer.OrdinalIgnoreCase).Count() * 10 + ParseScore(x.ScoreText)))
                .OrderByDescending(x => x.Score)
                .Select(x => x.Item)
                .Take(7)
                .ToList();
            if (ranked.Count == 0)
            {
                RelatedSection.Visibility = Visibility.Collapsed;
                _relatedHasItems = false;
                return;
            }

            var adapters = ranked
                .Select(s => new ShortDramaCardAdapter(s, OpenRelated, OpenRelated))
                .ToList();
            RelatedHost.ItemsSource = adapters;
            RelatedSubtitleText.Text = $"同类型 · {adapters.Count} 部";
            _relatedHasItems = true;
            if (!_pageFocus) RelatedSection.Visibility = Visibility.Visible;
            _ = FillRelatedCoversAsync(adapters, version);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDrama] 猜你喜欢加载失败: {ex.Message}");
        }
    }

    private void OpenRelated(ShortDramaSummary summary)
    {
        if (RelatedOpenRequested is not null)
        {
            RelatedOpenRequested.Invoke(this, summary);
            return;
        }
        _ = LoadCoreAsync(summary, 1, autoPlay: false);
    }

    private void RelatedRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is null) return;
        _relatedPage = _relatedPage >= 3 ? 1 : _relatedPage + 1;
        _ = LoadRelatedAsync(_detail);
    }

    private static double ParseScore(string? text)
        => double.TryParse(text, out var value) ? value : 0;

    private async Task FillRelatedCoversAsync(List<ShortDramaCardAdapter> cards, int version)
    {
        var service = App.Services?.GetService<DramaCoverService>();
        if (service is null) return;
        foreach (var card in cards)
        {
            if (version != _relatedVersion) return;
            if (card.Summary is not { } summary) continue;
            var cached = service.TryGetCached(summary.Id);
            if (cached is not null)
            {
                card.SetCoverUrl(cached);
                continue;
            }
            try
            {
                var path = await service.EnsureAsync(summary.Id, summary.CoverUrl);
                if (version != _relatedVersion) return;
                if (path is not null) card.SetCoverUrl(path);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"[ShortDrama] 相关推荐封面失败 {summary.Id}: {ex.Message}");
            }
        }
    }

    // ====================== 追剧 / 收藏 ======================

    private void UpdateMarkButtonsFor(string id)
    {
        _following = !string.IsNullOrEmpty(id) && _marks?.IsMarked(DramaMarkKind.Follow, id) == true;
        _favorited = !string.IsNullOrEmpty(id) && _marks?.IsMarked(DramaMarkKind.Favorite, id) == true;
        UpdateMarkButtons();
    }

    private void UpdateMarksForCurrent()
    {
        var id = _detail?.Id ?? _local?.Id ?? "";
        _following = !string.IsNullOrEmpty(id) && _marks?.IsMarked(DramaMarkKind.Follow, id) == true;
        _favorited = !string.IsNullOrEmpty(id) && _marks?.IsMarked(DramaMarkKind.Favorite, id) == true;
        UpdateMarkButtons();
    }

    private void UpdateMarkButtons()
    {
        FollowButtonText.Text = _following ? "已追剧" : "追剧";
        FollowIcon.Data = (Geometry)FindResource(_following ? "IconCheck" : "IconPlus");
        FavoriteButtonText.Text = _favorited ? "已收藏" : "收藏";
        FavoriteIcon.Fill = _favorited ? (Brush)FindResource("DangerBrush") : Brushes.Transparent;
    }

    private void Follow_Click(object sender, RoutedEventArgs e) => ToggleMark(DramaMarkKind.Follow);

    private void Favorite_Click(object sender, RoutedEventArgs e) => ToggleMark(DramaMarkKind.Favorite);

    private void ToggleMark(DramaMarkKind kind)
    {
        var id = _detail?.Id ?? _local?.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            ToastService.Show("详情还没加载完");
            return;
        }

        var title = _detail?.Title ?? _local?.Title ?? "";
        var cover = _detail?.CoverUrl ?? _local?.CoverPath ?? "";
        var channel = _detail?.ChannelName ?? _local?.Metadata.ChannelName ?? "";
        var episode = _detail?.EpisodeText ?? _local?.Metadata.EpisodeText ?? "";
        var score = _detail?.ScoreText ?? _local?.Metadata.ScoreText ?? "";
        var now = _marks?.Toggle(kind, id, title, cover, channel, episode, score)
                  ?? (kind == DramaMarkKind.Follow ? !_following : !_favorited);

        if (kind == DramaMarkKind.Follow) _following = now;
        else _favorited = now;
        UpdateMarkButtons();

        ToastService.Show(kind switch
        {
            DramaMarkKind.Follow => now ? "已加入追剧" : "已取消追剧",
            _ => now ? "已收藏" : "已取消收藏",
        });
    }

    // ====================== 交互 ======================

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_playingEpisode == _currentEpisode && !string.IsNullOrEmpty(Player.StreamUrl))
        {
            if (Player.IsPlaying || Player.IsPaused)
            {
                Player.TogglePause();
                UpdatePlayButtonText();
            }
            else
            {
                Player.Play();
                UpdatePlayButtonText();
            }
            return;
        }

        if (_local is not null) PlayLocalEpisode(_local, _currentEpisode, startPlayback: true);
        else _ = PlayEpisodeAsync(_currentEpisode, startPlayback: true, preloadOnly: false);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_local is not null) PlayLocalEpisode(_local, _currentEpisode, startPlayback: true);
        else _ = PlayEpisodeAsync(_currentEpisode, startPlayback: true, preloadOnly: false);
    }

    private void DescToggle_Click(object sender, RoutedEventArgs e)
    {
        _descExpanded = !_descExpanded;
        DescText.MaxHeight = _descExpanded ? double.PositiveInfinity : 46;
        DescToggleButton.Content = _descExpanded ? "收起" : "展开全部";
    }

    private void AutoPlayToggle_Click(object sender, RoutedEventArgs e)
    {
        _autoPlayNext = AutoPlayToggle.IsChecked == true;
        ToastService.Show(_autoPlayNext ? "已开启自动连播下一集" : "已关闭自动连播");
    }

    private void OpenDetailPage_Click(object sender, RoutedEventArgs e)
    {
        var url = _detail?.DetailUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            var folder = CurrentFolder();
            if (folder is not null && Directory.Exists(folder))
            {
                try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
                catch (Exception ex) { ToastService.ShowError(ex, "打开目录失败："); }
            }
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { ToastService.ShowError(ex, "打开浏览器失败："); }
    }

    private void Share_Click(object sender, RoutedEventArgs e)
    {
        var text = _detail?.DetailUrl;
        if (string.IsNullOrWhiteSpace(text)) text = _local?.FolderPath;
        if (string.IsNullOrWhiteSpace(text))
        {
            ToastService.Show("暂无可分享的链接");
            return;
        }
        try
        {
            Clipboard.SetText(text);
            ToastService.Show("链接已复制");
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex, "复制失败：");
        }
    }

    private void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateDownloadMenu();
        DownloadMenu.PlacementTarget = DownloadButton;
        DownloadMenu.IsOpen = true;
    }

    private void UpdateDownloadMenu()
    {
        var service = App.Services?.GetService<ShortDramaDownloadService>();
        var available = service is not null && _local is null && _detail is not null;
        MenuDownloadEpisode.IsEnabled = available;
        MenuDownloadMissing.IsEnabled = available;
        MenuDownloadAll.IsEnabled = available;

        if (_detail is not null)
        {
            MenuDownloadEpisode.Header = $"下载本集    第 {_currentEpisode} 集";
            MenuDownloadMissing.Header = "下载未看集";
            MenuDownloadAll.Header = $"下载全集    {TotalEpisodes} 集";
        }

        var folder = CurrentFolder();
        MenuOpenFolder.IsEnabled = folder is not null && Directory.Exists(folder);
        if (_detail is null)
        {
            DownloadButton.IsEnabled = false;
            DownloadButton.ToolTip = "已下载的剧集直接从本地播放";
            return;
        }
        DownloadButton.ToolTip = VideoDownloadServiceIsAvailable()
            ? "下载到本地（可离线播放）"
            : "未找到 ffmpeg，无法下载";
        DownloadButton.IsEnabled = VideoDownloadServiceIsAvailable();
    }

    private static bool VideoDownloadServiceIsAvailable()
        => ResourceGrab.Core.Services.VideoScrape.VideoDownloadService.FindFfmpeg() is not null;

    private string? CurrentFolder()
    {
        try
        {
            if (_local is not null) return _local.FolderPath;
            if (_detail is null) return null;
            return App.Services?.GetService<ShortDramaLibraryService>()?.FolderFor(_detail.Title);
        }
        catch { return null; }
    }

    private async void MenuDownloadEpisode_Click(object sender, RoutedEventArgs e)
        => await EnqueueAsync(episodes: [_currentEpisode], label: $"第 {_currentEpisode} 集");

    private async void MenuDownloadMissing_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is null) return;
        var downloaded = LoadDownloadedEpisodes(_detail);
        var missing = _detail.Episodes.Select(x => x.Number).Where(n => !downloaded.Contains(n)).ToArray();
        if (missing.Length == 0)
        {
            ToastService.Show("全部集数都已下载");
            return;
        }
        await EnqueueAsync(missing, $"未下载的 {missing.Length} 集");
    }

    private async void MenuDownloadAll_Click(object sender, RoutedEventArgs e)
        => await EnqueueAsync(episodes: null, label: "全剧");

    private async Task EnqueueAsync(IReadOnlyCollection<int>? episodes, string label)
    {
        if (_detail is null) return;
        var service = App.Services?.GetService<ShortDramaDownloadService>();
        if (service is null)
        {
            ToastService.Show("下载服务未注册");
            return;
        }

        DownloadButton.IsEnabled = false;
        try
        {
            ToastService.Show($"正在获取「{label}」的播放地址…");
            var result = await service.EnqueueDramaAsync(_detail, episodes);
            ToastService.Show(result.Queued > 0
                ? $"已加入下载队列：{result.Queued} 集（跳过 {result.Skipped}）"
                : $"没有需要下载的集（已跳过 {result.Skipped}）");
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDrama] 下载入队失败：{ex.Message}");
            ToastService.ShowError(ex, "下载失败：");
        }
        finally
        {
            DownloadButton.IsEnabled = VideoDownloadServiceIsAvailable();
            UpdateDownloadMenu();
        }
    }

    private void DeleteDrama_Click(object sender, RoutedEventArgs e)
    {
        if (_local is null) return;
        var confirm = MessageBox.Show(
            $"确定删除《{_local.Title}》的全部本地文件？\n（{_local.DownloadedCount} 集 · {FormatSize(_local.TotalBytes)}，会进回收站）",
            "删除本剧", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        var library = App.Services?.GetService<ShortDramaLibraryService>();
        if (library is null) return;
        if (library.DeleteDrama(_local))
        {
            ToastService.Show("已删除，可在回收站找回");
            _local = null;
            BackRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            ToastService.Show("删除失败（可能文件被占用）");
        }
    }

    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = CurrentFolder();
        if (folder is null || !Directory.Exists(folder))
        {
            ToastService.Show("还没有下载文件");
            return;
        }
        try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception ex) { ToastService.ShowError(ex, "打开目录失败："); }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "0 MB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F0} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB",
    };
}
