using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.IO;
using ResourceGrab.App.Common;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 通用海报卡片壳子：封面图(0.7比例) + 四角角标槽 + 信息区 + hover遮罩 + 进度条。
/// 视频和漫画共用此壳子，通过属性填充不同内容。
/// </summary>
public partial class PosterCard : UserControl
{
    // ===== 封面 =====
    public static readonly DependencyProperty CoverSourceProperty =
        DependencyProperty.Register(nameof(CoverSource), typeof(string), typeof(PosterCard),
            new PropertyMetadata(null, static (d, _) => { if (d is PosterCard c) c.UpdateCover(); }));

    public static readonly DependencyProperty CoverFallbackProperty =
        DependencyProperty.Register(nameof(CoverFallback), typeof(string), typeof(PosterCard),
            new PropertyMetadata(""));

    public string? CoverSource
    {
        get => (string?)GetValue(CoverSourceProperty);
        set => SetValue(CoverSourceProperty, value);
    }

    /// <summary>默认封面区高度：170 宽对应的 0.7 竖版海报。</summary>
    public const double DefaultCoverHeight = 243;

    /// <summary>
    /// 封面区高度，默认竖版 243。横版封面（宽:高 = 1.5:1，如在线搜索结果）按卡片宽 / 1.5
    /// 设置，否则 UniformToFill 会把封面左右裁掉。
    /// </summary>
    public double CoverHeight
    {
        get => CoverHost.Height;
        set
        {
            CoverHost.Height = value;
            FallbackBorder.MinHeight = Math.Min(120, value);
        }
    }

    public string CoverFallback
    {
        get => (string)GetValue(CoverFallbackProperty);
        set => SetValue(CoverFallbackProperty, value);
    }

    // ===== 文字 =====
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PosterCard),
            new PropertyMetadata(""));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PosterCard),
            new PropertyMetadata(null, static (d, _) => { if (d is PosterCard c) c.UpdateSubtitle(); }));

    public static readonly DependencyProperty MetaLeftProperty =
        DependencyProperty.Register(nameof(MetaLeft), typeof(string), typeof(PosterCard),
            new PropertyMetadata(""));

    public static readonly DependencyProperty MetaRightProperty =
        DependencyProperty.Register(nameof(MetaRight), typeof(string), typeof(PosterCard),
            new PropertyMetadata(""));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string MetaLeft
    {
        get => (string)GetValue(MetaLeftProperty);
        set => SetValue(MetaLeftProperty, value);
    }

    public string MetaRight
    {
        get => (string)GetValue(MetaRightProperty);
        set => SetValue(MetaRightProperty, value);
    }

    // ===== 角标内容（UIElement） =====
    public static readonly DependencyProperty TopLeftContentProperty =
        DependencyProperty.Register(nameof(TopLeftContent), typeof(UIElement), typeof(PosterCard), null);

    public static readonly DependencyProperty TopRightContentProperty =
        DependencyProperty.Register(nameof(TopRightContent), typeof(UIElement), typeof(PosterCard), null);

    public static readonly DependencyProperty BottomLeftContentProperty =
        DependencyProperty.Register(nameof(BottomLeftContent), typeof(UIElement), typeof(PosterCard), null);

    public static readonly DependencyProperty BottomRightContentProperty =
        DependencyProperty.Register(nameof(BottomRightContent), typeof(UIElement), typeof(PosterCard), null);

    public UIElement? TopLeftContent
    {
        get => (UIElement?)GetValue(TopLeftContentProperty);
        set => SetValue(TopLeftContentProperty, value);
    }

    public UIElement? TopRightContent
    {
        get => (UIElement?)GetValue(TopRightContentProperty);
        set => SetValue(TopRightContentProperty, value);
    }

    public UIElement? BottomLeftContent
    {
        get => (UIElement?)GetValue(BottomLeftContentProperty);
        set => SetValue(BottomLeftContentProperty, value);
    }

    public UIElement? BottomRightContent
    {
        get => (UIElement?)GetValue(BottomRightContentProperty);
        set => SetValue(BottomRightContentProperty, value);
    }

    // ===== 进度 =====
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(PosterCard),
            new PropertyMetadata(0.0, static (d, _) => { if (d is PosterCard c) c.UpdateProgress(); }));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    // ===== 标题字号（在线视频卡片用大字号突出标题，默认 12 保持漫画卡片原样） =====
    public static readonly DependencyProperty TitleFontSizeProperty =
        DependencyProperty.Register(nameof(TitleFontSize), typeof(double), typeof(PosterCard),
            new PropertyMetadata(12.0));

    public double TitleFontSize
    {
        get => (double)GetValue(TitleFontSizeProperty);
        set => SetValue(TitleFontSizeProperty, value);
    }

    // ===== Hover 文字 =====
    public static readonly DependencyProperty HoverTextProperty =
        DependencyProperty.Register(nameof(HoverText), typeof(string), typeof(PosterCard),
            new PropertyMetadata("查看详情"));

    public string HoverText
    {
        get => (string)GetValue(HoverTextProperty);
        set => SetValue(HoverTextProperty, value);
    }

    // ===== 点击事件 =====
    public event RoutedEventHandler? CardClick;

    // ===== 标签 =====
    private TranslateTransform? _cardTransform;
    private static readonly DoubleAnimation HoverUpAnim = new(-3, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
    private static readonly DoubleAnimation HoverDownAnim = new(0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };

    private readonly List<string> _tags = [];

    /// <summary>hover 遮罩上的动作按钮容器（如在线视频卡片的"▶ 预览 / 详情"），代码构建。</summary>
    private readonly StackPanel _hoverActionsHost = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 8, 0, 0),
    };

    public PosterCard()
    {
        InitializeComponent();
        // 遮罩内容在代码里构建：ControlTemplate 内的命名元素无法从代码后台访问
        HoverOverlay.Content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "查看详情",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                _hoverActionsHost,
            },
        };
        Loaded += (_, _) => { UpdateCover(); UpdateSubtitle(); UpdateProgress(); };
    }

    /// <summary>设置标签列表。</summary>
    public void SetTags(IEnumerable<string> tags)    {
        _tags.Clear();
        _tags.AddRange(tags);
        TagHost.Children.Clear();
        foreach (var tag in _tags.Take(4))
        {
            var pill = new Border
            {
                Background = (Brush)FindResource("HoverBgBrush"),
                BorderBrush = (Brush)FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 1, 8, 1),
                Margin = new Thickness(0, 0, 4, 4),
                Child = new TextBlock
                {
                    Text = tag,
                    FontSize = 10,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                },
            };
            TagHost.Children.Add(pill);
        }
    }

    /// <summary>
    /// 设置 hover 遮罩上的动作按钮（如在线视频卡片的"▶ 播放 / 详情"）。
    /// 圆角矩形样式：Primary 用主题主色底（行动号召），其余半透明白底；
    /// 动作 click 冒泡会触发 HoverOverlay_Click，因此用 handled 标记拦下冒泡。
    /// </summary>
    public void SetHoverActions(IEnumerable<(string Text, Action Action, bool Primary)> actions)
    {
        _hoverActionsHost.Children.Clear();
        var primaryBrush = TryFindResource("PrimaryBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED));
        foreach (var (text, action, primary) in actions)
        {
            var pill = new Border
            {
                Background = primary
                    ? new SolidColorBrush(Color.FromArgb(0xE6, 0x2F, 0x6F, 0xED))
                    : new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(primary ? 0 : 1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                },
            };
            // 悬停变亮反馈；主色按钮整体提亮，次按钮半透明白更实
            pill.MouseEnter += (_, _) => pill.Background = primary
                ? new SolidColorBrush(Color.FromRgb(0x4A, 0x82, 0xF5))
                : new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            pill.MouseLeave += (_, _) => pill.Background = primary
                ? new SolidColorBrush(Color.FromArgb(0xE6, 0x2F, 0x6F, 0xED))
                : new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
            pill.MouseLeftButtonUp += (_, _) => { _actionHandled = true; action(); };
            _hoverActionsHost.Children.Add(pill);
        }
    }

    /// <summary>创建一个角标 Badge 控件。</summary>
    public static Border MakeBadge(string text, Brush? foreground = null, Brush? background = null)
    {
        return new Border
        {
            Background = background ?? new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0)),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2, 8, 2),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = foreground ?? Brushes.White,
            },
        };
    }

    private void UpdateCover()
    {
        var path = CoverSource;
        FallbackText.Text = CoverFallback;

        // 网络图片（在线搜索结果）直接交给 ImageLoader 异步下载；本地路径才做存在性检查
        var isUrl = path?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true;
        if (string.IsNullOrEmpty(path) || (!isUrl && !File.Exists(path)))
        {
            CoverImage.Visibility = Visibility.Collapsed;
            FallbackText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            ImageLoader.SetSource(CoverImage, path);
            CoverImage.Visibility = Visibility.Visible;
            FallbackText.Visibility = Visibility.Collapsed;
        }
        catch
        {
            CoverImage.Visibility = Visibility.Collapsed;
            FallbackText.Visibility = Visibility.Visible;
        }
    }

    private void UpdateSubtitle()
    {
        if (string.IsNullOrEmpty(Subtitle))
        {
            SubtitleText.Visibility = Visibility.Collapsed;
        }
        else
        {
            SubtitleText.Text = Subtitle;
            SubtitleText.Visibility = Visibility.Visible;
        }
    }

    private void UpdateProgress()
    {
        ProgressTrack.Visibility = Progress > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Root_MouseEnter(object sender, MouseEventArgs e)
    {
        HoverOverlay.Visibility = Visibility.Visible;
        _cardTransform ??= new TranslateTransform(0, 0);
        CardBorder.RenderTransform = _cardTransform;
        _cardTransform.BeginAnimation(TranslateTransform.YProperty, HoverUpAnim);
    }

    private void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        HoverOverlay.Visibility = Visibility.Collapsed;
        _cardTransform?.BeginAnimation(TranslateTransform.YProperty, HoverDownAnim);
    }

    /// <summary>动作按钮点击标记：动作 click 冒泡到遮罩时拦下，避免同时触发卡片点击。</summary>
    private bool _actionHandled;

    /// <summary>点击卡片本体（未 hover 时遮罩不可见，命中此处）。遮罩可见时由 HoverOverlay_Click 处理。</summary>
    private void Root_Click(object sender, MouseButtonEventArgs e)
    {
        if (HoverOverlay.Visibility == Visibility.Visible) return;
        CardClick?.Invoke(this, new RoutedEventArgs());
    }

    private void HoverOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_actionHandled)
        {
            _actionHandled = false;
            return;
        }
        CardClick?.Invoke(this, new RoutedEventArgs());
    }
}
