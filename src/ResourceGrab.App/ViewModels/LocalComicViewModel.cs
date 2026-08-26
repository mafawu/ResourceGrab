using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ResourceGrab.Core.Models;

namespace ResourceGrab.App.ViewModels;

/// <summary>本地漫画卡片（仿 Green MediaCard：进度/多选/收藏/状态角标）</summary>
public class LocalComicViewModel : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string NameCn { get; init; } = "";
    public string CoverPath { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public List<string> Tags { get; init; } = new();
    public int ChapterCount { get; init; }
    public long ImageCount { get; init; }
    public bool HasMetadata { get; init; }
    public List<string> DisplayTags { get; init; } = new();
    public bool ShowNameCn => !string.IsNullOrEmpty(NameCn) && NameCn != Name;
    public string StatsText { get; init; } = "";
    public ICommand? OpenFolderCommand { get; set; }
    public ICommand? OpenReaderCommand { get; set; }
    public LocalComic Source { get; init; } = null!;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectionVisibility)); } } }
    public bool IsSelectionMode { get; set; }
    public System.Windows.Visibility SelectionVisibility => IsSelectionMode ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public bool IsFavorite => Source?.HasMetadata == true && Tags.Count > 5;
    public bool ShowFileError => !HasMetadata;
    public bool ShowArchiveIcon => false;

    public double Progress => Source?.ReadProgress ?? 0;
    public int Rating => Source?.Rating ?? 0;
    public bool HasRating => Rating > 0;
    public string RatingText => HasRating ? new string('★', Rating) + new string('☆', 5 - Rating) : "";
    public string ReadInfo => Source?.ReadCount > 0 ? $"读过 {Source.ReadCount} 次" : "";
    public bool HasReadInfo => !string.IsNullOrEmpty(ReadInfo);
    public string ProgressText => ImageCount > 0 ? $"{ImageCount}P" : $"{ChapterCount}章";

    public string TotalViews => Source?.TotalViews ?? "";
    public string Likes => Source?.Likes ?? "";
    public string CommentTotal => Source?.CommentTotal ?? "";
    public bool HasViews => !string.IsNullOrWhiteSpace(TotalViews);
    public bool HasLikes => !string.IsNullOrWhiteSpace(Likes);
    public bool HasComments => !string.IsNullOrWhiteSpace(CommentTotal);
    public bool HasPopularity => HasViews || HasLikes || HasComments;
    public string FormattedViews => FormatCount(TotalViews);
    public string FormattedLikes => FormatCount(Likes);
    public string FormattedComments => FormatCount(CommentTotal);

    private static string FormatCount(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim().Replace(",", "");
        if (!long.TryParse(s, out var n)) return raw;
        if (n >= 10000) return $"{n / 10000.0:0.#}w";
        if (n >= 1000) return $"{n / 1000.0:0.#}k";
        return n.ToString();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n=null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
