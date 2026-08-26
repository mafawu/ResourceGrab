using System.Windows;
using System.Windows.Controls;
using ResourceGrab.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Views;

/// <summary>右侧下载面板。</summary>
public partial class DownloadPanel : UserControl
{
    public DownloadPanel()
    {
        InitializeComponent();
        var viewModel = App.Services.GetRequiredService<DownloadPanelViewModel>();
        DataContext = viewModel;

        UpdateEmptyStates(viewModel);
        viewModel.PropertyChanged += (_, _) => UpdateEmptyStates(viewModel);
    }

    private void CurrentTab_Click(object sender, RoutedEventArgs e)
    {
        UpdateEmptyStates((DownloadPanelViewModel)DataContext);
    }

    private void HistoryTab_Click(object sender, RoutedEventArgs e)
    {
        UpdateEmptyStates((DownloadPanelViewModel)DataContext);
    }

    private void UpdateEmptyStates(DownloadPanelViewModel viewModel)
    {
        // 两个列表同处一个 Grid 单元格，必须显式互斥，否则先显示的一方会一直盖住另一方。
        var showHistory = HistoryTab.IsChecked == true;

        var hasHistory = viewModel.HasHistoryDownloads;
        HistoryList.Visibility = showHistory && hasHistory ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmptyHint.Visibility = showHistory && !hasHistory ? Visibility.Visible : Visibility.Collapsed;

        var hasCurrent = viewModel.HasCurrentDownloads;
        CurrentList.Visibility = !showHistory && hasCurrent ? Visibility.Visible : Visibility.Collapsed;
        CurrentEmptyHint.Visibility = !showHistory && !hasCurrent ? Visibility.Visible : Visibility.Collapsed;
    }
}


