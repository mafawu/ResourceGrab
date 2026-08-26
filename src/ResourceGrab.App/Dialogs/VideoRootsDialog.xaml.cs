using System.IO;
using System.Windows;
using System.Windows.Controls;
using ResourceGrab.Core.Services;

namespace ResourceGrab.App.Dialogs;

public partial class VideoRootsDialog : Window
{
    private readonly VideoLibraryService _library;

    public VideoRootsDialog(VideoLibraryService library)
    {
        InitializeComponent();
        _library = library;
        RenderRoots();
    }

    private void RenderRoots()
    {
        RootList.Items.Clear();
        foreach (var path in _library.RootFolders)
        {
            RootList.Items.Add(new ListBoxItem
            {
                Content = Directory.Exists(path) ? path : $"{path}（不存在）",
                Tag = path,
                Padding = new Thickness(10, 8, 10, 8),
            });
        }
        RemoveButton.IsEnabled = RootList.SelectedIndex >= 0;
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (RootList.SelectedItem is not ListBoxItem item || item.Tag is not string path) return;
        if (MessageBox.Show(this, $"从根目录列表移除 {path} 吗？\n已有视频记录不会被删除。", "移除确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_library.RemoveRootFolder(path))
        {
            RenderRoots();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
