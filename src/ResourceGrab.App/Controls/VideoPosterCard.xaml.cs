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

    /// <summary>hover"预览"动作：打开在线详情并自动起播。</summary>
    public event Action<OnlineVideoSummary>? OnlinePreviewRequested;

    /// <summary>双击海报（在线搜索结果）：直接进入完整详情页。</summary>
    public event Action<OnlineVideoSummary>? OnlineFullDetailRequested;

    /// <summary>第二次按下（ClickCount>=2）已标记双击：抬起时不再触发单击详情。</summary>
    private bool _doubleClickArmed;

    /// <summary>单击/双击判定窗口：单击延迟到窗口结束再开侧栏；窗口内第二次点击则进完整页。
    /// 不能单击立即开侧栏——侧栏遮罩会吞掉双击的第二次点击，导致双击永远收不到。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _singleClickTimer =
        new() { Interval = TimeSpan.FromMilliseconds(280) };
    private OnlineVideoSummary? _pendingSingleItem;

    public VideoPosterCard()
    {
        HoverText = "查看详情";
        // hover 遮罩是 Button，会把 MouseLeftButtonDown 标记为已处理：
        // 不加 handledEventsToo 的 AddHandler 永远收不到第二次按下，ClickCount>=2 永不成立，
        // 双击就会退化成两次单击（只开侧栏）。必须显式接收已处理事件。
        AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnCardMouseDown), true);
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            var item = _pendingSingleItem;
            _pendingSingleItem = null;
            if (item is not null) OnlineDetailRequested?.Invoke(item);
        };
        // 鼠标移开或卸载后取消挂起的单击，避免离开卡片后才弹出侧栏
        MouseLeave += (_, _) => CancelPendingSingle();
        Unloaded += (_, _) => CancelPendingSingle();
        CardClick += (_, _) =>
        {
            if (_item is not null) { DetailRequested?.Invoke(_item); return; }
            if (_onlineItem is null) return;
            if (_doubleClickArmed)
            {
                // 双击第二击：取消单击、直接进完整详情页
                _doubleClickArmed = false;
                CancelPendingSingle();
                OnlineFullDetailRequested?.Invoke(_onlineItem);
            }
            else
            {
                // 单击：进入双击判定窗口，窗口结束未再点击才开侧栏
                _pendingSingleItem = _onlineItem;
                _singleClickTimer.Stop();
                _singleClickTimer.Start();
            }
        };
        // 在线卡片宽度可调（每行个数滑杆）：横版封面高度始终跟随卡片宽度保持 1.5:1
        SizeChanged += (_, _) =>
        {
            if (_onlineItem is not null && _item is null && ActualWidth > 0)
                CoverHeight = ActualWidth / 1.5;
        };
    }

    /// <summary>第二次按下标记双击；hover 动作按钮（▶ 播放 / 详情）上的双击不接管。</summary>
    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount <= 1 || _onlineItem is null) return;
        // 命中 hover 动作按钮（位于 HoverOverlay 内容里）时不接管
        if (e.OriginalSource is DependencyObject source
            && HoverOverlay.Content is DependencyObject content
            && IsDescendantOf(source, content))
        {
            return;
        }
        _doubleClickArmed = true;
        e.Handled = true;
    }

    private void CancelPendingSingle()
    {
        _singleClickTimer.Stop();
        _pendingSingleItem = null;
    }

    private static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor)
    {
        for (var n = node; n is not null; n = VisualTreeHelper.GetParent(n) ?? LogicalTreeHelper.GetParent(n))
        {
            if (ReferenceEquals(n, ancestor)) return true;
        }
        return false;
    }

    /// <summary>设置统一已下载角标（绿底白字 ✓，与漫画卡一致）；false 清除。</summary>
    public void SetDownloaded(bool downloaded)
    {
        if (!downloaded)
        {
            // 仅清除本方法挂的角标，不碰评分/类型等其他槽位
            if (BottomLeftContent is Border b && b.Tag as string == "downloaded")
                BottomLeftContent = null;
            return;
        }
        var badge = MakeBadge("✓已下载",
            new SolidColorBrush(Colors.White),
            new SolidColorBrush(Color.FromRgb(0x1F, 0xA8, 0x55)));
        badge.Tag = "downloaded";
        BottomLeftContent = badge;
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
    /// 在线卡片用大横版封面 + 大标题 + hover 动作（▶ 预览 / 详情），方便快速浏览与试看。
    /// </summary>
    public void Bind(OnlineVideoSummary item)
    {
        _item = null;
        _onlineItem = item;
        ToolTip = "单击查看侧栏详情，双击进入完整详情";

        // 在线封面为横版（宽:高 = 1.5:1），按卡片宽 170 换算高度，完整显示不裁切
        CoverHeight = 170 / 1.5;

        CoverSource = string.IsNullOrEmpty(item.CoverUrl) ? null : item.CoverUrl;
        CoverFallback = item.Title;

        Title = item.Title;
        TitleFontSize = 14;

        // 信息行：番号 + 内容类型；时长压在封面右下角标，不重复放
        MetaLeft = string.IsNullOrEmpty(item.Number)
            ? VideoNumberParser.Parse(item.Title).Number
            : item.Number;
        MetaRight = "";

        SetTags(item.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Take(4).ToList());

        // hover 动作：▶ 播放（开详情并自动起播，主色按钮）/ 详情
        SetHoverActions(new (string, Action, bool)[]
        {
            ("▶ 播放", () => OnlinePreviewRequested?.Invoke(item), true),
            ("详情", () => OnlineDetailRequested?.Invoke(item), false),
        });

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

        // 右下：文件丢失（只挂角标，不再整体降透明——降透明在大片白底上像蒙了层白纱）
        Opacity = 1;
        if (!item.FileExists)
        {
            BottomRightContent = MakeBadge("缺失",
                new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x6F)),
                new SolidColorBrush(Color.FromArgb(0xB3, 0x80, 0x00, 0x00)));
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
