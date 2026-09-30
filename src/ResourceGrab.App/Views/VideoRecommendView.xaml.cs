using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Controls;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;

namespace ResourceGrab.App.Views;

/// <summary>
/// 视频推荐页：基于本地库数据分组展示（最近添加 / 高评分 / 热门系列）。
/// 分组块使用 RecommendSection 竖向换行网格，整页只有一个竖向滚动。
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
        SectionsHost.Children.Clear();

        // 按番号折叠：同文件夹的推广视频曾被刮成同一番号，库里同番号条目很多，所有行都要先去重
        var items = VideoLibraryService.CollapseByNumber(_library.Items);
        if (items.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            return;
        }
        EmptyState.Visibility = Visibility.Collapsed;

        AddSection("最近添加", items.OrderByDescending(i => i.AddedDate).ToList());
        AddSection("高评分", items.Where(i => i.Score > 0)
            .OrderByDescending(i => i.Score)
            .ThenByDescending(i => i.UserRating)
            .ToList());

        // 热门系列 — 每系列一个子分组（members 已按番号折叠，系列内不再有同番号重复）
        var seriesGroups = items
            .Where(i => !string.IsNullOrEmpty(i.Series))
            .GroupBy(i => i.Series)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .ToList();
        if (seriesGroups.Count > 0)
        {
            SectionsHost.Children.Add(MakeGroupHeader("热门系列"));
            foreach (var group in seriesGroups)
            {
                var members = group.OrderByDescending(i => i.AddedDate).ToList();
                var section = new RecommendSection(group.Key, sub: true);
                section.SetItems(members, MakeCard, members.Count, members.Count);
                SectionsHost.Children.Add(section);
            }
        }
    }

    private void AddSection(string title, IReadOnlyList<VideoItem> items)
    {
        if (items.Count == 0) return;
        var section = new RecommendSection(title);
        section.SetItems(items, MakeCard, 12, 60);
        SectionsHost.Children.Add(section);
    }

    private FrameworkElement MakeCard(VideoItem item)
    {
        var card = new VideoPosterCard();
        card.Bind(item);
        card.DetailRequested += OnDetailRequested;
        return card;
    }

    private static TextBlock MakeGroupHeader(string text)
    {
        var header = new TextBlock
        {
            Text = text,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 2, 0, 8),
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return header;
    }

    private static void OnDetailRequested(VideoItem item)
    {
        // 点击海报卡片：打开所在目录
        var dir = System.IO.Path.GetDirectoryName(item.FilePath);
        if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
        {
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
    }
}
