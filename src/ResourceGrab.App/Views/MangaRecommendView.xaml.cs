using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Common;
using ResourceGrab.App.Controls;
using ResourceGrab.Core;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;

namespace ResourceGrab.App.Views;

/// <summary>
/// 漫画本地推荐页：基于本地库缓存做离线分组展示
/// （继续阅读 / 最近添加 / 高分佳作 / 作品系列 / 热门作者 / 相关推荐）。
/// 分组块使用 RecommendSection 竖向换行网格，整页只有一个竖向滚动。
/// 关系数据来自 manga-relations-cache.json（增量构建），相关推荐在线封面会落盘缓存，二次打开零网络。
/// </summary>
public partial class MangaRecommendView : UserControl
{
    private readonly ConfigService _config;
    private readonly LocalLibraryService _localLibrary;
    private readonly MangaRelationsService _relations;
    private readonly RelatedCoverCache _coverCache;
    private readonly ILogger? _logger;

    private List<LocalComic> _comics = new();
    private bool _loading;

    public MangaRecommendView()
    {
        InitializeComponent();
        _config = App.Services.GetRequiredService<ConfigService>();
        _localLibrary = App.Services.GetRequiredService<LocalLibraryService>();
        _relations = App.Services.GetRequiredService<MangaRelationsService>();
        _coverCache = App.Services.GetRequiredService<RelatedCoverCache>();
        _logger = App.Services.GetService<ILogger>();
    }

    public void OnShown() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var dirs = _config.Current.LocalDirs
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (dirs.Count == 0)
            {
                ShowEmpty("还没有配置本地目录", "去本地页添加目录后即可看到推荐");
                return;
            }

            var roots = await Task.Run(() => _localLibrary.LoadCache(AppPaths.LocalLibraryCachePath));
            var comics = dirs
                .Where(roots.ContainsKey)
                .SelectMany(dir => roots[dir])
                .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (comics.Count == 0)
            {
                ShowEmpty("本地库暂无漫画", "去本地页点「刷新」扫描目录后即可看到推荐");
                return;
            }

            _comics = comics;
            BuildSections(new MangaRelationsCache());
            ShowContent();

            // 关系缓存：只在过期/缺失时增量读 album.json，之后纯读缓存
            var relations = await Task.Run(() => _relations.Load());
            if (!MangaRelationsService.IsFresh(relations, comics))
            {
                var changed = await Task.Run(() =>
                {
                    var libraryChanged = _relations.Build(relations, comics);
                    _relations.Save(relations);
                    return libraryChanged;
                });
                if (changed)
                {
                    // 回填的 Works 写回本地库缓存，供下次秒开
                    await Task.Run(() => _localLibrary.SaveCache(AppPaths.LocalLibraryCachePath, roots));
                }
            }

            BuildSections(relations);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[MangaRecommend] 加载失败: {ex.Message}");
            ShowEmpty("加载推荐失败", ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private void BuildSections(MangaRelationsCache relations)
    {
        SectionsHost.Children.Clear();

        AddFlatSection("继续阅读", _comics
            .Where(c => c.ReadProgress > 0 && c.ReadProgress < 100)
            .OrderByDescending(c => ParseDate(c.LastReadAt))
            .ToList());

        AddFlatSection("最近添加", _comics
            .OrderByDescending(c => c.ModifiedAt)
            .ToList());

        var rated = _comics.Where(c => c.Rating > 0)
            .OrderByDescending(c => c.Rating)
            .ThenByDescending(c => ParseCount(c.Likes) + ParseCount(c.TotalViews))
            .ToList();
        if (rated.Count == 0)
        {
            rated = _comics.Where(c => ParseCount(c.Likes) + ParseCount(c.TotalViews) > 0)
                .OrderByDescending(c => ParseCount(c.Likes) + ParseCount(c.TotalViews))
                .ToList();
        }
        AddFlatSection("高分佳作", rated);

        var workGroups = RecommendationRelationResolver.BuildWorkGroups(_comics);
        if (workGroups.Count > 0)
        {
            SectionsHost.Children.Add(MakeGroupHeader("作品系列"));
            foreach (var group in workGroups) AddGroupSection(group);
        }

        var authorGroups = RecommendationRelationResolver.BuildAuthorGroups(_comics);
        if (authorGroups.Count > 0)
        {
            SectionsHost.Children.Add(MakeGroupHeader("热门作者"));
            foreach (var group in authorGroups) AddGroupSection(group);
        }

        var recommendations = RecommendationRelationResolver.BuildRelated(_comics, relations, 20);
        if (recommendations.Count > 0)
        {
            var rows = new List<(RelatedRecommendation Rec, string? Cover)>(recommendations.Count);
            foreach (var rec in recommendations)
            {
                rows.Add((rec, ResolveRelatedCover(rec)));
            }
            var section = new RecommendSection("相关推荐");
            section.SetItems(rows, MakeRelatedCard, 12, rows.Count);
            SectionsHost.Children.Add(section);
        }
    }

    private void AddFlatSection(string title, IReadOnlyList<LocalComic> items)
    {
        if (items.Count == 0) return;
        var section = new RecommendSection(title);
        section.SetItems(items, MakeComicCard, 12, 60);
        SectionsHost.Children.Add(section);
    }

    private void AddGroupSection(ComicGroup group)
    {
        var section = new RecommendSection(group.Title, sub: true);
        section.SetItems(group.Members, MakeComicCard, group.Members.Count, group.Members.Count);
        SectionsHost.Children.Add(section);
    }

    private FrameworkElement MakeComicCard(LocalComic comic)
    {
        var card = new MangaPosterCard();
        card.Bind(comic);
        return card;
    }

    private FrameworkElement MakeRelatedCard((RelatedRecommendation Rec, string? Cover) data)
    {
        var card = new MangaPosterCard();
        card.BindRelated(data.Rec.Entry, data.Rec.LocalMatch, data.Cover);
        return card;
    }

    /// <summary>相关推荐封面：本地命中用本地封面；否则用已落盘的在线封面，未缓存则后台下载一次供下次使用。</summary>
    private string? ResolveRelatedCover(RelatedRecommendation rec)
    {
        if (rec.LocalMatch is not null) return null;
        var cached = _coverCache.TryGet(rec.Entry.Id);
        if (cached is not null) return cached;

        if (rec.Entry.Image.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var (id, url) = (rec.Entry.Id, rec.Entry.Image);
            _ = Task.Run(async () =>
            {
                try { await _coverCache.EnsureAsync(id, url).ConfigureAwait(false); }
                catch { }
            });
        }
        return null;
    }

    private void ShowContent() => EmptyState.Visibility = Visibility.Collapsed;

    private void ShowEmpty(string title, string subtitle)
    {
        SectionsHost.Children.Clear();
        EmptyTitle.Text = title;
        EmptySubtitle.Text = subtitle;
        EmptyState.Visibility = Visibility.Visible;
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

    private static DateTime ParseDate(string? raw)
        => DateTime.TryParse(raw, out var value) ? value : DateTime.MinValue;

    private static long ParseCount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var value) ? value : 0;
    }
}
