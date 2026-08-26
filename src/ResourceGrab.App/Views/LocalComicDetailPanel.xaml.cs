using System.Windows;
using System.Windows.Controls;
using ResourceGrab.App.Common;
using ResourceGrab.App.Services;
using ResourceGrab.Core.Downloading;
using ResourceGrab.Core.Http;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using ResourceGrab.Core;
using ResourceGrab.Core.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Views;

/// <summary>
/// 本地漫画详情面板：展示本地漫画信息，支持手动「检查更新」与「更新下载」。
/// 更新检查为手动触发（不做后台自动检查），发现新章节后「更新下载」按钮才可用。
/// </summary>
public partial class LocalComicDetailPanel : UserControl
{
    private readonly DownloadManager _downloadManager;
    private readonly AlbumUpdateService _updateService;

    private LocalComic? _comic;
    private AlbumUpdateResult? _lastResult;
    private ComicUserDataService? _userData;
    private CancellationTokenSource? _checkCts;
    private bool _checking;
    private readonly LocalLibraryService _localLib;
    private Guid _commentsToken;
    private List<CommentVM> _allComments = new();
    private int _commentsPage = 1;
    private int _commentsPageCount = 1;

    public LocalComicDetailPanel()
    {
        InitializeComponent();
        _downloadManager = App.Services.GetRequiredService<DownloadManager>();
        _updateService = App.Services.GetRequiredService<AlbumUpdateService>();
        _userData = App.Services.GetService(typeof(ComicUserDataService)) as ComicUserDataService;
        _localLib = App.Services.GetRequiredService<LocalLibraryService>();
    }

    public void Show(LocalComic comic, bool inReader = false)
    {
        _comic = comic;
        _lastResult = null;
        _checkCts?.Cancel();
        _checkCts = null;
        _checking = false;

        BackButton.Visibility = inReader ? Visibility.Collapsed : Visibility.Visible;

        TitleText.Text = string.IsNullOrEmpty(comic.NameCn) ? comic.Name : comic.NameCn;
        TitleCnText.Text = comic.Name;
        TitleCnText.Visibility = !string.IsNullOrEmpty(comic.NameCn) && comic.NameCn != comic.Name
            ? Visibility.Visible
            : Visibility.Collapsed;

        MetaText.Text = $"{comic.ChapterCount} 章";
        if (comic.ImageCount > 0)
        {
            MetaText.Text += $" · {comic.ImageCount} 页";
        }
        MetaText.Text += $" · 更新 {comic.ModifiedAt:yyyy-MM-dd}";

        PathText.Text = comic.Path;
        ImageLoader.SetSource(CoverImage, comic.CoverPath);

        _commentsToken = Guid.NewGuid();
        CommentsList.ItemsSource = null;
        _allComments = new List<CommentVM>();
        _commentsPage = 1;
        _commentsPageCount = 1;
        UpdateCommentsPager();
        CommentsStatusText.Text = comic.AlbumId is > 0 ? "正在加载评论..." : "暂无评论";
        _ = LoadCommentsAsync(comic.Path, _commentsToken);

        DownloadUpdateButton.IsEnabled = false;
        if (comic.AlbumId is > 0)
        {
            CheckUpdateButton.IsEnabled = true;
            UpdateStatusText.Text = "点击「检查更新」查看是否有新章节";
        }
        else
        {
            CheckUpdateButton.IsEnabled = false;
            UpdateStatusText.Text = "该漫画缺少专辑 ID，无法检查更新";
        }
    }

    private void LoadUserRating(LocalComic comic)
    {
        var data = _userData?.Get(comic.Path);
        var rating = data?.Rating ?? 0;
        UpdateRatingStars(rating);
        NotesBox.Text = data?.Notes ?? "";
        SaveHintText.Text = "";
    }

    private void UpdateRatingStars(int rating)
    {
        for (var i = 1; i <= 5; i++)
        {
            var star = (Button?)FindName($"Star{i}");
            if (star is not null) star.Foreground = i <= rating ? System.Windows.Media.Brushes.Gold : System.Windows.Media.Brushes.Gray;
        }
    }

    private void Rating_Click(object sender, RoutedEventArgs e)
    {
        if (_comic is null || sender is not Button { Tag: string tag } || !int.TryParse(tag, out var stars)) return;
        _userData?.SetRating(_comic.Path, stars);
        UpdateRatingStars(stars);
        SaveHintText.Text = "已保存";
    }

    private void ClearRating_Click(object sender, RoutedEventArgs e)
    {
        if (_comic is null) return;
        _userData?.SetRating(_comic.Path, 0);
        UpdateRatingStars(0);
        SaveHintText.Text = "已清除";
    }

    private void NotesBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_comic is null) return;
        _userData?.SetNotes(_comic.Path, NotesBox.Text ?? "");
        SaveHintText.Text = "备注已保存";
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var comic = _comic;
        if (comic is null || comic.AlbumId is not > 0 || _checking)
        {
            return;
        }

        _checking = true;
        _lastResult = null;
        CheckUpdateButton.IsEnabled = false;
        DownloadUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新…";
        _checkCts?.Cancel();
        _checkCts = new CancellationTokenSource();

        try
        {
            var result = await _updateService.CheckAsync(comic.AlbumId.Value, comic.Path, _checkCts.Token);
            _lastResult = result;
            if (result.HasUpdates)
            {
                UpdateStatusText.Text =
                    $"发现 {result.NewChapters.Count} 个新章节（本地 {result.LocalChapterCount} 章 → 线上 {result.RemoteChapterCount} 章），可点击「更新下载」";
                DownloadUpdateButton.IsEnabled = true;
            }
            else
            {
                UpdateStatusText.Text = $"已是最新（本地 {result.LocalChapterCount} 章 / 线上 {result.RemoteChapterCount} 章）";
            }
        }
        catch (OperationCanceledException)
        {
            // 切换漫画时取消旧检查，不提示
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "检查更新失败，可稍后重试";
            ToastService.ShowError(ex);
        }
        finally
        {
            _checking = false;
            if (comic.AlbumId is > 0)
            {
                CheckUpdateButton.IsEnabled = true;
            }
        }
    }

    private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var result = _lastResult;
        if (result is null || result.NewChapters.Count == 0)
        {
            return;
        }

        DownloadUpdateButton.IsEnabled = false;
        try
        {
            foreach (var chapter in result.NewChapters)
            {
                await _downloadManager.SubmitChapterAsync(chapter);
            }
            UpdateStatusText.Text =
                $"已将 {result.NewChapters.Count} 个新章节加入下载队列，下载完成后点击本地页「刷新」查看";
            ToastService.Show($"已将 {result.NewChapters.Count} 个章节加入下载队列", ToastKind.Success);
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex);
        }
    }

    private async Task LoadCommentsAsync(string albumDir, Guid token)
    {
        var loaded = await Task.Run(() => _localLib.LoadLocalComments(albumDir));
        if (!Equals(_commentsToken, token)) return;
        if (loaded is null || loaded.Value.comments.Count == 0)
        {
            Dispatcher.Invoke(() => { CommentsStatusText.Text = _comic?.AlbumId is > 0 ? "No saved comments, click refresh" : "No comments"; });
            return;
        }
        var total = loaded.Value.total;
        var comments = loaded.Value.comments;
        var savedAt = loaded.Value.savedAt;
        var vms = await Task.Run(() => comments.Select(c => new CommentVM(c)).ToList());
        Dispatcher.Invoke(() => {
            if (!Equals(_commentsToken, token)) return;
            _allComments = vms;
            _commentsPage = 1;
            _commentsPageCount = Math.Max(1, (int)Math.Ceiling(vms.Count / 20.0));
            UpdateCommentsPager();
            RenderCommentsPage();
            CommentsStatusText.Text = string.Format("共 {0} 条主评，已保存 {0} / {1}，{2:yyyy-MM-dd HH:mm}", vms.Count, total, savedAt);
        });
    }

    private void RenderCommentsPage()
    {
        if (_commentsPage < 1) _commentsPage = 1;
        if (_commentsPage > _commentsPageCount) _commentsPage = _commentsPageCount;
        var pageItems = _allComments.Skip((_commentsPage - 1) * 20).Take(20).ToList();
        CommentsList.ItemsSource = pageItems;
        if (pageItems.Count > 0)
        {
            CommentsList.ScrollIntoView(pageItems[0]);
        }
        UpdateCommentsPager();
    }

    private void UpdateCommentsPager()
    {
        CommentsPagerText.Text = $"{_commentsPage} / {_commentsPageCount}";
        CommentsPrevButton.IsEnabled = _commentsPage > 1;
        CommentsNextButton.IsEnabled = _commentsPage < _commentsPageCount;
    }

    private void CommentsPrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_commentsPage <= 1) return;
        _commentsPage--;
        RenderCommentsPage();
    }

    private void CommentsNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_commentsPage >= _commentsPageCount) return;
        _commentsPage++;
        RenderCommentsPage();
    }

    private async void RefreshComments_Click(object sender, RoutedEventArgs e)
    {
        if (_comic?.AlbumId is not > 0) { ToastService.Show("No id", ToastKind.Info); return; }
        var aid = _comic.AlbumId.Value;
        var dir = _comic.Path;
        CommentsStatusText.Text = "Fetching...";
        try
        {
            var client = App.Services.GetRequiredService<JmHttpClient>();
            var count = await _localLib.FetchAndSaveAllCommentsAsync(client, aid, dir);
            var ct2 = Guid.NewGuid(); _commentsToken = ct2;
            await LoadCommentsAsync(dir, ct2);
            ToastService.Show(string.Format("Updated {0}", count), ToastKind.Success);
        }
        catch (Exception ex)
        {
            CommentsStatusText.Text = "Refresh failed";
            ToastService.ShowError(ex);
        }
    }

    private class CommentVM
    {
        public string AuthorLine { get; }
        public string Content { get; }
        public string LikesText { get; }
        public string TimeText { get; }
        public List<CommentVM> Replies { get; }
        public CommentVM(ForumCommentRespData c)
        {
            var name = !string.IsNullOrWhiteSpace(c.Nickname) ? c.Nickname : (!string.IsNullOrWhiteSpace(c.Username) ? c.Username : "anon");
            AuthorLine = name ?? "anon";
            Content = HtmlText.StripToText(c.Content);
            LikesText = c.Likes is { Length: >0 } s ? s : "0";
            TimeText = c.Addtime is { Length: >0 } tt ? FormatTime(tt) : "";
            Replies = (c.Replies ?? new()).Select(r => new CommentVM(r)).ToList();
        }
        private static string FormatTime(string raw)
        {
            if (long.TryParse(raw, out var ts)) return DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("MM-dd HH:mm");
            return raw;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
        => Navigation.CloseLocalDetail();
}
