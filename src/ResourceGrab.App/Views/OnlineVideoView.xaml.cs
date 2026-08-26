using System.Windows;
using System.Windows.Controls;
using System.Net.Http;
using System.IO;
using System.Windows.Media;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Sources.VideoSources;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Services;
using ResourceGrab.Core.Logging;

namespace ResourceGrab.App.Views;

public partial class OnlineVideoView : UserControl
{
    private string _searchText = "";
    private int _page = 1;
    private CancellationTokenSource? _searchCts;
    private int _searchVersion;
    private readonly ILogger _logger = App.Services.GetRequiredService<ILogger>();

    private IVideoSource? Source => App.Services.GetServices<IVideoSource>()
        .FirstOrDefault(s => s.Info.Id == "missav");

    public OnlineVideoView()
    {
        InitializeComponent();
    }

    public void OnShown()
    {
    }

    /// <summary>由侧边栏搜索框触发的搜索。</summary>
    public void ExecuteSearchWithKeyword(string keyword)
    {
        _searchText = keyword;
        ExecuteSearch();
    }

    /// <summary>由侧边栏源选择器触发的源切换。</summary>
    public void SwitchSource(string sourceId)
    {
        _logger.Info($"[OnlineVideo] 切换源: {sourceId}");
        // 后续接入多源时根据 sourceId 选择对应的 IVideoSource。
    }

    private async void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_page > 1) { _page--; ExecuteSearch(); }
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        _page++;
        ExecuteSearch();
    }

    private async void ExecuteSearch()
    {
        var keyword = _searchText;
        if (string.IsNullOrWhiteSpace(keyword))
        {
            ToastService.Show("请输入搜索关键词", ToastKind.Info);
            return;
        }

        _logger.Info($"[OnlineVideo] ExecuteSearch 开始: keyword=\"{keyword}\" page={_page}");

        var source = Source;
        if (source is null)
        {
            _logger.Warn("[OnlineVideo] IVideoSource 未注册（missav 不在 DI 容器中）");
            ShowEmpty("在线源未注册", "请检查 DI 配置");
            return;
        }

        _logger.Info($"[OnlineVideo] 使用源: {source.Info.Id}");

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        var version = ++_searchVersion;

        ShowLoading($"正在搜索 {source.Info.DisplayName}…");
        try
        {
            var result = await source.SearchAsync(keyword, _page, ct);
            if (version != _searchVersion || ct.IsCancellationRequested) return;

            RenderResults(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _searchVersion)
            {
                _logger.Error("[OnlineVideo] 搜索异常", ex);
                ShowEmpty("搜索失败", ex.Message);
            }
        }
    }

    private void RenderResults(OnlineVideoSearchResult result)
    {
        ResultCardsPanel.Children.Clear();
        foreach (var item in result.Items.Take(120))
        {
            var card = BuildCard(item);
            ResultCardsPanel.Children.Add(card);
        }
        ShowResults(result.Items.Count);
    }

    private FrameworkElement BuildCard(OnlineVideoSummary item)
    {
        // 简易卡片：封面 + 标题。后续替换为统一的 OnlineVideoCard 控件。
        var border = new Border
        {
            Width = 180,
            Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("HoverBgBrush"),
            BorderBrush = (Brush)FindResource("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        var stack = new StackPanel();
        if (!string.IsNullOrEmpty(item.CoverUrl))
        {
            var img = new Image { Height = 100, Stretch = Stretch.UniformToFill, Margin = new Thickness(6, 6, 6, 4) };
            img.Loaded += async (_, _) =>
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var bytes = await client.GetByteArrayAsync(item.CoverUrl);
                    img.Source = System.Windows.Media.Imaging.BitmapFrame.Create(
                        new MemoryStream(bytes), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                }
                catch { /* 缩略图加载失败静默 */ }
            };
            stack.Children.Add(img);
        }
        stack.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 36,
            Margin = new Thickness(8, 2, 8, 8),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
        });
        border.Child = stack;
        return border;
    }

    private void ShowEmpty(string title, string hint)
    {
        ResultsScroll.Visibility = Visibility.Collapsed;
        LoadingState.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        EmptyHintText.Text = title + "\n" + hint;
    }

    public void ShowLoading(string message)
    {
        ResultsScroll.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        LoadingState.Visibility = Visibility.Visible;
        LoadingText.Text = message;
    }

    public void ShowResults(int count)
    {
        LoadingState.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        ResultsScroll.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count == 0) ShowEmpty("没有找到匹配的视频", "尝试更换关键词或切换数据源");
    }
}
