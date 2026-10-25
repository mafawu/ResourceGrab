using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Services;
using ResourceGrab.App.ViewModels;

namespace ResourceGrab.App.Views;

/// <summary>
/// 下载页：跨媒体统一展示漫画章节与视频文件的传输队列，以及已完成历史。
/// 数据来自 <see cref="DownloadPageViewModel"/>（漫画事件驱动 + 视频队列轮询）。
/// </summary>
public partial class DownloadPage : UserControl
{
    private readonly DownloadPageViewModel _viewModel;
    private readonly ICollectionView _currentView;
    private readonly ICollectionView _historyView;
    private string _kindFilter = "all";
    private bool _showHistory;

    public DownloadPage()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<DownloadPageViewModel>();
        DataContext = _viewModel;

        _currentView = CollectionViewSource.GetDefaultView(_viewModel.CurrentItems);
        _currentView.Filter = FilterPredicate;
        _historyView = CollectionViewSource.GetDefaultView(_viewModel.HistoryItems);
        _historyView.Filter = FilterPredicate;

        CurrentList.ItemsSource = _currentView;
        HistoryList.ItemsSource = _historyView;

        _viewModel.CurrentItems.CollectionChanged += OnCollectionChanged;
        _viewModel.HistoryItems.CollectionChanged += OnCollectionChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;

        Loaded += (_, _) => UpdateEmptyStates();
        Unloaded += (_, _) =>
        {
            _viewModel.CurrentItems.CollectionChanged -= OnCollectionChanged;
            _viewModel.HistoryItems.CollectionChanged -= OnCollectionChanged;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        };

        UpdateEmptyStates();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyStates();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadPageViewModel.SpeedText))
        {
            // 速度每秒刷新，不需要动列表
            return;
        }
        UpdateEmptyStates();
    }

    private bool FilterPredicate(object obj)
    {
        if (obj is not TransferItem item) return false;
        return _kindFilter switch
        {
            "manga" => item.Kind == TransferKind.Manga,
            "video" => item.Kind == TransferKind.Video,
            _ => true,
        };
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        _showHistory = HistoryTab.IsChecked == true;
        CurrentScroll.Visibility = _showHistory ? Visibility.Collapsed : Visibility.Visible;
        HistoryScroll.Visibility = _showHistory ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.Visibility = _showHistory && _viewModel.HasHistory
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateEmptyStates();
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        _kindFilter = FilterManga.IsChecked == true ? "manga"
            : FilterVideo.IsChecked == true ? "video"
            : "all";
        _currentView.Refresh();
        _historyView.Refresh();
        UpdateEmptyStates();
    }

    private void UpdateEmptyStates()
    {
        if (_currentView is null) return;
        var currentEmpty = _currentView.IsEmpty;
        var historyEmpty = _historyView.IsEmpty;

        CurrentEmpty.Visibility = !_showHistory && currentEmpty ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Visibility = _showHistory && historyEmpty ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.Visibility = _showHistory && _viewModel.HasHistory
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item })
        {
            _viewModel.Cancel(item);
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item })
        {
            _viewModel.Retry(item);
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item } && !string.IsNullOrEmpty(item.FilePath))
        {
            OpenInExplorer(item.FilePath, selectFile: true);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item })
        {
            OpenInExplorer(item.FilePath, selectFile: false, fallbackFolder: item.FolderPath);
        }
    }

    private void OpenDownloadDir_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = OpenDownloadDirButton };
        var manga = new MenuItem { Header = "漫画下载目录" };
        manga.Click += (_, _) => OpenInExplorer(null, false, _viewModel.MangaDownloadDir);
        var video = new MenuItem { Header = "视频下载目录" };
        video.Click += (_, _) => OpenInExplorer(null, false, _viewModel.VideoDownloadDir);
        menu.Items.Add(manga);
        menu.Items.Add(video);
        menu.IsOpen = true;
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearHistory();
        UpdateEmptyStates();
        ToastService.Show("已清除下载记录", ToastKind.Info);
    }

    /// <summary>打开文件所在位置（selectFile）或直接打开目录；路径不存在时给提示而不是抛异常。</summary>
    private static void OpenInExplorer(string? filePath, bool selectFile, string? fallbackFolder = null)
    {
        try
        {
            if (selectFile && !string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                return;
            }

            var folder = fallbackFolder;
            if (string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(filePath))
            {
                folder = File.Exists(filePath) ? Path.GetDirectoryName(filePath) : filePath;
            }
            if (string.IsNullOrEmpty(folder)) return;

            if (Directory.Exists(folder))
            {
                Process.Start("explorer.exe", $"\"{folder}\"");
            }
            else
            {
                ToastService.Show("目录不存在：" + folder, ToastKind.Info);
            }
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex, "打开失败：");
        }
    }
}
