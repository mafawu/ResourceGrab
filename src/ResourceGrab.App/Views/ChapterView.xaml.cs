using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ResourceGrab.Core;
using ResourceGrab.App.Common;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Services;
using ResourceGrab.App.ViewModels;
using ResourceGrab.Core.Downloading;
using ResourceGrab.Core.Http;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Sources.Jm;

namespace ResourceGrab.App.Views;

/// <summary>
/// 章节详情页：支持鼠标框选 + Ctrl 多选 + 右键菜单批量下载。
/// 站点差异收敛到 IComicSource，页面只依赖通用 ComicDetail / Chapter 模型。
/// </summary>
public partial class ChapterView : UserControl
{
    private readonly IComicSource _source;
    private readonly ConfigService _config;
    private readonly DownloadManager _downloadManager;
    private readonly SessionService _session;
    private readonly OnlinePageCountCacheService _pageCountCache;

    private ComicDetail? _detail;
    private bool _isFavorite;
    private readonly Dictionary<string, Chapter> _chapterMap = new();

    /// <summary>章节卡单元格尺寸（卡片 158x54 + 边距 10），框选命中按此换算。</summary>
    private const double ChapterCellWidth = 168;
    private const double ChapterCellHeight = 64;

    private Point _dragStart;
    private bool _isDragging;
    private int _pageCountVersion;
    private const int MaxMetadataLinkCount = 18;
    private string _headerMetaBaseText = "";

    public ObservableCollection<ChapterCardViewModel> Chapters { get; } = new();

    public ChapterView(IComicSource source, string comicId)
    {
        InitializeComponent();
        _source = source;
        _config = App.Services.GetRequiredService<ConfigService>();
        _downloadManager = App.Services.GetRequiredService<DownloadManager>();
        _session = App.Services.GetRequiredService<SessionService>();
        _pageCountCache = App.Services.GetRequiredService<OnlinePageCountCacheService>();
        FavoriteButton.Visibility = source.Info.SupportsFavorites ? Visibility.Visible : Visibility.Collapsed;
        ImageLoader.SetHeaders(CoverImage, source.Info.CoverHeaders);
        ChapterItems.ItemsSource = Chapters;
        _ = LoadAsync(comicId);
    }

    private async Task LoadAsync(string comicId)
    {
        LoadingPanel.Visibility = Visibility.Visible;
        try
        {
            var detail = await _source.GetComicAsync(comicId);
            _detail = detail;

            HeaderTitle.Text = detail.Title;
            RenderMetadataLinks(detail);
            _headerMetaBaseText = $"共 {detail.Chapters.Count} 章";
            HeaderMeta.Text = _headerMetaBaseText;
            HeaderDesc.Text = string.IsNullOrWhiteSpace(detail.Description) ? "暂无简介" : detail.Description;
            ImageLoader.SetSource(CoverImage, detail.CoverUrl);
            UpdateFavoriteButton();

            Chapters.Clear();
            _chapterMap.Clear();
            var chapterIndex = 0;
            foreach (var chapter in detail.Chapters)
            {
                var index = chapterIndex++;
                var card = new ChapterCardViewModel
                {
                    ChapterId = chapter.Id,
                    AlbumId = chapter.ComicId,
                    Title = chapter.Title,
                    ReadCommand = new RelayCommand(_ => Navigation.OpenOnlineReader(_source, detail.Chapters, index)),
                };
                Chapters.Add(card);
                _chapterMap[chapter.Id] = chapter;
            }
            UpdateSelectedText();
            ReadAllButton.IsEnabled = detail.Chapters.Count > 0;
            _ = LoadTotalImageCountAsync(comicId, detail);
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex);
            HeaderTitle.Text = "加载失败";
        }
        finally
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private const int MaxAutoPageCountChapters = 10;

    /// <summary>少于阈值时统计总页数；命中缓存则不请求章节图片列表。</summary>
    private async Task LoadTotalImageCountAsync(string comicId, ComicDetail detail)
    {
        if (detail.Chapters.Count == 0 || detail.Chapters.Count >= MaxAutoPageCountChapters)
        {
            return;
        }

        var sourceId = _source.Info.Id;
        if (_pageCountCache.TryGet(sourceId, comicId, detail.Chapters, out var cachedCount))
        {
            HeaderMeta.Text = $"{_headerMetaBaseText} · 共 {cachedCount:N0} P";
            return;
        }

        var version = ++_pageCountVersion;
        var loadedImageCount = 0L;
        var failedCount = 0;
        await Task.Run(async () =>
        {
            foreach (var chapter in detail.Chapters)
            {
                if (version != _pageCountVersion)
                {
                    return;
                }

                try
                {
                    var pages = await _source.GetChapterImagesAsync(chapter);
                    if (version != _pageCountVersion)
                    {
                        return;
                    }

                    if (pages.Count == 0)
                    {
                        continue;
                    }

                    loadedImageCount += pages.Count;
                    var chapterId = chapter.Id;
                    Dispatcher.Invoke(() =>
                    {
                        if (version != _pageCountVersion)
                        {
                            return;
                        }

                        HeaderMeta.Text = $"{_headerMetaBaseText} · 共 {loadedImageCount:N0} P";
                    });
                }
                catch
                {
                    // 统计失败时不写缓存，避免把部分总数误存为完整总页数。
                    failedCount++;
                }
            }

            if (version == _pageCountVersion && failedCount == 0)
            {
                _pageCountCache.Set(sourceId, comicId, detail.Chapters, loadedImageCount);
            }
        });
    }

    private void ReadAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is null || _detail.Chapters.Count == 0)
        {
            return;
        }
        Navigation.OpenOnlineReader(_source, _detail.Chapters, 0);
    }

    private void RenderMetadataLinks(ComicDetail detail)
    {
        AuthorLinks.Children.Clear();
        TagLinks.Children.Clear();

        var authors = detail.Authors
            .Where(IsUsableMetadataTerm)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxMetadataLinkCount);
        foreach (var author in authors)
        {
            AuthorLinks.Children.Add(CreateMetadataLink(author, isAuthor: true));
        }

        var tags = detail.Tags
            .Where(IsUsableMetadataTerm)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxMetadataLinkCount);
        foreach (var tag in tags)
        {
            TagLinks.Children.Add(CreateMetadataLink(tag, isAuthor: false));
        }

        AuthorLinks.Visibility = AuthorLinks.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TagLinks.Visibility = TagLinks.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private Button CreateMetadataLink(string value, bool isAuthor)
    {
        var query = value.Trim();
        var button = new Button
        {
            Style = (Style)FindResource("ChipButtonStyle"),
            Content = (isAuthor ? "@" : "#") + query,
            Tag = query,
            ToolTip = (isAuthor ? "搜索作者：" : "搜索标签：") + query
        };
        button.Click += MetadataChip_Click;
        return button;
    }

    private void MetadataChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string query })
        {
            Navigation.OpenSearch(query, _source.Info.Id);
        }
    }

    private static bool IsUsableMetadataTerm(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value.Trim(), "N/A", StringComparison.OrdinalIgnoreCase);
    }

    // ====================== 框选 ======================

    private void ChaptersLayer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }
        // 点中章节卡片或滚动条时不启动框选
        if (FindVisualParent<Border>(source, b => b.DataContext is ChapterCardViewModel) is not null ||
            FindVisualParent<ScrollBar>(source) is not null)
        {
            return;
        }

        _dragStart = e.GetPosition(SelectionCanvas);
        _isDragging = true;
        Canvas.SetLeft(SelectionRect, _dragStart.X);
        Canvas.SetTop(SelectionRect, _dragStart.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        SelectionRect.Visibility = Visibility.Visible;
        ChaptersLayer.CaptureMouse();
        e.Handled = true;
    }

    private void ChaptersLayer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }
        var pos = e.GetPosition(SelectionCanvas);
        var x = Math.Min(_dragStart.X, pos.X);
        var y = Math.Min(_dragStart.Y, pos.Y);
        var w = Math.Abs(pos.X - _dragStart.X);
        var h = Math.Abs(pos.Y - _dragStart.Y);
        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
        // 忽略过小的"点击"（点击空白处），避免误清空当前选择
        if (w < 4 && h < 4)
        {
            return;
        }
        ApplySelectionToCards(new Rect(x, y, w, h));
    }

    private void ChaptersLayer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }
        _isDragging = false;
        SelectionRect.Visibility = Visibility.Collapsed;
        ChaptersLayer.ReleaseMouseCapture();
        UpdateSelectedText();
        e.Handled = true;
    }

    private void ApplySelectionToCards(Rect selection)
    {
        // 章节卡为固定 158x54 + 边距 10 → 单元格 168x64；行由虚拟化网格按列数切分。
        // 直接用「行列 → 下标」换算命中范围（O(命中区域)），不再遍历全部章节或依赖
        // 已实例化容器（虚拟化后离屏章节本就没有容器），避免拖拽时逐帧 TransformToVisual。
        var columns = Math.Max(1, ChapterItems.Columns);
        if (columns <= 0 || Chapters.Count == 0)
        {
            return;
        }
        var origin = ChapterItems.TranslatePoint(new Point(0, 0), SelectionCanvas);
        origin.Y -= ChapterItems.VerticalOffset;

        var left = selection.Left - origin.X;
        var top = selection.Top - origin.Y;
        var right = selection.Right - origin.X;
        var bottom = selection.Bottom - origin.Y;

        var firstCol = Math.Max(0, (int)Math.Floor(left / ChapterCellWidth));
        var lastCol = Math.Min(columns - 1, (int)Math.Floor(right / ChapterCellWidth));
        var firstRow = Math.Max(0, (int)Math.Floor(top / ChapterCellHeight));
        var lastRow = Math.Min((Chapters.Count - 1) / columns, (int)Math.Floor(bottom / ChapterCellHeight));
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var col = firstCol; col <= lastCol; col++)
            {
                var index = row * columns + col;
                if (index >= 0 && index < Chapters.Count)
                {
                    Chapters[index].IsSelected = true;
                }
            }
        }
    }

    // ====================== 卡片点击 / 选择操作 ======================

    private void ChapterCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: ChapterCardViewModel card })
        {
            return;
        }
        // 点击「在线阅读」按钮时不触发选择
        if (e.OriginalSource is DependencyObject source && FindVisualParent<Button>(source, _ => true) is not null)
        {
            return;
        }
        // 单击切换选中状态：逐个点选即可多选，不再清空其他已选章节
        card.IsSelected = !card.IsSelected;
        UpdateSelectedText();
        e.Handled = true;
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var card in Chapters)
        {
            card.IsSelected = true;
        }
        UpdateSelectedText();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var card in Chapters)
        {
            card.IsSelected = false;
        }
        UpdateSelectedText();
    }

    private void UpdateSelectedText()
    {
        var count = Chapters.Count(c => c.IsSelected);
        SelectedText.Text = count == 0 ? "未选择章节" : $"已选 {count} 章";
        DownloadSelectedButton.Content = count == 0 ? "下载选中" : $"下载选中（{count}）";
    }

    // ====================== 下载 ======================

    private void ContextDownloadSelected_Click(object sender, RoutedEventArgs e) => DownloadSelected();

    private async void DownloadSelectedButton_Click(object sender, RoutedEventArgs e) => await DownloadSelectedAsync();

    private void DownloadSelected() => _ = DownloadSelectedAsync();

    private async Task DownloadSelectedAsync()
    {
        var selected = Chapters.Where(c => c.IsSelected && !c.IsDownloading && !c.IsDownloaded).ToList();
        if (selected.Count == 0)
        {
            ToastService.Show("请先框选需要下载的章节（已下载的除外）", ToastKind.Info);
            return;
        }
        await EnqueueChaptersAsync(selected);
        ToastService.Show($"已将 {selected.Count} 个章节加入下载队列", ToastKind.Success);
    }

    private async void DownloadAllButton_Click(object sender, RoutedEventArgs e)
    {
        var pending = Chapters.Where(c => !c.IsDownloading && !c.IsDownloaded).ToList();
        if (pending.Count == 0)
        {
            ToastService.Show("没有需要下载的章节", ToastKind.Info);
            return;
        }
        await EnqueueChaptersAsync(pending);
        ToastService.Show($"已将全部 {pending.Count} 个章节加入下载队列", ToastKind.Success);
    }

    private async Task EnqueueChaptersAsync(IEnumerable<ChapterCardViewModel> cards)
    {
        // 禁漫写专辑元数据（album.json）；其余源写通用来源元数据（source.json）
        if (_detail is not null)
        {
            try
            {
                if (_source is JmSource jmSource)
                {
                    var resp = await jmSource.GetAlbumRawAsync(_detail.Id);
                    var album = AlbumBuilder.Build(resp, _config.Current.DownloadDir);
                    if (album is not null)
                    {
                        App.Services.GetRequiredService<LocalLibraryService>()
                            .SaveMetadataForAlbum(_config.Current.DownloadDir, album);
                    }
                }
                else
                {
                    App.Services.GetRequiredService<LocalLibraryService>()
                        .SaveSourceMetadata(_config.Current.DownloadDir, _source.Info.Id, _detail);
                }
            }
            catch
            {
                // 元数据失败不影响下载
            }
        }

        foreach (var card in cards)
        {
            if (!_chapterMap.TryGetValue(card.ChapterId, out var chapter))
            {
                continue;
            }
            card.IsDownloading = true;
            await _downloadManager.SubmitChapterAsync(chapter);
        }
    }

    // ====================== 收藏（仅禁漫） ======================

    private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is null)
        {
            return;
        }
        if (_source is not JmSource)
        {
            ToastService.Show("该来源暂不支持收藏", ToastKind.Info);
            return;
        }
        if (!_session.IsLoggedIn)
        {
            ToastService.Show("请先登录后再收藏", ToastKind.Info);
            return;
        }
        try
        {
            var client = App.Services.GetRequiredService<JmHttpClient>();
            var resp = await client.ToggleFavoriteAlbumAsync(long.Parse(_detail.Id));
            _isFavorite = resp.ToggleType == ToggleType.Add;
            UpdateFavoriteButton();
            ToastService.Show(_isFavorite ? "已加入收藏" : "已取消收藏", ToastKind.Success);
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex);
        }
    }

    private void UpdateFavoriteButton()
    {
        if (_detail is null)
        {
            return;
        }
        FavoriteText.Text = _isFavorite ? "已收藏" : "收藏";
        if (_isFavorite)
        {
            FavoriteButton.Style = (Style)FindResource("PrimaryButtonStyle");
            FavoriteIcon.Stroke = Brushes.White;
            FavoriteText.Foreground = Brushes.White;
        }
        else
        {
            FavoriteButton.Style = (Style)FindResource("GhostButtonStyle");
            FavoriteIcon.Stroke = (Brush)FindResource("TextSecondaryBrush");
            FavoriteText.Foreground = (Brush)FindResource("TextPrimaryBrush");
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => Navigation.Back();

    // ====================== 视觉树工具 ======================

    private static T? FindVisualParent<T>(DependencyObject? child, Func<T, bool>? predicate = null)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match && (predicate is null || predicate(match)))
            {
                return match;
            }
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}


