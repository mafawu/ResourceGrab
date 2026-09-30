using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ResourceGrab.App.Common;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 本地漫画推荐海报卡片：复用 PosterCard 壳子。
/// 支持两种绑定：本地条目（点击进本地详情/阅读器）与相关推荐条目（本地命中走本地，未命中用在线封面点开在线详情）。
/// </summary>
public class MangaPosterCard : PosterCard
{
    private LocalComic? _comic;
    private RelatedEntry? _related;

    public MangaPosterCard()
    {
        HoverText = "查看详情";
        CardClick += (_, _) => OnClick();
    }

    /// <summary>绑定本地漫画条目。</summary>
    public void Bind(LocalComic comic)
    {
        _comic = comic;
        _related = null;

        var title = !string.IsNullOrEmpty(comic.NameCn) ? comic.NameCn : comic.Name;
        CoverHeight = DefaultCoverHeight;
        CoverSource = string.IsNullOrEmpty(comic.CoverPath) ? null : comic.CoverPath;
        CoverFallback = Fallback(title);
        Title = string.IsNullOrEmpty(title) ? "未知" : title;
        Subtitle = comic.Author.Count > 0 ? string.Join(" / ", comic.Author.Take(2)) : null;
        MetaLeft = comic.ChapterCount > 0 ? $"{comic.ChapterCount} 话" : "";
        MetaRight = FormatHeat(comic);
        SetTags(comic.Tags
            .Where(t => !t.StartsWith("作品:", StringComparison.Ordinal) && !t.StartsWith("标题:", StringComparison.Ordinal))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Take(3));

        TopLeftContent = comic.Rating > 0
            ? MakeBadge(new string('★', Math.Clamp(comic.Rating, 1, 5)), new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47)))
            : null;
        TopRightContent = !comic.HasMetadata
            ? MakeBadge("无元数据", new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x35)))
            : null;
        BottomLeftContent = null;
        BottomRightContent = null;
        Progress = comic.ReadProgress > 0 ? comic.ReadProgress : 0;
        Opacity = 1;
        SetHoverActions(new (string, Action, bool)[] { ("阅读", () => Navigation.OpenReader(comic), true) });
    }

    /// <summary>绑定相关推荐条目；localMatch 非空表示该推荐已在本地库。</summary>
    public void BindRelated(RelatedEntry entry, LocalComic? localMatch, string? cachedCoverPath)
    {
        _comic = localMatch;
        _related = entry;

        var title = localMatch is not null
            ? (!string.IsNullOrEmpty(localMatch.NameCn) ? localMatch.NameCn : localMatch.Name)
            : entry.Name;
        CoverHeight = DefaultCoverHeight;
        CoverSource = localMatch is not null
            ? (string.IsNullOrEmpty(localMatch.CoverPath) ? null : localMatch.CoverPath)
            : cachedCoverPath ?? (string.IsNullOrEmpty(entry.Image) ? null : entry.Image);
        CoverFallback = Fallback(title);
        Title = string.IsNullOrEmpty(title) ? "未知" : title;
        Subtitle = localMatch is not null
            ? (localMatch.Author.Count > 0 ? string.Join(" / ", localMatch.Author.Take(2)) : null)
            : (string.IsNullOrEmpty(entry.Author) ? null : entry.Author);
        MetaLeft = localMatch is not null
            ? (localMatch.ChapterCount > 0 ? $"{localMatch.ChapterCount} 话" : "")
            : entry.Id;
        MetaRight = localMatch is not null ? FormatHeat(localMatch) : "";
        SetTags(Array.Empty<string>());
        TopLeftContent = null;
        TopRightContent = localMatch is not null
            ? MakeBadge("已下载", new SolidColorBrush(Color.FromRgb(0x6F, 0xCF, 0x97)))
            : MakeBadge("在线", new SolidColorBrush(Color.FromRgb(0x9A, 0xC1, 0xFF)));
        BottomLeftContent = null;
        BottomRightContent = null;
        Progress = localMatch?.ReadProgress > 0 ? localMatch.ReadProgress : 0;
        Opacity = 1;
        SetHoverActions(Array.Empty<(string, Action, bool)>());
    }

    private void OnClick()
    {
        if (_comic is not null)
        {
            Navigation.OpenLocalDetail(_comic);
            return;
        }
        if (_related is not null && long.TryParse(_related.Id, out var albumId))
        {
            Navigation.OpenAlbum(albumId);
        }
    }

    private static string FormatHeat(LocalComic comic)
    {
        if (!string.IsNullOrWhiteSpace(comic.Likes)) return $"赞 {comic.Likes}";
        if (!string.IsNullOrWhiteSpace(comic.TotalViews)) return $"阅 {comic.TotalViews}";
        return "";
    }

    private static string Fallback(string title)
        => string.IsNullOrEmpty(title) ? "?" : title[..1].ToUpperInvariant();
}

/// <summary>漫画推荐页横向滚动海报行构建器。</summary>
public static class MangaPosterGridHelper
{
    /// <summary>从本地漫画列表创建海报行。</summary>
    public static ScrollViewer CreateMangaPosterRow(IReadOnlyList<LocalComic> items)
    {
        var scroll = CreateScroll();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items)
        {
            var card = new MangaPosterCard();
            card.Bind(item);
            panel.Children.Add(card);
        }
        scroll.Content = panel;
        return scroll;
    }

    /// <summary>从相关推荐列表创建海报行（localMatches 按推荐 id 关联本地条目）。</summary>
    public static ScrollViewer CreateRelatedPosterRow(
        IReadOnlyList<(RelatedEntry Entry, LocalComic? Local, string? Cover)> items)
    {
        var scroll = CreateScroll();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (entry, local, cover) in items)
        {
            var card = new MangaPosterCard();
            card.BindRelated(entry, local, cover);
            panel.Children.Add(card);
        }
        scroll.Content = panel;
        return scroll;
    }

    private static ScrollViewer CreateScroll() => new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
}
