using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using YingkuHome.Models;

namespace YingkuHome.Controls;

public partial class MediaCard : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(DemoItem), typeof(MediaCard),
        new PropertyMetadata(null, static (d, e) => ((MediaCard)d).Apply(e.NewValue as DemoItem)));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(MediaCard),
        new PropertyMetadata(false, static (d, _) => ((MediaCard)d).UpdateSelection()));

    public event EventHandler? Activated;

    public MediaCard()
    {
        InitializeComponent();
    }

    public DemoItem? Item
    {
        get => (DemoItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    private void Apply(DemoItem? item)
    {
        if (item is null) return;
        PosterBrush.ImageSource = item.Poster;
        CodeText.Text = item.Code;
        TitleText.Text = item.Title;
        SubText.Text = LetterSpaced(item.Subtitle);
    }

    /// <summary>WPF has no letter-spacing, so thin spaces stand in for it.</summary>
    private static string LetterSpaced(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length * 2);
        foreach (var ch in text)
        {
            if (ch == ' ')
            {
                sb.Append('\u2002').Append('\u2002');
                continue;
            }
            sb.Append(ch).Append('\u2009');
        }
        return sb.ToString().TrimEnd('\u2009');
    }

    private void UpdateSelection()
    {
        SelectedRing.Opacity = IsSelected ? 1 : 0;
        if (IsSelected) HoverRing.Opacity = 0;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        Animate(1.045, showHover: !IsSelected);
        Panel.SetZIndex(this, 10);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        Animate(1.0, showHover: false);
        Panel.SetZIndex(this, 0);
    }

    private void Animate(double scale, bool showHover)
    {
        var duration = TimeSpan.FromMilliseconds(170);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease });
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease });
        HoverRing.BeginAnimation(OpacityProperty,
            new DoubleAnimation(showHover ? 1 : 0, duration) { EasingFunction = ease });
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}


