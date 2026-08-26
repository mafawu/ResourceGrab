using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ResourceGrab.App.Services;
using ResourceGrab.App.Themes;

namespace ResourceGrab.App.Controls;

/// <summary>右下角轻量通知条宿主。</summary>
public partial class SnackbarHost : UserControl
{
    public SnackbarHost()
    {
        InitializeComponent();
    }

    public void Show(string message, ToastKind kind = ToastKind.Info)
    {
        var accent = kind switch
        {
            ToastKind.Success => (Brush)FindResource("SuccessBrush"),
            ToastKind.Error => (Brush)FindResource("DangerBrush"),
            _ => Brushes.White,
        };
        var glyph = kind switch
        {
            ToastKind.Success => Icons.Check,
            ToastKind.Error => Icons.Close,
            _ => Icons.Info,
        };

        var border = new Border
        {
            Background = (Brush)FindResource("SnackbarBgBrush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
            MaxWidth = 420,
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new Path
        {
            Data = glyph,
            Stroke = accent,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Width = 15,
            Height = 15,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var text = new TextBlock
        {
            Text = message,
            FontSize = 12.5,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(icon);
        panel.Children.Add(text);

        // 左侧 3px 状态色条
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var accentBar = new Border
        {
            Width = 3,
            Background = accent,
            CornerRadius = new CornerRadius(10, 0, 0, 10),
            Margin = new Thickness(-14, -10, 10, -10),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetColumn(accentBar, 0);
        Grid.SetColumn(panel, 1);
        row.Children.Add(accentBar);
        row.Children.Add(panel);
        border.Child = row;

        Host.Children.Add(border);

        var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240));
            fade.Completed += (_, _) => Host.Children.Remove(border);
            border.BeginAnimation(OpacityProperty, fade);
        };
        closeTimer.Start();
    }
}
