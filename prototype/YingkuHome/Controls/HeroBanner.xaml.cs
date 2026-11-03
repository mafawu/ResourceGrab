using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using YingkuHome.Models;

namespace YingkuHome.Controls;

public partial class HeroBanner : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(DemoItem), typeof(HeroBanner),
        new PropertyMetadata(null, static (d, e) => ((HeroBanner)d).Apply(e.NewValue as DemoItem)));

    private bool _frontIsB;

    public HeroBanner()
    {
        InitializeComponent();
    }

    public DemoItem? Item
    {
        get => (DemoItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private void Apply(DemoItem? item)
    {
        if (item is null) return;

        TitleText.Text = item.HeroTitle;
        MetaText.Text = $"{item.Code}    {item.Year}    {item.Duration}";
        ActorsText.Text = item.Actors;
        DescText.Text = item.Description;

        StillImage.Source = item.Still;
        CrossfadeBackdrop(item.Backdrop);
    }

    /// <summary>Two stacked images so switching titles dissolves instead of blinking.</summary>
    private void CrossfadeBackdrop(System.Windows.Media.ImageSource? source)
    {
        var duration = TimeSpan.FromMilliseconds(320);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var (incoming, outgoing) = _frontIsB ? (BackA, BackB) : (BackB, BackA);

        incoming.Source = source;
        incoming.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        outgoing.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration) { EasingFunction = ease });
        _frontIsB = !_frontIsB;
    }
}
