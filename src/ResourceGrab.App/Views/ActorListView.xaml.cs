using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.Core.Services;

namespace ResourceGrab.App.Views;

/// <summary>演员列表页：从本地库统计全部演员及作品数，点击进入演员详情。</summary>
public partial class ActorListView : UserControl
{
    private readonly VideoLibraryService _library;
    public Action<string>? ActorSelected;

    public ActorListView()
    {
        InitializeComponent();
        _library = App.Services.GetRequiredService<VideoLibraryService>();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var counts = _library.GetActorCounts();
        var ordered = counts.OrderByDescending(kv => kv.Value).ToList();
        ActorWrap.Children.Clear();
        foreach (var (actor, count) in ordered)
        {
            ActorWrap.Children.Add(MakeChip(actor, count));
        }
        EmptyState.Visibility = ordered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border MakeChip(string actor, int count)
    {
        var border = new Border
        {
            Style = (Style)FindResource("VideoChipStyle"),
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 6, 6),
        };
        border.Child = new TextBlock
        {
            Text = $"{actor} ({count})",
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        border.MouseLeftButtonUp += (_, _) => ActorSelected?.Invoke(actor);
        return border;
    }
}
