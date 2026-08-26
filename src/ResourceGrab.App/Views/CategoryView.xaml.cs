using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceGrab.App.Common;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Services;
using ResourceGrab.App.ViewModels;
using ResourceGrab.Core;
using ResourceGrab.Core.Downloading;
using ResourceGrab.Core.Http;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources.Jm;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Views;

/// <summary>分类页：左侧主题分区（对应网站 /theme/ 页）+ 分类/标签搜索，点击后组合搜索。</summary>
public partial class CategoryView : CardGridViewBase
{
    private const int PageSize = 20;

    private readonly JmHttpClient _client;
    private readonly ConfigService _config;
    private readonly DownloadManager _downloadManager;
    private readonly LocalLibraryService _localLibrary;

    /// <summary>已下载漫画的 (源,id) 键集合（卡片右上角徽章）。</summary>
    private HashSet<string> _downloadedKeys = new();
    private SearchSort _sort = SearchSort.Latest;
    private RankPeriod _period = RankPeriod.All;
    private long _total;
    private long _page = 1;
    private string _keyword = "";
    private string _categoryPath = "";
    private bool _hasLoaded;

    public ObservableCollection<AlbumCardViewModel> Results { get; } = new();

    /// <summary>内容区当前状态，驱动统一状态控件切换模板。</summary>
    public StateKind CurrentState
    {
        get => (StateKind)GetValue(CurrentStateProperty);
        set => SetValue(CurrentStateProperty, value);
    }

    public static readonly DependencyProperty CurrentStateProperty = DependencyProperty.Register(
        nameof(CurrentState),
        typeof(StateKind),
        typeof(CategoryView),
        new PropertyMetadata(StateKind.Hint));

    public ICommand RetryCommand { get; }

    public CategoryView()
    {
        InitializeComponent();
        _client = App.Services.GetRequiredService<JmHttpClient>();
        _config = App.Services.GetRequiredService<ConfigService>();
        _downloadManager = App.Services.GetRequiredService<DownloadManager>();
        _localLibrary = App.Services.GetRequiredService<LocalLibraryService>();
        RetryCommand = new RelayCommand(_ => _ = LoadAsync(1));
        SortBox.SelectedIndex = 0;
        PeriodBox.SelectedIndex = 0;
        SidebarPanel.ItemsSource = ThemeCatalog.Sections;
    }

    /// <summary>导航到本页时调用：仅首次加载，之后保持已浏览状态。</summary>
    public async void OnShown()
    {
        if (!_hasLoaded)
        {
            await LoadAsync(1);
        }
    }

    /// <summary>点击左侧分类或标签：分类切换路径并清除关键词；标签以关键词与当前分类组合搜索。</summary>
    private void Entry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ThemeEntry entry })
        {
            return;
        }

        if (sender is RadioButton)
        {
            if (_categoryPath == entry.Slug)
            {
                return;
            }
            _categoryPath = entry.Slug;
            KeywordBox.Clear();
        }
        else
        {
            KeywordBox.Text = entry.Name;
        }

        _ = LoadAsync(1);
    }

    private void KeywordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = LoadAsync(1);
        }
    }

    private void KeywordBox_GotFocus(object sender, RoutedEventArgs e)
    {
        UpdateKeywordPlaceholder();
        KeywordBox.CaretIndex = 0;
    }

    private void KeywordBox_LostFocus(object sender, RoutedEventArgs e) => UpdateKeywordPlaceholder();

    private void KeywordBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateKeywordPlaceholder();

    /// <summary>占位提示仅在「内容为空且未聚焦」时显示。</summary>
    private void UpdateKeywordPlaceholder()
    {
        KeywordPlaceholder.Visibility =
            string.IsNullOrEmpty(KeywordBox.Text) && !KeywordBox.IsKeyboardFocused
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(1);

    private void ClearKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        KeywordBox.Clear();
        KeywordBox.Focus();
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(_page - 1);

    private void NextButton_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(_page + 1);

    public void RefreshDownloadedKeys(HashSet<string> keys)
    {
        _downloadedKeys = keys;
        foreach (var item in Results)
        {
            item.IsDownloaded = keys.Contains(LocalLibraryService.KeyFor(item.SourceId, item.Id));
        }
    }


    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            _sort = Enum.TryParse<SearchSort>(tag, out var sort) ? sort : SearchSort.Latest;
        }
    }

    private void PeriodBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PeriodBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            var period = Enum.TryParse<RankPeriod>(tag, out var parsed) ? parsed : RankPeriod.All;
            if (period != _period)
            {
                _period = period;
                _ = LoadAsync(1);
            }
        }
    }

    private async Task LoadAsync(long page)
    {
        var keyword = KeywordBox.Text.Trim();
        if (page < 1)
        {
            return;
        }

        _keyword = keyword;
        _page = page;
        SetBusy(true);
        try { var dir = _config.Current.DownloadDir; _downloadedKeys = await Task.Run(() => _localLibrary.GetDownloadedKeys(dir)); } catch { _downloadedKeys = new HashSet<string>(); }
        try
        {
            // 顶级分类 + 空关键词：走分类过滤接口（精确）；子分类或带关键词：走搜索接口（路径 + 关键词）
            var resp = string.IsNullOrEmpty(keyword) && !_categoryPath.Contains('/')
                ? await _client.CategoriesFilterAsync(page, _sort, _period, _categoryPath)
                : await _client.SearchPhotosAsync(keyword, page, _sort, _period, _categoryPath);
            if (resp.IsAlbum && resp.AlbumRespData is { } album)
            {
                Results.Clear();
                ShowState(StateKind.Result);
                Navigation.OpenAlbum(album.Id);
                return;
            }

            Results.Clear();
            var data = resp.SearchRespData;
            if (data is null || data.Content.Count == 0)
            {
                ShowState(StateKind.Empty);
                return;
            }

            _total = data.Total;
            foreach (var item in data.Content)
            {
                Results.Add(ToCard(item));
            }
            var totalPages = _total > 0
                ? Math.Max(1, (_total + PageSize - 1) / PageSize)
                : page + (Results.Count >= PageSize ? 1 : 0);
            PageText.Text = $"第 {page} / {totalPages} 页";
            PrevButton.IsEnabled = page > 1;
            NextButton.IsEnabled = page < totalPages;
            ShowState(StateKind.Result);
        }
        catch (Exception ex)
        {
            ShowState(StateKind.Empty);
            ToastService.ShowError(ex);
        }
        finally
        {
            _hasLoaded = true;
            SetBusy(false);
        }
    }

    private AlbumCardViewModel ToCard(AlbumInSearchRespData item)
    {
        var id = long.TryParse(item.Id, out var parsed) ? parsed : 0;
        return new AlbumCardViewModel
        {
            Id = item.Id,
            SourceId = "jm",
            Name = item.Name,
            AuthorText = string.IsNullOrEmpty(item.Author) ? "未知作者" : item.Author,
            CoverUrl = JmSource.NormalizeCover(id, item.Image),
            IsFavorite = item.IsFavorite,
            IsDownloaded = _downloadedKeys.Contains(LocalLibraryService.KeyFor("jm", item.Id)),
            OpenCommand = new RelayCommand(_ => Navigation.OpenAlbum(id)),
            DownloadCommand = new AsyncRelayCommand(async _ =>
            {
                try
                {
                    var (count, title) = await DownloadHelper.EnqueueAllAsync(
                        _client, _config, _downloadManager, id);
                    ToastService.Show($"已将「{title}」全部 {count} 个章节加入下载队列", ToastKind.Success);
                }
                catch (Exception ex)
                {
                    ToastService.ShowError(ex);
                }
            }),
        };
    }

    private void SetBusy(bool busy)
    {
        if (busy)
        {
            CurrentState = StateKind.Loading;
            PagingPanel.Visibility = Visibility.Collapsed;
        }
        else if (CurrentState == StateKind.Loading)
        {
            CurrentState = StateKind.Hint;
        }
    }

    private void ShowState(StateKind state)
    {
        CurrentState = state;
        PagingPanel.Visibility = state == StateKind.Result ? Visibility.Visible : Visibility.Collapsed;
    }
}


