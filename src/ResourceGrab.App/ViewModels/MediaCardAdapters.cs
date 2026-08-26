using System.Windows.Input;
using ResourceGrab.App.Controls;

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

/// <summary>视频卡片适配器：映射视频文件信息为统一 MediaCard。</summary>
public sealed class VideoCardAdapter : MediaCardViewModel
{
    private readonly string _id;
    private readonly string _title;
    private readonly string? _coverPath;
    private readonly string? _duration;
    private readonly string? _resolution;
    private readonly ICommand? _playCommand;
    private readonly ICommand? _openFolderCommand;

    public VideoCardAdapter(
        string id, string title, string? coverPath = null,
        string? duration = null, string? resolution = null,
        ICommand? playCommand = null, ICommand? openFolderCommand = null)
    {
        _id = id; _title = title; _coverPath = coverPath;
        _duration = duration; _resolution = resolution;
        _playCommand = playCommand; _openFolderCommand = openFolderCommand;
    }

    public override string CardId => _id;
    public override string Title => _title;
    public override MediaVisualKind VisualKind => MediaVisualKind.WideThumb;

    public override string? Subtitle => _duration ?? "";
    public override object? Cover => _coverPath;
    public override string? CoverFallbackText => "V";

    public override IReadOnlyList<MediaBadge> Badges
    {
        get
        {
            var badges = new List<MediaBadge>();
            if (!string.IsNullOrEmpty(_resolution)) badges.Add(new(_resolution));
            return badges.AsReadOnly();
        }
    }

    public override ICommand? OpenCommand => _playCommand;
    public override ICommand? SecondaryCommand => _openFolderCommand;
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

