using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YingkuHome.Controls;
using YingkuHome.Models;

namespace YingkuHome;

public partial class MainWindow : Window
{
    private readonly List<MediaCard> _cards = [];
    private DemoItem? _selected;

    public MainWindow()
    {
        InitializeComponent();

        var vm = DemoData.Build();
        DataContext = vm;

        var start = 0;
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && int.TryParse(args[1], out var n)) start = Math.Clamp(n, 0, vm.RecentItems.Count - 1);
        Select(vm.RecentItems[start]);
    }

    private void Select(DemoItem item)
    {
        _selected = item;
        Hero.Item = item;
        foreach (var card in _cards)
        {
            card.IsSelected = ReferenceEquals(card.Item, item);
        }
    }

    private void Card_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaCard card || _cards.Contains(card)) return;
        _cards.Add(card);
        card.IsSelected = ReferenceEquals(card.Item, _selected);
    }

    private void Card_Activated(object? sender, EventArgs e)
    {
        if (sender is MediaCard { Item: { } item }) Select(item);
    }

    private void Row_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;
        scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // the button was released before the drag started
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
