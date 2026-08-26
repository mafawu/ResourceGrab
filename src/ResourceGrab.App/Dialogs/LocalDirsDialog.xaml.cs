using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceGrab.App.Common;
using ResourceGrab.App.Services;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.Core.Services;

namespace ResourceGrab.App.Dialogs;

/// <summary>本地路径管理弹窗：添加 / 移除本地模式扫描目录，确定后写入配置。</summary>
public partial class LocalDirsDialog : Window
{
    private sealed record DirItem(string Path);

    private readonly ObservableCollection<DirItem> _dirs = new();

    public LocalDirsDialog()
    {
        InitializeComponent();
        foreach (var dir in App.Services.GetRequiredService<ConfigService>().Current.LocalDirs)
        {
            if (!string.IsNullOrWhiteSpace(dir))
            {
                _dirs.Add(new DirItem(dir));
            }
        }
        DirList.ItemsSource = _dirs;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DwmWindowCorner.Apply(this);
    }

    private void AddDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择本地漫画目录",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        var path = Path.GetFullPath(dialog.FolderName);
        if (_dirs.Any(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError("该路径已在列表中");
            return;
        }
        _dirs.Add(new DirItem(path));
        HideError();
    }

    private void RemoveDir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DirItem item })
        {
            _dirs.Remove(item);
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var config = App.Services.GetRequiredService<ConfigService>().Current;
        config.LocalDirs = _dirs.Select(d => d.Path).ToList();
        App.Services.GetRequiredService<ConfigService>().Save();
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
    }
}
