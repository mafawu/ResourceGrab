using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ResourceGrab.App.Common;
using ResourceGrab.App.Services;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Views;

/// <summary>
/// 阅读历史：本地书与在线书共用一份记录（<see cref="NovelReadingHistoryService"/>），
/// 靠 key 前缀区分来源——在线是 online:{源}:{书}:{章}，本地是文件路径。
/// </summary>
public partial class NovelHistoryView : UserControl
{
    private readonly NovelReadingHistoryService _history;
    private readonly NovelSourceManager _sources;

    public ObservableCollection<HistoryRow> Rows { get; } = new();

    public NovelHistoryView()
    {
        InitializeComponent();
        _history = App.Services.GetRequiredService<NovelReadingHistoryService>();
        _sources = App.Services.GetRequiredService<NovelSourceManager>();
        HistoryList.ItemsSource = Rows;
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        Rows.Clear();

        // 在线记录按"章"存（同一本书读完 100 章就是 100 条），展示时按书聚合成一条，
        // 取最近读的那章；否则历史页会被一本书刷屏。
        var onlineLatest = new Dictionary<string, NovelReadingProgress>(StringComparer.OrdinalIgnoreCase);
        var locals = new List<NovelReadingProgress>();

        foreach (var item in _history.GetRecent(500))
        {
            var online = OnlineKey.TryParse(item.Path);
            if (online is null)
            {
                locals.Add(item);
                continue;
            }

            var bookKey = online.SourceId + ":" + online.NovelId;
            if (!onlineLatest.TryGetValue(bookKey, out var existing) || existing.UpdatedAt < item.UpdatedAt)
                onlineLatest[bookKey] = item;
        }

        var merged = new List<(NovelReadingProgress Item, OnlineKey? Online)>();
        foreach (var item in locals) merged.Add((item, null));
        foreach (var item in onlineLatest.Values) merged.Add((item, OnlineKey.TryParse(item.Path)));

        foreach (var (item, online) in merged.OrderByDescending(m => m.Item.UpdatedAt))
        {
            var chapterLabel = ChapterLabel(item.Title);
            var title = online is null
                ? (string.IsNullOrWhiteSpace(item.Title) ? Path.GetFileNameWithoutExtension(item.Path) : item.Title)
                : StripChapterSuffix(item.Title, online.NovelId);

            Rows.Add(new HistoryRow
            {
                Title = title,
                KindText = online is null ? "本地" : "在线",
                SubText = online is null
                    ? item.Path
                    : (chapterLabel.Length > 0 ? chapterLabel : online.SourceId + " · " + online.ChapterId),
                ProgressText = $"{item.Page} / {item.PageCount} 页 · {(int)(item.Page * 100.0 / Math.Max(1, item.PageCount))}%",
                TimeText = item.UpdatedAt.ToString("MM-dd HH:mm"),
                Progress = item,
                Online = online,
            });
        }

        CountText.Text = Rows.Count == 0 ? "" : $"{Rows.Count} 条";
        HistoryList.Visibility = Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = Rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>在线记录的标题形如「山海旧事 · 第 9 / 128 章」，把章号单独拎出来展示。</summary>
    private static string ChapterLabel(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var m = System.Text.RegularExpressions.Regex.Match(title, @"第\s*(\d+)\s*/\s*(\d+)\s*章");
        return m.Success ? $"读到第 {m.Groups[1].Value} / {m.Groups[2].Value} 章" : "";
    }

    private static string StripChapterSuffix(string? title, string fallbackId)
    {
        if (string.IsNullOrWhiteSpace(title)) return "在线书籍 " + fallbackId;
        var idx = title.LastIndexOf(" · ", StringComparison.Ordinal);
        return idx > 0 ? title[..idx] : title;
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;
        HistoryList.SelectedItem = null;

        try
        {
            if (row.Online is { } online)
            {
                Navigation.OpenNovelBookAt(online.SourceId, online.NovelId, online.ChapterId, online.Title);
                return;
            }

            if (File.Exists(row.Progress.Path))
                Navigation.OpenNovelReader(row.Progress.Path);
            else
                ToastService.Show("文件不在了：" + row.Progress.Path, ToastKind.Error);
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var item in _history.GetRecent(500))
                _history.Remove(item.Path);
            Refresh();
            ToastService.Show("已清空阅读历史", ToastKind.Info);
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex);
        }
    }

    /// <summary>online:{sourceId}:{novelId}:{chapterBookId}/{chapterKey}</summary>
    public sealed record OnlineKey(string SourceId, string NovelId, string ChapterId, string Title)
    {
        public static OnlineKey? TryParse(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (!path.StartsWith("online:", StringComparison.OrdinalIgnoreCase)) return null;
            var parts = path.Split(':', 4);
            if (parts.Length < 4) return null;
            var sourceId = parts[1];
            var novelId = parts[2];
            // 早期版本把键里的 / 换成了 \，这里两种都认
            var chapterId = parts[3].Replace('\\', '/');
            return new OnlineKey(sourceId, novelId, chapterId, "");
        }
    }

    public sealed class HistoryRow
    {
        public string Title { get; init; } = "";
        public string SubText { get; init; } = "";
        public string KindText { get; init; } = "";
        public string ProgressText { get; init; } = "";
        public string TimeText { get; init; } = "";
        public NovelReadingProgress Progress { get; init; } = new();
        public OnlineKey? Online { get; init; }
    }
}
