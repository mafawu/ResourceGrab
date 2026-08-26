using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ResourceGrab.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Services;
using ResourceGrab.Core.Logging;
using ResourceGrab.App.Common;
using ResourceGrab.Core.Models;

namespace ResourceGrab.App.Controls;

[System.Obsolete("Use MediaCard + MediaCardAdapters instead")]
public partial class VideoFileCard : UserControl
{
    public double CardWidth
    {
        get => Width;
        set => Width = value > 0 ? value : double.NaN;
    }

    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(VideoItem), typeof(VideoFileCard), new PropertyMetadata(null, OnItemChanged));

    public static readonly DependencyProperty SelectionModeProperty =
        DependencyProperty.Register(nameof(SelectionMode), typeof(bool), typeof(VideoFileCard),
            new PropertyMetadata(false, OnSelectionModeChanged));

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(VideoFileCard),
            new PropertyMetadata(false, OnIsSelectedChanged));

    private static ILogger? Logger => (App.Services?.GetService(typeof(ILogger)) as ILogger);

    public VideoItem? Item
    {
        get => (VideoItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public bool SelectionMode
    {
        get => (bool)GetValue(SelectionModeProperty);
        set => SetValue(SelectionModeProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public event Action<VideoItem, bool>? SelectedChanged;

    public VideoFileCard()
    {
        InitializeComponent();
        MouseEnter += Card_MouseEnter;
        MouseLeave += Card_MouseLeave;
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not VideoFileCard card || e.NewValue is not VideoItem item) return;
        card.Render(item);
    }

    private void Render(VideoItem item)
    {
        TitleText.Text = item.DisplayTitle;
        InfoText.Text = string.Join(" · ", new[] { string.IsNullOrEmpty(item.Number) ? "" : item.Number,
            item.Score > 0 ? $"★{item.Score:F1}" : "", FormatSize(item.FileSizeBytes) }.Where(s => !string.IsNullOrEmpty(s)));
        DurationText.Text = item.DurationText;
        DurationBadge.Visibility = string.IsNullOrEmpty(item.DurationText) ? Visibility.Collapsed : Visibility.Visible;
        ResolutionText.Text = item.Resolution;
        ResolutionBadge.Visibility = string.IsNullOrEmpty(item.Resolution) ? Visibility.Collapsed : Visibility.Visible;
        WatchedBar.Visibility = item.WatchProgress <= 0 ? Visibility.Visible : Visibility.Collapsed;
        FavoriteHeart.Text = item.IsFavorite ? "♥" : "♡";
        var cover = new[] { item.PosterPath, item.CoverPath, item.ThumbnailPath }.FirstOrDefault(File.Exists);
        PlaceholderIcon.Visibility = cover is null ? Visibility.Visible : Visibility.Collapsed;
        ImageLoader.SetSource(CoverImage, cover);
        ToolTip = item.FilePath;
    }

    private static void OnSelectionModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (VideoFileCard)d;
        card.SelectionBadge.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        if (!(bool)e.NewValue) card.SetCurrentValue(IsSelectedProperty, false);
    }

    private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (VideoFileCard)d;
        card.SelectionMark.Text = (bool)e.NewValue ? "✓" : "";
        card.SelectedChanged?.Invoke(card.Item!, (bool)e.NewValue);
    }

    private void Selection_Click(object sender, MouseButtonEventArgs e)
    {
        if (!SelectionMode || Item is null) return;
        e.Handled = true;
        IsSelected = !IsSelected;
    }

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        PlayOverlay.Visibility = Visibility.Visible;
        FavoriteHeart.Visibility = Visibility.Visible;
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        PlayOverlay.Visibility = Visibility.Collapsed;
        FavoriteHeart.Visibility = Visibility.Collapsed;
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (Item is null) return;
        if (SelectionMode)
        {
            IsSelected = !IsSelected;
            return;
        }
        if (!Item.FileExists)
        {
            ToastService.Show("文件不存在或已被移动", ToastKind.Error);
            Logger?.Warn($"[VideoFileCard] 播放失败，文件不存在: {Item.FilePath}");
            return;
        }
        // 左键点卡片 = 查看详情（由宿主视图处理）；按钮点击走 Play/OpenFolder。
        DetailRequested?.Invoke(Item);
    }

    public event Action<VideoItem>? DetailRequested;

    private void Play_Click(object sender, MouseButtonEventArgs e) => Play(e);

    private void OpenFolder_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (Item is null || Item.FilePath.Length == 0) return;
        var dir = Path.GetDirectoryName(Item.FilePath)!;
        if (!Directory.Exists(dir)) return;
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        App.Services.GetRequiredService<VideoLibraryService>().RecordFolderOpen(Item.Id);
    }

    internal void Play(RoutedEventArgs? e = null)
    {
        e ??= null;
        if (Item is null || !Item.FileExists)
        {
            ToastService.Show("文件不存在或已被移动", ToastKind.Error);
            return;
        }
        try
        {
            Logger?.Info($"[VideoFileCard] 播放: {Item.FileName} ({Item.FilePath})");
            Process.Start(new ProcessStartInfo(Item.FilePath!) { UseShellExecute = true });
            App.Services.GetRequiredService<VideoLibraryService>().RecordWatch(Item.Id);
            Render(Item);
        }
        catch (Exception ex)
        {
            Logger?.Error($"[VideoFileCard] 播放失败: {Item.FilePath}", ex);
            ToastService.ShowError(ex, "播放失败：");
        }
        finally
        {
            if (e is not null) e.Handled = true;
        }
    }

    private void Favorite_Click(object sender, MouseButtonEventArgs e)
    {
        if (Item is null) return;
        Item.IsFavorite = !Item.IsFavorite;
        e.Handled = true;
        Render(Item);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}

