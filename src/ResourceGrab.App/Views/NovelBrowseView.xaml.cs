using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ResourceGrab.App.Common;
using ResourceGrab.App.Services;
using ResourceGrab.Core.Sources.Novel;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Views;

/// <summary>
/// 在线书库的「分类」与「排行」共用一页：同一套表格行、同一套分页，
/// 只是数据来源与筛选条件不同（分类=分类 chips + 排序；排行=周期 tabs）。
/// </summary>
public partial class NovelBrowseView : UserControl
{
    public enum BrowseMode
    {
        Category,
        Rank,
    }

    private readonly NovelSourceManager _sources;
    private readonly BrowseMode _mode;

    private IReadOnlyList<NovelCategory> _categories = Array.Empty<NovelCategory>();
    private string _categoryId = "";
    private NovelListSort _sort = NovelListSort.Update;
    private NovelRankPeriod _period = NovelRankPeriod.Day;
    private int _page = 1;
    private int _totalPages = 1;
    private int _loadVersion;
    private CancellationTokenSource? _cts;

    public ObservableCollection<NovelRowItem> Rows { get; } = new();

    public NovelBrowseView() : this(BrowseMode.Category) { }

    public NovelBrowseView(BrowseMode mode)
    {
        InitializeComponent();
        _mode = mode;
        _sources = App.Services.GetRequiredService<NovelSourceManager>();
        ItemsList.ItemsSource = Rows;

        TitleText.Text = mode == BrowseMode.Category ? "分类" : "排行";
        BuildModeTabs();
        // 排行没有分类 chips，别留一条空白卡片
        if (mode == BrowseMode.Rank) ChipCard.Visibility = Visibility.Collapsed;
        // 切回本页时保留当前页码，不要退回第 1 页
        Loaded += (_, _) => _ = LoadAsync(_page);
    }

    /// <summary>外部指定分类（详情页点标签进来）。</summary>
    public void SelectCategory(string categoryId)
    {
        if (string.IsNullOrWhiteSpace(categoryId) || _mode != BrowseMode.Category) return;
        _categoryId = categoryId;
        if (_categories.Count > 0) BuildCategoryChips();
        _ = LoadAsync(1);
    }

    private NovelListSort CurrentSort => _sort;

    private void BuildModeTabs()
    {
        if (_mode == BrowseMode.Rank)
        {
            // 排行：日 / 周 / 月 / 总
            foreach (var (period, label) in new[]
                     {
                         (NovelRankPeriod.Day, "本日"),
                         (NovelRankPeriod.Week, "本周"),
                         (NovelRankPeriod.Month, "本月"),
                         (NovelRankPeriod.All, "总榜"),
                     })
            {
                var btn = new ToggleButton
                {
                    Style = (Style)FindResource("FilterMultiTabStyle"),
                    Content = label,
                    Tag = period,
                    IsChecked = period == _period,
                    Margin = new Thickness(0, 0, 6, 0),
                };
                btn.Click += RankTab_Click;
                ModeTabs.Children.Add(btn);
            }
            SubText.Text = "站点热度榜";
        }
        else
        {
            // 分类：排序 chips（更新 / 字数 / 人气）
            foreach (var (sort, label) in new[]
                     {
                         (NovelListSort.Update, "最近更新"),
                         (NovelListSort.WordCount, "字数最多"),
                         (NovelListSort.Hits, "人气最高"),
                     })
            {
                var btn = new ToggleButton
                {
                    Style = (Style)FindResource("FilterMultiTabStyle"),
                    Content = label,
                    Tag = sort,
                    IsChecked = sort == _sort,
                    Margin = new Thickness(0, 0, 6, 0),
                };
                btn.Click += SortTab_Click;
                ModeTabs.Children.Add(btn);
            }
            SubText.Text = "按站点分类浏览";
        }
    }

    private void RankTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        foreach (ToggleButton child in ModeTabs.Children) child.IsChecked = ReferenceEquals(child, btn);
        if (btn.Tag is not NovelRankPeriod period || period == _period) return;
        _period = period;
        _ = LoadAsync(1);
    }

    private void SortTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        foreach (ToggleButton child in ModeTabs.Children) child.IsChecked = ReferenceEquals(child, btn);
        if (btn.Tag is not NovelListSort sort || sort == _sort) return;
        _sort = sort;
        _ = LoadAsync(1);
    }

    private async Task LoadAsync(int page = 1)
    {
        var source = _sources.Current;
        if (source is null)
        {
            Rows.Clear();
            SubText.Text = "没有可用的在线书源";
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var version = ++_loadVersion;
        _page = Math.Max(1, page);

        try
        {
            if (_mode == BrowseMode.Category && _categories.Count == 0)
            {
                _categories = await source.GetCategoriesAsync(ct);
                if (version != _loadVersion) return;
                BuildCategoryChips();
                if (_categoryId.Length == 0 && _categories.Count > 0) _categoryId = _categories[0].Id;
            }

            NovelSearchResult result = _mode == BrowseMode.Rank
                ? await source.GetRankAsync(_period, ct)
                : await source.GetCategoryAsync(_categoryId, _page, _sort, ct);

            if (version != _loadVersion || ct.IsCancellationRequested) return;

            Rows.Clear();
            var index = 0;
            foreach (var item in result.Items)
            {
                index++;
                Rows.Add(new NovelRowItem
                {
                    RankText = _mode == BrowseMode.Rank ? index.ToString() : index.ToString(),
                    Title = item.Title,
                    Author = item.Author,
                    WordText = item.WordCountText,
                    UpdatedText = item.UpdatedText,
                    HitsText = string.IsNullOrWhiteSpace(item.HitsText) ? "" : item.HitsText,
                    Summary = item,
                    Source = source,
                });
            }

            if (_mode == BrowseMode.Category)
            {
                _totalPages = Math.Max(1, result.TotalPages);
                PageText.Text = $"第 {_page} / {_totalPages} 页";
                PrevButton.IsEnabled = _page > 1;
                NextButton.IsEnabled = _page < _totalPages;
                PagingPanel.Visibility = Visibility.Visible;
            }

            var current = _categories.FirstOrDefault(c => c.Id == _categoryId);
            SubText.Text = _mode == BrowseMode.Rank
                ? $"站点热度榜 · {Rows.Count} 本"
                : (current is null ? $"共 {Rows.Count} 本" : $"{current.Name} · 共 {Rows.Count} 本");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _loadVersion) ToastService.ShowError(ex, "加载失败：");
        }
    }

    private void BuildCategoryChips()
    {
        ChipHost.Children.Clear();
        foreach (var category in _categories)
        {
            var btn = new ToggleButton
            {
                Style = (Style)FindResource("FilterMultiTabStyle"),
                Content = category.Name,
                Tag = category.Id,
                IsChecked = category.Id == _categoryId,
                Margin = new Thickness(0, 0, 6, 6),
            };
            btn.Click += CategoryChip_Click;
            ChipHost.Children.Add(btn);
        }
    }

    private void CategoryChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        foreach (ToggleButton child in ChipHost.Children) child.IsChecked = ReferenceEquals(child, btn);
        if (btn.Tag is not string id || id == _categoryId) return;
        _categoryId = id;
        _ = LoadAsync(1);
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is not NovelRowItem row) return;
        ItemsList.SelectedItem = null;
        Navigation.OpenNovelBook(row.Source, row.Summary);
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(_page - 1);

    private void Next_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(_page + 1);
}

/// <summary>分类 / 排行列表的一行。</summary>
public sealed class NovelRowItem
{
    public string RankText { get; init; } = "";
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string WordText { get; init; } = "";
    public string UpdatedText { get; init; } = "";
    public string HitsText { get; init; } = "";
    public NovelSummary Summary { get; init; } = new();
    public INovelSource Source { get; init; } = null!;
}
