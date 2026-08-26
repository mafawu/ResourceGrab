using System.Windows.Input;
using ResourceGrab.App.Common;

namespace ResourceGrab.App.ViewModels;

/// <summary>卡片视觉类型。</summary>
public enum MediaVisualKind
{
    PortraitCover,
    WideThumb,
    TextCover
}

/// <summary>卡片上的徽标。</summary>
public sealed record MediaBadge(string Text, string? Color = null);

/// <summary>卡片底部统计项。</summary>
public sealed record CardStat(string Label, string Value);

/// <summary>卡片进度信息。</summary>
public sealed record CardProgress(double Value, double Maximum, string? Text = null);

/// <summary>
/// 统一卡片 ViewModel 基类。四种媒体类型的卡片都必须继承此模型，
/// 由 MediaCard 控件根据 MediaVisualKind 选择渲染模板。
/// </summary>
public abstract class MediaCardViewModel : ObservableObject
{
    public abstract string CardId { get; }
    public abstract string Title { get; }
    public abstract MediaVisualKind VisualKind { get; }

    public virtual string? Subtitle { get; set; }
    public virtual string? SecondaryTitle { get; set; }
    public virtual object? Cover { get; set; }
    public virtual string? CoverFallbackText { get; set; }

    public virtual IReadOnlyList<string> Tags { get; set; } = [];
    public virtual IReadOnlyList<MediaBadge> Badges { get; set; } = [];
    public virtual CardProgress? Progress { get; set; }
    public virtual IReadOnlyList<CardStat> Stats { get; set; } = [];

    private bool _isSelected;
    public bool IsSelectable { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public virtual ICommand? OpenCommand { get; set; }
    public virtual ICommand? PrimaryCommand { get; set; }
    public virtual ICommand? SecondaryCommand { get; set; }
    public virtual ICommand? FavoriteCommand { get; set; }
}
