using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ResourceGrab.App.Common;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Services;
using ResourceGrab.App.ViewModels;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources.Novel;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Views;

/// <summary>
/// 在线小说搜索页。单源（爱丽丝书屋），站点侧只按"页"翻，不做无限滚动。
/// 本地索引已加载时，命中本地库的书挂「✓ 本地」，进详情页会用本地数据。
/// </summary>
public partial class NovelOnlineView : CardGridViewBase
{
    private readonly NovelSourceManager _sources;
    private readonly NovelLocalMatcher _matcher;
    private readonly NovelIndexService _index;

    private readonly List<ToggleButton> _fieldTabs = new();
    private NovelSearchField _field = NovelSearchField.All;
    private int _page = 1;
    private int _searchVersion;
    private CancellationTokenSource? _searchCts;

    public ObservableCollection<MediaCardViewModel> Results { get; } = new();

    public StateKind CurrentState
    {
        get => (StateKind)GetValue(CurrentStateProperty);
        set => SetValue(CurrentStateProperty, value);
    }

    public static readonly DependencyProperty CurrentStateProperty = DependencyProperty.Register(
        nameof(CurrentState), typeof(StateKind), typeof(NovelOnlineView), new PropertyMetadata(StateKind.Hint));

    public System.Windows.Input.ICommand RetryCommand { get; }

    public NovelOnlineView()
    {
        InitializeComponent();
        _sources = App.Services.GetRequiredService<NovelSourceManager>();
        _index = App.Services.GetRequiredService<NovelIndexService>();
        _matcher = new NovelLocalMatcher(_index);

        RetryCommand = new RelayCommand(_ => _ = SearchAsync(1, force: true));
        BuildFieldTabs();
    }

    private NovelSearchField FieldOf(ToggleButton tab) => (NovelSearchField)(tab.Tag ?? NovelSearchField.All);

    private void BuildFieldTabs()
    {
        _fieldTabs.Clear();
        Toolbar.Chips.Children.Clear();
        foreach (var (field, label) in new[]
                 {
                     (NovelSearchField.All, "全部"),
                     (NovelSearchField.Title, "书名"),
                     (NovelSearchField.Author, "作者"),
                     (NovelSearchField.Tag, "标签"),
                 })
        {
            var tab = new ToggleButton
            {
                Style = (Style)FindResource("FilterMultiTabStyle"),
                Content = label,
                Tag = field,
                IsChecked = field == NovelSearchField.All,
                Margin = new Thickness(0, 0, 6, 6),
            };
            tab.Click += FieldTab_Click;
            Toolbar.Chips.Children.Add(tab);
            _fieldTabs.Add(tab);
        }
    }

    private void FieldTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton current) return;
        var field = FieldOf(current);
        // 单选：点谁选谁，再点保持选中
        foreach (var tab in _fieldTabs) tab.IsChecked = ReferenceEquals(tab, current);
        if (field == _field) return;
        _field = field;
        if (!string.IsNullOrWhiteSpace(Toolbar.SearchBox.Text))
            _ = SearchAsync(1, force: true);
    }

    private void KeywordSearch_SearchSubmitted(object? sender, string e) => _ = SearchAsync(1, force: true);

    private void SearchButton_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(1, force: true);

    private void PrevButton_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(_page - 1);

    private void NextButton_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(_page + 1);

    private async Task SearchAsync(int page, bool force = false)
    {
        var keyword = Toolbar.SearchBox.Text.Trim();
        if (keyword.Length == 0)
        {
            ToastService.Show("请输入书名 / 作者 / 标签", ToastKind.Info);
            return;
        }
        if (page < 1) return;

        var source = _sources.Current;
        if (source is null)
        {
            CurrentState = StateKind.Empty;
            ToastService.Show("没有可用的在线书源", ToastKind.Error);
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        _page = page;
        var version = ++_searchVersion;
        CurrentState = StateKind.Loading;
        PagingPanel.Visibility = Visibility.Collapsed;

        try
        {
            var result = await source.SearchAsync(keyword, page, _field, ct);
            if (version != _searchVersion || ct.IsCancellationRequested) return;

            Results.Clear();
            foreach (var item in result.Items)
            {
                var local = SafeMatch(item.Title);
                Results.Add(new NovelOnlineCardAdapter(source, item, local));
            }

            if (Results.Count == 0)
            {
                CurrentState = StateKind.Empty;
                return;
            }

            PageText.Text = $"第 {Math.Max(1, page)} / {Math.Max(1, result.TotalPages)} 页";
            PrevButton.IsEnabled = page > 1;
            NextButton.IsEnabled = page < result.TotalPages;
            PagingPanel.Visibility = Visibility.Visible;
            CurrentState = StateKind.Result;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version != _searchVersion) return;
            CurrentState = StateKind.Empty;
            ToastService.ShowError(ex, "搜索失败：");
        }
    }

    /// <summary>本地索引没加载时不做匹配，也不因为它抛错。</summary>
    private NovelLocalMatch? SafeMatch(string title)
    {
        try
        {
            return _matcher.Find(title);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>供外部（导航栏/设置）触发一次搜索。</summary>
    public void Search(string keyword)
    {
        Toolbar.SearchBox.Text = keyword;
        _ = SearchAsync(1, force: true);
    }

    /// <summary>按标签搜（详情页点标签、且标签不是分类名时走这里）。</summary>
    public void SearchTag(string tag)
    {
        _field = NovelSearchField.Tag;
        foreach (var tab in _fieldTabs) tab.IsChecked = FieldOf(tab) == NovelSearchField.Tag;
        Toolbar.SearchBox.Text = tag;
        _ = SearchAsync(1, force: true);
    }
}
