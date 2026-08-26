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
        if (HistoryTab.IsChecked == true)
        {
            HistoryList.Visibility = viewModel.HasHistoryDownloads ? Visibility.Visible : Visibility.Collapsed;
            HistoryEmptyHint.Visibility = viewModel.HasHistoryDownloads ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        CurrentList.Visibility = viewModel.HasCurrentDownloads ? Visibility.Visible : Visibility.Collapsed;
        CurrentEmptyHint.Visibility = viewModel.HasCurrentDownloads ? Visibility.Collapsed : Visibility.Visible;
    }
}


