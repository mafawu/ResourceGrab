using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Controls;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Services.VideoScrape;

namespace ResourceGrab.App.Views;

/// <summary>演员详情页：本地作品网格 + 在线资料（来自 VideoActorMerger，源未实现时优雅降级）。</summary>
public partial class ActorProfileView : UserControl
{
    private readonly VideoLibraryService _library;
    private readonly VideoActorMerger? _merger;
    private CancellationTokenSource? _cts;
    private static readonly HttpClient _http = new();

    public Action? CloseRequested;
    public Action<VideoItem>? OnLocalWorkSelected;

    public ActorProfileView()
    {
        InitializeComponent();
        _library = App.Services.GetRequiredService<VideoLibraryService>();
        try { _merger = App.Services.GetService<VideoActorMerger>(); } catch { }
        BackButton.Click += (_, _) => CloseRequested?.Invoke();
    }

    public void LoadAsync(string actor)
    {
        NameText.Text = actor;
        AliasWrap.Children.Clear();
        InfoText.Text = "";
        SourceWrap.Children.Clear();
        Avatar.Visibility = Visibility.Collapsed;
        OnlineText.Text = "加载中…";
        OnlineText.Visibility = Visibility.Visible;

        var items = _library.Query(new VideoQueryOptions
        {
            ActorFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { actor },
            SortBy = VideoSortBy.AddedDesc,
        }).ToList();

        // 全量绑定，虚拟化网格只实例化可视卡片（滚动位置复位）
        WorksGrid.ItemsSource = items;
        WorksGrid.ScrollToTop();
        WorksEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _ = LoadOnlineAsync(actor);
    }

    private void ActorWorkCard_Loaded(object sender, RoutedEventArgs e)
    {
        // 容器回收会复用卡片实例，Loaded 反复触发：先解绑再绑，避免重复订阅
        if (sender is not VideoFileCard card) return;
        card.DetailRequested -= OnCardDetail;
        card.DetailRequested += OnCardDetail;
    }

    private void OnCardDetail(VideoItem item) => OnLocalWorkSelected?.Invoke(item);

    private async Task LoadOnlineAsync(string actor)
    {
        if (_merger is null)
        {
            OnlineText.Text = "未配置在线演员源";
            return;
        }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var meta = await _merger.MergeAsync(actor, ct);
            if (ct.IsCancellationRequested) return;
            if (meta is null)
            {
                OnlineText.Text = "暂无在线资料";
                return;
            }

            if (meta.ImageUrls.Count > 0)
            {
                await LoadAvatarAsync(meta.ImageUrls[0], ct);
            }
            foreach (var alias in meta.Aliases.Take(12))
            {
                AliasWrap.Children.Add(new Border
                {
                    Style = (Style)FindResource("VideoChipStyle"),
                    Margin = new Thickness(0, 0, 6, 6),
                    Child = new TextBlock { Text = alias, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center },
                });
            }
            var info = new List<string>();
            if (meta.Birthday is not null) info.Add($"生日 {meta.Birthday}");
            if (meta.Birthplace is not null) info.Add($"出生地 {meta.Birthplace}");
            if (meta.Height is not null) info.Add($"{meta.Height}cm");
            InfoText.Text = string.Join(" · ", info);

            SourceWrap.Children.Clear();
            foreach (var (key, url) in meta.SourceUrls)
            {
                SourceWrap.Children.Add(MakeLink(key, url));
            }
            OnlineText.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            if (!ct.IsCancellationRequested) OnlineText.Text = "在线资料获取失败";
        }
    }

    private async Task LoadAvatarAsync(string source, CancellationToken ct)
    {
        try
        {
            // GFriends 返回本地落盘路径，在线源返回 URL，两者都支持
            var bytes = !source.StartsWith("http", StringComparison.OrdinalIgnoreCase) && File.Exists(source)
                ? await File.ReadAllBytesAsync(source, ct)
                : await _http.GetByteArrayAsync(source, ct);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            Dispatcher.Invoke(() =>
            {
                Avatar.Source = bmp;
                Avatar.Visibility = Visibility.Visible;
            });
        }
        catch
        {
            // 头像加载失败不影响其余信息展示
        }
    }

    private static Border MakeLink(string key, string url)
    {
        var text = new TextBlock
        {
            Text = key,
            FontSize = 12,
            TextDecorations = System.Windows.TextDecorations.Underline,
            Foreground = (Brush)Application.Current.FindResource("PrimaryBrush"),
            Cursor = Cursors.Hand,
        };
        text.MouseLeftButtonUp += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        };
        return new Border
        {
            Style = (Style)Application.Current.FindResource("VideoChipStyle"),
            Margin = new Thickness(0, 0, 6, 6),
            Child = text,
        };
    }
}
