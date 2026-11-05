using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ResourceGrab.App.Common;
using ResourceGrab.App.Services;
using ResourceGrab.App.ViewModels;
using ResourceGrab.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Views;

/// <summary>
/// 我的书架：只放在线收藏，与本地索引互不写入。
/// 空白态直接给「去在线书库」，不做成一个死胡同。
/// </summary>
public partial class NovelShelfView : UserControl
{
    private readonly NovelShelfService _shelf;
    private readonly NovelSourceManager _sources;

    public ObservableCollection<MediaCardViewModel> Cards { get; } = new();

    public NovelShelfView()
    {
        InitializeComponent();
        _shelf = App.Services.GetRequiredService<NovelShelfService>();
        _sources = App.Services.GetRequiredService<NovelSourceManager>();
        ShelfGrid.ItemsSource = Cards;
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        Cards.Clear();
        var source = _sources.Current;
        var items = _shelf.Items;

        if (source is null || items.Count == 0)
        {
            CountText.Text = items.Count == 0 ? "" : $"{items.Count} 本";
            ShelfGrid.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
            return;
        }

        foreach (var item in items)
        {
            var resolved = _sources.Get(item.SourceId) ?? source;
            Cards.Add(new NovelShelfCardAdapter(item, resolved,
                new RelayCommand(_ => Remove(item.SourceId, item.NovelId))));
        }

        CountText.Text = $"{items.Count} 本";
        ShelfGrid.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;
    }

    private void RemoveShelf_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: NovelShelfCardAdapter card }) return;
        Remove(card.Item.SourceId, card.Item.NovelId);
    }

    private void Remove(string sourceId, string novelId)
    {
        if (!_shelf.Remove(sourceId, novelId)) return;
        ToastService.Show("已从书架移除", ToastKind.Info);
        Refresh();
    }

    private void GoOnline_Click(object sender, RoutedEventArgs e) => Navigation.GoNovelOnline();
}
