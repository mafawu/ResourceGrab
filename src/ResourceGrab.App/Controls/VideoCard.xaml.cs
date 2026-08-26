using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceGrab.App.Common;
using ResourceGrab.Core.Models;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 视频文件夹卡片：封面 + 名称 + 标签 + 收藏心形 + 丢失提示 + 右键菜单。
/// 左键点击 → 浏览文件夹内视频；右键 → 管理（编辑/重扫/收藏/移除等）。
/// </summary>
[System.Obsolete("Use MediaCard + MediaCardAdapters instead")]
public partial class VideoCard : UserControl
{
    public static readonly DependencyProperty FolderProperty =
        DependencyProperty.Register(nameof(Folder), typeof(VideoFolder), typeof(VideoCard),
            new PropertyMetadata(null, OnFolderChanged));

    public VideoFolder? Folder
    {
        get => (VideoFolder?)GetValue(FolderProperty);
        set => SetValue(FolderProperty, value);
    }

    public event EventHandler<VideoFolder>? OpenRequested;

    public event EventHandler<VideoFolder>? EditRequested;

    public event EventHandler<VideoFolder>? RescanRequested;

    /// <summary>收藏状态切换后触发，由视图负责持久化。</summary>
    public event EventHandler<VideoFolder>? FavoriteChanged;

    public event EventHandler<VideoFolder>? DeleteRequested;

    public VideoCard()
    {
        InitializeComponent();
        MouseEnter += (_, _) => BrowseOverlay.Visibility = Visibility.Visible;
        MouseLeave += (_, _) => BrowseOverlay.Visibility = Visibility.Collapsed;
    }

    private static void OnFolderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not VideoCard card || e.NewValue is not VideoFolder folder) return;
        card.Render(folder);
    }

    /// <summary>元数据后台补齐后可调用，原地刷新卡片内容（不重建控件）。</summary>
    public void Refresh() => Render(Folder);

    private void Render(VideoFolder? folder)
    {
        if (folder is null) return;

        TitleText.Text = string.IsNullOrEmpty(folder.Name) ? "(未命名)" : folder.Name;
        FileCountText.Text = $"{folder.Files.Count} 个";
        SizeText.Text = folder.TotalSizeText;

        DurationText.Text = folder.TotalDurationText.Length > 0 ? "⏱ " + folder.TotalDurationText : "";
        DurationText.Visibility = DurationText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrEmpty(folder.Series))
        {
            SeriesText.Text = $"系列: {folder.Series}";
            SeriesText.Visibility = Visibility.Visible;
        }
        else
        {
            SeriesText.Visibility = Visibility.Collapsed;
        }

        if (folder.Rating > 0)
        {
            RatingText.Text = new string('★', folder.Rating);
            RatingText.Visibility = Visibility.Visible;
        }
        else
        {
            RatingText.Visibility = Visibility.Collapsed;
        }

        // 丢失状态：目录不存在 / 有文件缺失
        var missingFiles = folder.MissingFileCount;
        if (!folder.FileExists)
        {
            MissingText.Text = "目录丢失";
            MissingBadge.Visibility = Visibility.Visible;
        }
        else if (missingFiles > 0)
        {
            MissingText.Text = $"缺失 {missingFiles} 个";
            MissingBadge.Visibility = Visibility.Visible;
        }
        else
        {
            MissingBadge.Visibility = Visibility.Collapsed;
        }

        // 收藏心形
        FavoriteIcon.Text = folder.IsFavorite ? "♥" : "♡";
        FavoriteIcon.Foreground = folder.IsFavorite
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x4D, 0x6F))
            : System.Windows.Media.Brushes.White;
        FavoriteMenuItem.Header = folder.IsFavorite ? "取消收藏" : "收藏";

        TagItems.ItemsSource = folder.Tags.Take(3).ToList();

        // 封面：ImageLoader 异步加载本地文件（带缓存），占位图标由 DataTrigger 自动显隐
        var cover = folder.CoverPath;
        ImageLoader.SetSource(CoverImage,
            !string.IsNullOrEmpty(cover) && System.IO.File.Exists(cover) ? cover : null);

        // 进度条
        var progress = Math.Clamp(folder.WatchProgress, 0, 100);
        ProgressBar.Width = ActualWidth * progress / 100.0;
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (Folder is not null) OpenRequested?.Invoke(this, Folder);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (Folder is not null) OpenRequested?.Invoke(this, Folder);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Folder is not null) EditRequested?.Invoke(this, Folder);
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (Folder is not null) RescanRequested?.Invoke(this, Folder);
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (Folder is null) return;
        if (e is MouseButtonEventArgs m) m.Handled = true; // 防止冒泡触发 Card_Click
        Folder.IsFavorite = !Folder.IsFavorite;
        Render(Folder);
        FavoriteChanged?.Invoke(this, Folder);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Folder is null) return;
        try
        {
            if (!string.IsNullOrEmpty(Folder.FolderPath) && System.IO.Directory.Exists(Folder.FolderPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", Folder.FolderPath);
            }
        }
        catch { }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Folder is not null) DeleteRequested?.Invoke(this, Folder);
    }
}

