using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ResourceGrab.App.Common;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Services;
using ResourceGrab.App.ViewModels;
using ResourceGrab.Core.Downloading;
using ResourceGrab.Core.Http;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources.Jm;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Views;

/// <summary>
/// 每周必看页：分类下拉 + 类型分段 + 漫画卡片网格。
/// 列表中的漫画复用 AlbumCard（打开详情 / 一键下载），
/// 并根据本地已下载漫画标记「已下载」徽章。
/// </summary>
public partial class WeeklyView : CardGridViewBase
{
    private readonly JmHttpClient _client;
    private readonly ConfigService _config;
    private readonly DownloadManager _downloadManager;
    private readonly LocalLibraryService _localLibrary;

    private List<CategoryInWeeklyInfo> _categories = new();
    private string _categoryId = "";
    private string _typeId = "";
    private HashSet<string> _downloadedKeys = new();
    private bool _hasLoaded;
    private bool _loading;
    private int _listVersion;

    public ObservableCollection<AlbumCardViewModel> Items { get; } = new();

    public WeeklyView()
    {
        InitializeComponent();
        _client = App.Services.GetRequiredService<JmHttpClient>();
        _config = App.Services.GetRequiredService<ConfigService>();
        _downloadManager = App.Services.GetRequiredService<DownloadManager>();
        _localLibrary = App.Services.GetRequiredService<LocalLibraryService>();
        WeeklyItems.ItemsSource = Items;
    }

    /// <summary>切换到本页时调用：仅首次加载，之后保持已浏览状态；需要更新时可点手动刷新。</summary>
    public async void OnShown()
    {
        if (_loading || _hasLoaded)
        {
            return;
        }
        await LoadInfoAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadInfoAsync();

    /// <summary>加载每周必看信息（分类 + 类型），默认选中第一个分类与最后一个类型。</summary>
    private async Task LoadInfoAsync()
    {
        if (_loading)
        {
            return;
        }
        _loading = true;
        ShowState(State.Loading);
        try
        {
            var info = await _client.GetWeeklyInfoAsync();
            _categories = info.Categories;
            if (_categories.Count == 0 || info.Types.Count == 0)
            {
                ShowState(State.Empty);
                return;
            }

            // 分类下拉
            CategoryBox.Items.Clear();
            foreach (var category in _categories)
            {
                CategoryBox.Items.Add(new ComboBoxItem { Content = category.Time, Tag = category.Id });
            }
            CategoryBox.SelectedIndex = 0;

            // 类型分段（默认最后一个，与站点默认一致）
            TypePanel.Children.Clear();
            for (var i = 0; i < info.Types.Count; i++)
            {
                var type = info.Types[i];
                var button = new RadioButton
                {
                    Content = type.Title,
                    Tag = type.Id,
                    GroupName = "WeeklyType",
                    Style = (Style)FindResource("NavItemStyle"),
                    IsChecked = i == info.Types.Count - 1,
                };
                button.Click += TypeButton_Click;
                TypePanel.Children.Add(button);
            }
            _typeId = info.Types[^1].Id;

            await LoadListAsync();
            _hasLoaded = true;
        }
        catch (Exception ex)
        {
            ShowState(State.Empty);
            ToastService.ShowError(ex);
        }
        finally
        {
            _loading = false;
        }
    }

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryBox.SelectedItem is ComboBoxItem { Tag: string id })
        {
            _categoryId = id;
            _ = LoadListAsync();
        }
    }

    private void TypeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string id })
        {
            _typeId = id;
            _ = LoadListAsync();
        }
    }

    /// <summary>加载当前分类 + 类型下的每周必看列表。</summary>
    private async Task LoadListAsync()
    {
        if (string.IsNullOrEmpty(_categoryId) || string.IsNullOrEmpty(_typeId))
        {
            return;
        }
        var version = ++_listVersion;
        ShowState(State.Loading);
        try
        {
            var resp = await _client.GetWeeklyAsync(_categoryId, _typeId);
            if (version != _listVersion)
            {
                return; // 已有更新的请求，丢弃过期响应
            }
            try { var dir = _config.Current.DownloadDir; _downloadedKeys = await Task.Run(() => _localLibrary.GetDownloadedKeys(dir)); } catch { _downloadedKeys = new HashSet<string>(); }

            Items.Clear();
            foreach (var item in resp.List)
            {
                Items.Add(ToCard(item));
            }
            WeeklyCount.Text = resp.Total > 0 ? $"共 {resp.Total} 部" : "";
            ShowState(Items.Count == 0 ? State.Empty : State.Result);
        }
        catch (Exception ex)
        {
            ShowState(State.Empty);
            ToastService.ShowError(ex);
        }
    }

    public void RefreshDownloadedKeys(HashSet<string> keys)
    {
        _downloadedKeys = keys;
        foreach (var item in Items)
        {
            item.IsDownloaded = keys.Contains(LocalLibraryService.KeyFor(item.SourceId, item.Id));
        }
    }

    private AlbumCardViewModel ToCard(ComicInWeeklyRespData item)
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

    private void ShowState(State state)
    {
        LoadingPanel.Visibility = state == State.Loading ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = state == State.Empty ? Visibility.Visible : Visibility.Collapsed;
        ResultScroller.Visibility = state == State.Result ? Visibility.Visible : Visibility.Collapsed;
    }

    private enum State
    {
        Loading,
        Empty,
        Result,
    }
}

