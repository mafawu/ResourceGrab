using System.Windows.Media;

namespace YingkuHome.Models;

/// <summary>One demo title. Mirrors the fields a real VideoFolder/VideoItem would expose.</summary>
public sealed class DemoItem
{
    public required string Code { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }

    /// <summary>Two-line hero headline (may contain \n).</summary>
    public required string HeroTitle { get; init; }

    public required string Year { get; init; }
    public required string Duration { get; init; }
    public required string Actors { get; init; }
    public required string Description { get; init; }

    public ImageSource? Poster { get; init; }
    public ImageSource? Still { get; init; }
    public ImageSource? Backdrop { get; init; }
}

public sealed class HomeViewModel
{
    public IReadOnlyList<DemoItem> RecentItems { get; init; } = [];
    public IReadOnlyList<DemoItem> AddedItems { get; init; } = [];
}
