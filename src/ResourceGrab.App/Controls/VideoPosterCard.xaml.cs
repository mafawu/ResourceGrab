using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ResourceGrab.App.Common;
using ResourceGrab.Core.Models;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 视频海报卡片：基于 PosterCard 壳子，填充视频特有的角标和信息。
/// 角标：评分、中字、收藏、用户评分、文件状态。
/// </summary>
public class VideoPosterCard : PosterCard
{
    private VideoItem? _item;

    /// <summary>点击查看详情。</summary>
    public event Action<VideoItem>? DetailRequested;

    public VideoPosterCard()
    {
        HoverText = "查看详情";
        CardClick += (_, _) =>
        {
            if (_item is not null) DetailRequested?.Invoke(_item);
        };
    }

    /// <summary>绑定 VideoItem，自动填充所有属性和角标。</summary>
    public void Bind(VideoItem item)
    {
        _item = item;

        // 封面
        var posterPath = new[] { item.PosterPath, item.CoverPath, item.ThumbnailPath }
            .FirstOrDefault(p => !string.IsNullOrEmpty(p) && System.IO.File.Exists(p));
        CoverSource = posterPath;
        CoverFallback = item.Number;

        // 标题
        Title = item.DisplayTitle;
        Subtitle = !string.IsNullOrEmpty(item.OriginalTitle) && item.OriginalTitle != item.Title
            ? item.OriginalTitle : null;

        // 元数据行
        MetaLeft = item.Number;
        if (item.Score > 0) MetaRight = $"★ {item.Score:F1}";
        else if (item.DurationSeconds > 0) MetaRight = item.DurationText;
        else MetaRight = "";

        // 标签
        var tags = item.Actors.Take(2).ToList();
        if (item.HasChineseSubtitle) tags.Insert(0, "中字");
        SetTags(tags);

        // 角标
        BuildBadges(item);

        // 进度
        if (item.WatchProgress > 0)
        {
            Progress = item.WatchProgress;
        }
    }

    private void BuildBadges(VideoItem item)
    {
        // 左上：评分
        if (item.Score > 0)
        {
            TopLeftContent = MakeBadge($"★ {item.Score:F1}",
                new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47)));
        }

        // 右上：中字 或 收藏
        if (item.HasChineseSubtitle)
        {
            TopRightContent = MakeBadge("中字",
                new SolidColorBrush(Color.FromRgb(0x6F, 0xCF, 0x97)));
        }
        else if (item.IsFavorite)
        {
            TopRightContent = MakeBadge("♥",
                new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x6F)));
        }

        // 左下：用户评分
        if (item.UserRating > 0)
        {
            var stars = new string('★', item.UserRating) + new string('☆', 5 - item.UserRating);
            BottomLeftContent = MakeBadge(stars,
                new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47)));
        }

        // 右下：文件丢失
        if (!item.FileExists)
        {
            BottomRightContent = MakeBadge("缺失",
                new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x6F)),
                new SolidColorBrush(Color.FromArgb(0xB3, 0x80, 0x00, 0x00)));
            // 整体降低透明度
            Opacity = 0.6;
        }
    }
}

/// <summary>
/// 海报网格：横向滚动的海报卡片容器。
/// </summary>
public static class PosterGridHelper
{
    /// <summary>从 VideoItem 列表创建海报卡片网格。</summary>
    public static ScrollViewer CreateVideoPosterRow(
        IReadOnlyList<VideoItem> items,
        Action<VideoItem>? onDetailClicked = null)
    {
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        foreach (var item in items)
        {
            var card = new VideoPosterCard();
            card.Bind(item);
            if (onDetailClicked is not null)
                card.DetailRequested += onDetailClicked;
            panel.Children.Add(card);
        }

        scroll.Content = panel;
        return scroll;
    }
}
