using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ResourceGrab.App.Common;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 视频海报卡片：基于 PosterCard 壳子，填充视频特有的角标和信息。
/// 角标：评分、中字、收藏、用户评分、文件状态。
/// </summary>
public class VideoPosterCard : PosterCard
{
    private VideoItem? _item;
    private OnlineVideoSummary? _onlineItem;

    /// <summary>点击查看详情（本地条目）。</summary>
    public event Action<VideoItem>? DetailRequested;

    /// <summary>点击查看详情（在线搜索结果）。</summary>
    public event Action<OnlineVideoSummary>? OnlineDetailRequested;

    public VideoPosterCard()
    {
        HoverText = "查看详情";
        CardClick += (_, _) =>
        {
            if (_item is not null) DetailRequested?.Invoke(_item);
            else if (_onlineItem is not null) OnlineDetailRequested?.Invoke(_onlineItem);
        };
        // 在线卡片宽度可调（每行个数滑杆）：横版封面高度始终跟随卡片宽度保持 1.5:1
        SizeChanged += (_, _) =>
        {
            if (_onlineItem is not null && _item is null && ActualWidth > 0)
                CoverHeight = ActualWidth / 1.5;
        };
    }

    /// <summary>绑定 VideoItem，自动填充所有属性和角标。</summary>
    public void Bind(VideoItem item)
    {
        _item = item;
        _onlineItem = null;
        // 封面（本地为竖版海报，重置横版在线卡片改过的高度）
        CoverHeight = PosterCard.DefaultCoverHeight;
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

    /// <summary>
    /// 绑定在线搜索摘要：复用本地海报样式，封面直接走网络 URL（ImageLoader 支持 http）。
    /// 点击触发 OnlineDetailRequested（与本地条目的 DetailRequested 区分）。
    /// </summary>
    public void Bind(OnlineVideoSummary item)
    {
        _item = null;
        _onlineItem = item;

        // 在线封面为横版（宽:高 = 1.5:1），按卡片宽 170 换算高度，完整显示不裁切
        CoverHeight = 170 / 1.5;

        CoverSource = string.IsNullOrEmpty(item.CoverUrl) ? null : item.CoverUrl;
        CoverFallback = item.Title;

        Title = item.Title;

        // 信息行：番号 + 内容类型；时长压在封面右下角标，不重复放
        MetaLeft = string.IsNullOrEmpty(item.Number)
            ? VideoNumberParser.Parse(item.Title).Number
            : item.Number;
        MetaRight = "";

        SetTags(item.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Take(4).ToList());

        // 清掉本地卡片的角标/进度/缺失态，类型压左上角标、评分压右上角标、时长压右下角标
        TopLeftContent = string.IsNullOrEmpty(item.KindLabel)
            ? null
            : MakeBadge(item.KindLabel,
                new SolidColorBrush(Color.FromRgb(0x9A, 0xC1, 0xFF)),
                new SolidColorBrush(Color.FromArgb(0xB3, 0x1D, 0x4E, 0xD8)));
        TopRightContent = string.IsNullOrEmpty(item.RatingText)
            ? null
            : MakeRatingBadge(item.RatingText);
        BottomLeftContent = null;
        BottomRightContent = string.IsNullOrEmpty(item.DurationText)
            ? null
            : MakeBadge(item.DurationText);
        Progress = 0;
        Opacity = 1;
    }

    /// <summary>
    /// 详情页回填：后台渐进拉到的详情补充到搜索卡片上（演员作副标题、发行日期进信息行、
    /// 评分压右上角标、标签换为详情页完整标签）。摘要里已有的信息（时长、类型角标）保持不变。
    /// </summary>
    public void UpdateOnlineDetail(OnlineVideoDetail detail)
    {
        if (_onlineItem is null || _item is not null) return;

        var actors = detail.Actors.Where(a => !string.IsNullOrWhiteSpace(a)).Take(3).ToList();
        if (actors.Count > 0)
            Subtitle = string.Join(" / ", actors);

        if (!string.IsNullOrEmpty(detail.ReleaseDateText))
            MetaRight = $"发行 {detail.ReleaseDateText}";

        if (!string.IsNullOrEmpty(detail.RatingText))
            TopRightContent = MakeRatingBadge(detail.RatingText);

        var detailTags = detail.Tags.Concat(detail.Genres)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Where(t => !detail.Actors.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Take(4).ToList();
        if (detailTags.Count > 0)
            SetTags(detailTags);

        // 番号以详情页为准（搜索摘要从 URL 提取，偶有出入）
        if (!string.IsNullOrEmpty(detail.Number))
            MetaLeft = detail.Number;
    }

    /// <summary>评分角标：琥珀色 ★ 徽章，评分文本已含 ★ 时不重复加。</summary>
    private static Border MakeRatingBadge(string ratingText)
    {
        var text = ratingText.Trim();
        if (!text.Contains('★')) text = $"★ {text}";
        return MakeBadge(text, new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47)));
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
