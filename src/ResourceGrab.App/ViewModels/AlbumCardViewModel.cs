using System.Windows.Input;
using ResourceGrab.App.Common;

namespace ResourceGrab.App.ViewModels;

/// <summary>搜索结果 / 收藏列表中的漫画卡片。</summary>
public class AlbumCardViewModel : ObservableObject
{
    public string Id { get; init; } = "";

    /// <summary>内容源 id，供下载完成后刷新徽章时精确匹配。</summary>
    public string SourceId { get; init; } = "";

    /// <summary>聚合搜索时显示来源站点（如 "禁漫天堂"）；单源模式为空。</summary>
    public string SourceBadge { get; init; } = "";

    /// <summary>加载封面需要的请求头（防盗链 Referer 等）。</summary>
    public IReadOnlyDictionary<string, string>? ImageHeaders { get; init; }

    public string Kind { get; init; } = "Manga";
    public string Name { get; init; } = "";
    public string AuthorText { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public bool IsFavorite { get; init; }

    /// <summary>是否已下载到本地（卡片右上角显示徽章）。</summary>
    private bool _isDownloaded;
    public bool IsDownloaded
    {
        get => _isDownloaded;
        set => SetProperty(ref _isDownloaded, value);
    }

    public ICommand? OpenCommand { get; set; }
    public ICommand? DownloadCommand { get; set; }
}

/// <summary>章节卡片（支持框选状态）。</summary>
public class ChapterCardViewModel : ObservableObject
{
    public string ChapterId { get; init; } = "";
    public string AlbumId { get; init; } = "";
    public string Title { get; init; } = "";

    /// <summary>在线阅读命令（打开在线阅读器并跳到本章）。</summary>
    public ICommand? ReadCommand { get; set; }

    private string _pageCountText = "";
    /// <summary>章节页数（异步加载后显示，如 "24P"）。</summary>
    public string PageCountText
    {
        get => _pageCountText;
        set
        {
            if (SetProperty(ref _pageCountText, value))
            {
                OnPropertyChanged(nameof(HasPageCount));
            }
        }
    }

    public bool HasPageCount => !string.IsNullOrEmpty(_pageCountText);

    private bool _isDownloaded;
    public bool IsDownloaded
    {
        get => _isDownloaded;
        set => SetProperty(ref _isDownloaded, value);
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set => SetProperty(ref _isDownloading, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
