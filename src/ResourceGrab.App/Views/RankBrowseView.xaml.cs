using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceGrab.App.Common;
using ResourceGrab.App.Controls;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Services;
using ResourceGrab.App.ViewModels;
using ResourceGrab.Core.Downloading;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources;

namespace ResourceGrab.App.Views;

/// <summary>
/// 通用排行浏览页：面向任意实现 IRankSource 的内容源（如绅士漫画收藏排行榜）。
/// 顶部周期 Tab（今日/本週/本月/本年），下方卡片网格 + 分页。
/// </summary>
public partial class RankBrowseView : CardGridViewBase
{
    private readonly SourceManager _sourceManager;
    private readonly ConfigService _config;
    private readonly DownloadManager _downloadManager;
    private readonly LocalLibraryService _localLibrary;

    /// <summary>已下载漫画的 (源,id) 键集合（卡片右上角徽章）。</summary>
    private HashSet<string> _downloadedKeys = new();
    private IRankSource? _source;
    private string _periodId = "";
    private long _page = 1;
    private long _totalPages = 1;

    public ObservableCollection<AlbumCardViewModel> Results { get; } = new();

    /// <summary>内容区当前状态。</summary>
    public StateKind CurrentState
    {
        get => (StateKind)GetValue(CurrentStateProperty);
        set => SetValue(CurrentStateProperty, value);
    }

    public static readonly DependencyProperty CurrentStateProperty = DependencyProperty.Register(
        nameof(CurrentState),
        typeof(StateKind),
        typeof(RankBrowseView),
        new PropertyMetadata(StateKind.Loading));

    public ICommand RetryCommand { get; }

    public RankBrowseView()
    {
        InitializeComponent();
        _sourceManager = App.Services.GetRequiredService<SourceManager>();
        _config = App.Services.GetRequiredService<ConfigService>();
        _downloadManager = App.Services.GetRequiredService<DownloadManager>();
        _localLibrary = App.Services.GetRequiredService<LocalLibraryService>();
        RetryCommand = new RelayCommand(_ =>
        {
            if (_periodId.Length > 0)
                _ = LoadAsync(_periodId, _page);
            else if (_source is { } source && source.GetRankPeriods().Count > 0)
                _ = LoadAsync(source.GetRankPeriods()[0].Id, 1);
        });
    }

    /// <summary>导航到本页时调用：按当前源加载周期 Tab 与排行数据。</summary>
    public async void OnShown()
    {
        if (_sourceManager.Current is not IRankSource source)
        {
            return;
        }
        _source = source;
        RankTitle.Text = $"{_sourceManager.Current.Info.DisplayName} 排行";
        BuildPeriodTabs();
        if (_periodId.Length == 0)
        {
            await LoadAsync(_periodId = source.GetRankPeriods()[0].Id, 1);
        }
    }

    public void RefreshDownloadedKeys(HashSet<string> keys)
    {
        _downloadedKeys = keys;
        foreach (var item in Results)
        {
            item.IsDownloaded = keys.Contains(LocalLibraryService.KeyFor(item.SourceId, item.Id));
        }
    }

    private void BuildPeriodTabs()
    {
        PeriodTabs.Children.Clear();
        foreach (var period in _source!.GetRankPeriods())
        {
            var tab = new RadioButton
            {
                Style = (Style)FindResource("RankTabStyle"),
                Content = period.Name,
                Tag = period,
                IsChecked = _periodId == period.Id,
                Margin = new Thickness(6, 0, 0, 0),
            };
            tab.Click += PeriodTab_Click;
            PeriodTabs.Children.Add(tab);
        }
    }

    private async void PeriodTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: RankPeriodInfo period })
        {
            await LoadAsync(period.Id, 1);
        }
    }

    private async Task LoadAsync(string periodId, long page)
    {
        if (_source is null || page < 1)
        {
            return;
        }
        _periodId = periodId;
        _page = page;
        CurrentState = StateKind.Loading;
        PagingPanel.Visibility = Visibility.Collapsed;
        try { var dir = _config.Current.DownloadDir; _downloadedKeys = await Task.Run(() => _localLibrary.GetDownloadedKeys(dir)); } catch { _downloadedKeys = new HashSet<string>(); }
        try
        {
            var result = await _source.GetRankAsync(periodId, (int)page);
            Results.Clear();
            if (result.Items.Count == 0)
            {
                CurrentState = StateKind.Empty;
                return;
            }

            foreach (var item in result.Items)
            {
                Results.Add(ToCard(item));
            }
            _totalPages = result.TotalPages;
            PageText.Text = $"第 {Math.Max(1, page)} / {Math.Max(1, _totalPages)} 页";
            PrevButton.IsEnabled = page > 1;
            NextButton.IsEnabled = page < _totalPages;
            CurrentState = StateKind.Result;
            PagingPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            CurrentState = StateKind.Empty;
            ToastService.ShowError(ex);
        }
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(_periodId, _page - 1);

    private void NextButton_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(_periodId, _page + 1);

    private AlbumCardViewModel ToCard(ComicSummary item)
    {
        var source = _sourceManager.Current;
        return new AlbumCardViewModel
        {
            Id = item.Id,
            SourceId = source.Info.Id,
            Name = item.Title,
            AuthorText = string.IsNullOrEmpty(item.Author) ? "未知作者" : item.Author,
            CoverUrl = item.CoverUrl,
            ImageHeaders = source.Info.CoverHeaders,
            IsDownloaded = _downloadedKeys.Contains(LocalLibraryService.KeyFor(source.Info.Id, item.Id)),
            OpenCommand = new RelayCommand(_ => Navigation.OpenComic(source.Info.Id, item.Id)),
            DownloadCommand = new AsyncRelayCommand(async _ =>
            {
                try
                {
                    var (count, title) = await DownloadHelper.EnqueueAllAsync(
                        source, _config, _downloadManager, item.Id);
                    ToastService.Show($"已将「{title}」全部 {count} 个章节加入下载队列", ToastKind.Success);
                }
                catch (Exception ex)
                {
                    ToastService.ShowError(ex);
                }
            }),
        };
    }

}
