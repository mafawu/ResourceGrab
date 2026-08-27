using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Controls;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;

namespace ResourceGrab.App.Views;

/// <summary>
/// 视频推荐页：基于本地库数据分组展示。
/// 使用 PosterCard 海报卡片，包含最近添加、高评分、热门系列三个横向滚动区。
/// </summary>
public partial class VideoRecommendView : UserControl
{
    private readonly VideoLibraryService _library;
    private bool _loaded;

    public VideoRecommendView()
    {
        InitializeComponent();
        _library = App.Services.GetRequiredService<VideoLibraryService>();
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; Refresh(); } };
    }

    public void OnShown() => Refresh();

    private void Refresh()
    {
        var items = _library.Items;
        if (items.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            return;
        }
        EmptyState.Visibility = Visibility.Collapsed;

        // 最近添加 — 海报卡片
        var recent = items
            .OrderByDescending(i => i.AddedDate)
            .Take(20)
            .ToList();
        RenderPosterRow(RecentPanel, recent);

        // 高评分 — 海报卡片
        var topRated = items
            .Where(i => i.Score > 0)
            .OrderByDescending(i => i.Score)
            .Take(20)
            .ToList();
        if (topRated.Count > 0)
            RenderPosterRow(TopRatedPanel, topRated);
        else
            TopRatedPanel.Children.Clear();

        // 热门系列 — 每系列一行海报卡片
        SeriesPanel.Children.Clear();
        var seriesGroups = items
            .Where(i => !string.IsNullOrEmpty(i.Series))
            .GroupBy(i => i.Series)
            .OrderByDescending(g => g.Count())
            .Take(5);

        foreach (var group in seriesGroups)
        {
            var header = new TextBlock
            {
                Text = $"{group.Key} ({group.Count()})",
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 6),
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
            };
            SeriesPanel.Children.Add(header);

            var scroll = PosterGridHelper.CreateVideoPosterRow(group.Take(10).ToList());
            SeriesPanel.Children.Add(scroll);
        }
    }

    private void RenderPosterRow(StackPanel panel, List<VideoItem> items)
    {
        panel.Children.Clear();
        foreach (var item in items)
        {
            var card = new VideoPosterCard();
            card.Bind(item);
            card.DetailRequested += OnDetailRequested;
            panel.Children.Add(card);
        }
    }

    private static void OnDetailRequested(VideoItem item)
    {
        // 点击海报卡片：尝试导航到视频本地页并选中该项
        // 这里简单用文件管理器打开目录作为演示
        var dir = System.IO.Path.GetDirectoryName(item.FilePath);
        if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
        {
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
    }
}
