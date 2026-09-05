using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Common;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Services;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;

namespace ResourceGrab.App.ViewModels;

/// <summary>在线漫画卡片适配器：包装 AlbumCardViewModel 为统一 MediaCard。</summary>
public sealed class MangaOnlineCardAdapter : MediaCardViewModel
{
    private readonly AlbumCardViewModel _inner;

    public MangaOnlineCardAdapter(AlbumCardViewModel inner)
    {
        _inner = inner;
        OpenCommand = inner.OpenCommand;
        PrimaryCommand = inner.DownloadCommand;
    }

    public AlbumCardViewModel Inner => _inner;

    public override string CardId => _inner.Id;
    public override string Title => _inner.Name;
    public override MediaVisualKind VisualKind => MediaVisualKind.PortraitCover;

    public override string? Subtitle => _inner.AuthorText;
    public override string? SecondaryTitle => _inner.SourceBadge;
    public override object? Cover => _inner.CoverUrl;
    public override string? CoverFallbackText => string.IsNullOrEmpty(_inner.Name) ? "?" : _inner.Name[..1].ToUpperInvariant();

    public override IReadOnlyList<MediaBadge> Badges
    {
        get
        {
            var badges = new List<MediaBadge>();
            if (_inner.IsDownloaded) badges.Add(new("已下载"));
            if (!string.IsNullOrEmpty(_inner.SourceBadge)) badges.Add(new(_inner.SourceBadge));
            return badges.AsReadOnly();
        }
    }

    public bool IsFavorite => _inner.IsFavorite;
}

/// <summary>本地漫画卡片适配器：包装 LocalComicViewModel 为统一 MediaCard。</summary>
public sealed class MangaLocalCardAdapter : MediaCardViewModel
{
    private readonly LocalComicViewModel _inner;

    public MangaLocalCardAdapter(LocalComicViewModel inner)
    {
        _inner = inner;
        OpenCommand = inner.OpenReaderCommand;
        SecondaryCommand = inner.OpenFolderCommand;
    }

    public LocalComicViewModel Inner => _inner;

    public override string CardId => _inner.FolderPath;
    public override string Title => !string.IsNullOrEmpty(_inner.NameCn) ? _inner.NameCn : _inner.Name;
    public override MediaVisualKind VisualKind => MediaVisualKind.PortraitCover;

    public override string? Subtitle => _inner.StatsText;
    public override object? Cover => _inner.CoverPath;
    public override string? CoverFallbackText => string.IsNullOrEmpty(_inner.Name) ? "?" : _inner.Name[..1];

    public override IReadOnlyList<MediaBadge> Badges
    {
        get
        {
            var badges = new List<MediaBadge>();
            if (_inner.HasMetadata) badges.Add(new("本地"));
            if (_inner.HasMetadata) badges.Add(new($"{_inner.ProgressText}"));
            else badges.Add(new("无元数据", "#FF6B35"));
            return badges.AsReadOnly();
        }
    }

    public override CardProgress? Progress =>
        _inner.Progress > 0 ? new(_inner.Progress, 100, $"{_inner.Progress:F0}%") : null;

    public bool IsFavorite => _inner.IsFavorite;
}

/// <summary>
/// 视频卡片适配器：包装本地视频库的 VideoItem 为统一 MediaCard。
/// 交互语义与原 VideoFileCard 一致：点卡片=详情（框选模式下=切换选中），
/// 悬停浮层提供播放/打开目录，右上心形收藏，左上刮削状态徽章。
/// </summary>
public sealed class VideoCardAdapter : MediaCardViewModel
{
    private static ILogger? Logger => App.Services?.GetService(typeof(ILogger)) as ILogger;

    private readonly VideoItem _item;
    private readonly Action<VideoItem>? _detailRequested;
    private readonly Action<VideoItem, bool>? _selectionChanged;

    public VideoCardAdapter(
        VideoItem item,
        Action<VideoItem>? detailRequested = null,
        Action<VideoItem, bool>? selectionChanged = null)
    {
        _item = item;
        _detailRequested = detailRequested;
        _selectionChanged = selectionChanged;

        var cover = new[] { item.PosterPath, item.CoverPath, item.ThumbnailPath }.FirstOrDefault(File.Exists);
        Cover = cover;
        CoverFallbackText = "V";
        Subtitle = JoinInfo(" ", item.DurationText, item.Resolution) is { Length: > 0 } s ? s : null;
        SecondaryTitle = JoinInfo(" · ",
            item.Number,
            item.Score > 0 ? $"★{item.Score:F1}" : "",
            FormatSize(item.FileSizeBytes)) is { Length: > 0 } info ? info : null;
        ShowUnwatchedMark = item.WatchProgress <= 0;
        IsFavorite = item.IsFavorite;
        ScrapeBadge = item.ScrapeStatus switch
        {
            ScrapeStatus.Success => ("已刮削", "SuccessBrush"),
            ScrapeStatus.NoMatch => ("未匹配", "WarningBrush"),
            ScrapeStatus.Failed => ("失败", "DangerBrush"),
            _ => ("", ""),
        };

        OpenCommand = new RelayCommand(_ =>
        {
            if (IsSelectable) { ToggleSelection(); return; }
            _detailRequested?.Invoke(_item);
        });
        PrimaryCommand = new RelayCommand(_ => Play());
        SecondaryCommand = new RelayCommand(_ => OpenFolder());
        FavoriteCommand = new RelayCommand(_ => ToggleFavorite());
        SelectionCommand = new RelayCommand(_ => ToggleSelection());

        // 选中态变化转发给宿主视图（外部直接设 IsSelected 时也同步）
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsSelected)) _selectionChanged?.Invoke(_item, IsSelected);
        };
    }

    public VideoItem Inner => _item;

    public override string CardId => _item.Id;
    public override string Title => _item.DisplayTitle;
    public override MediaVisualKind VisualKind => MediaVisualKind.WideThumb;

    /// <summary>未观看标记（左侧小蓝条），对应原 VideoFileCard 的 WatchedBar。</summary>
    public bool ShowUnwatchedMark { get; }

    public bool IsFavorite { get; private set; }

    /// <summary>(文案, 资源画刷键)；无状态时文案为空。</summary>
    public (string Text, string BrushKey) ScrapeBadge { get; }

    public ICommand SelectionCommand { get; }

    private void ToggleSelection()
    {
        if (!IsSelectable) return;
        IsSelected = !IsSelected;
    }

    private void ToggleFavorite()
    {
        _item.IsFavorite = !_item.IsFavorite;
        IsFavorite = _item.IsFavorite;
        OnPropertyChanged(nameof(IsFavorite));
    }

    private void Play()
    {
        if (!_item.FileExists)
        {
            ToastService.Show("文件不存在或已被移动", ToastKind.Error);
            Logger?.Warn($"[VideoCardAdapter] 播放失败，文件不存在: {_item.FilePath}");
            return;
        }
        try
        {
            Logger?.Info($"[VideoCardAdapter] 播放: {_item.FileName} ({_item.FilePath})");
            Process.Start(new ProcessStartInfo(_item.FilePath) { UseShellExecute = true });
            App.Services.GetRequiredService<VideoLibraryService>().RecordWatch(_item.Id);
        }
        catch (Exception ex)
        {
            Logger?.Error($"[VideoCardAdapter] 播放失败: {_item.FilePath}", ex);
            ToastService.ShowError(ex, "播放失败：");
        }
    }

    private void OpenFolder()
    {
        if (_item.FilePath.Length == 0) return;
        var dir = Path.GetDirectoryName(_item.FilePath);
        if (dir is null || !Directory.Exists(dir)) return;
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        App.Services.GetRequiredService<VideoLibraryService>().RecordFolderOpen(_item.Id);
    }

    private static string JoinInfo(string separator, params string[] parts)
        => string.Join(separator, parts.Where(s => !string.IsNullOrEmpty(s)));

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}

/// <summary>小说卡片适配器：映射小说文件信息为统一 MediaCard。</summary>
public sealed class NovelCardAdapter : MediaCardViewModel
{
    private readonly string _id;
    private readonly string _title;
    private readonly string? _author;
    private readonly long _fileSizeBytes;
    private readonly ICommand? _openCommand;

    public NovelCardAdapter(
        string id, string title, string? author = null,
        long fileSizeBytes = 0, ICommand? openCommand = null)
    {
        _id = id; _title = title; _author = author;
        _fileSizeBytes = fileSizeBytes; _openCommand = openCommand;
    }

    public override string CardId => _id;
    public override string Title => _title;
    public override MediaVisualKind VisualKind => MediaVisualKind.TextCover;

    public override string? Subtitle => _author ?? FormatSize(_fileSizeBytes);
    public override string? CoverFallbackText => string.IsNullOrEmpty(_title) ? "N" : _title[..1].ToUpperInvariant();

    public override IReadOnlyList<string> Tags => [];
    public override ICommand? OpenCommand => _openCommand;

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}

