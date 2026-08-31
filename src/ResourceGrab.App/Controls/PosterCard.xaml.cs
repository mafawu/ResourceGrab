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

    public PosterCard()
    {
        InitializeComponent();
        Loaded += (_, _) => { UpdateCover(); UpdateSubtitle(); UpdateProgress(); };
    }

    /// <summary>设置标签列表。</summary>
    public void SetTags(IEnumerable<string> tags)
    {
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

    private void Root_Click(object sender, MouseButtonEventArgs e)
    {
        CardClick?.Invoke(this, new RoutedEventArgs());
    }
}
