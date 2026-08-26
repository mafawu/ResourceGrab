using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceGrab.App.Common;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Views;

public partial class NovelLocalView : CardGridViewBase
{
    private const int PageSize = 40;
    private readonly ResourceGrab.Core.Services.NovelIndexService _novelService;
    private List<ResourceGrab.Core.Models.NovelResource> _all = new();
    private List<ResourceGrab.Core.Models.NovelResource> _filtered = new();
    private int _page = 1;
    private int _pageCount = 1;
    private string _sortTag = "NameAsc";
    private HashSet<string> _includedTags = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _excludedTags = new(StringComparer.OrdinalIgnoreCase);
    private string _keyword = "";
    private NovelSearchPanel? _searchPanel;
    private string _indexPath = @"E:\备份\小说\小说标签索引.json";
    private bool _loaded;

    protected override void UpdateCellSize()
    {
        if (ActualWidth <= 0) return;
        double available = 0;
        if (NovelGrid != null && NovelGrid.ActualWidth > 40)
            available = NovelGrid.ActualWidth;
        else if (ActualWidth > 40)
            available = ActualWidth - 40;
        else if (ActualWidth > 0)
            available = ActualWidth;
        else return;



        var (cardWidth, slotWidth) = ResourceGrab.App.Controls.GridCellSizer.ComputeByColumns(Columns, available);
        CardWidth = cardWidth;
        SlotWidth = slotWidth;
    }

    public NovelLocalView()
    {
        InitializeComponent();
        _novelService = App.Services.GetService<ResourceGrab.Core.Services.NovelIndexService>() ?? new ResourceGrab.Core.Services.NovelIndexService();
        Loaded += NovelLocalView_Loaded;
        SizeChanged += (_, _) => UpdateCellSize();
        Loaded += (_, _) => { if (NovelGrid != null) NovelGrid.SizeChanged += (_, _) => UpdateCellSize(); UpdateCellSize(); };
    }

    private async void NovelLocalView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await LoadIndexAsync(_indexPath);
    }

    public void SetSearchPanel(NovelSearchPanel panel)
    {
        _searchPanel = panel;
        _searchPanel.FilterChanged += ApplyFilter;
        if (_novelService.Index != null)
            _searchPanel.SetData(_novelService.GetStructureTree(), _novelService.TagCounts);
    }

    private void ApplyFilter(string keyword, IReadOnlyCollection<string> inc, IReadOnlyCollection<string> exc)
    {
        _keyword = keyword ?? "";
        _includedTags = new HashSet<string>(inc ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _excludedTags = new HashSet<string>(exc ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        RebuildFilter();
    }

    private async Task LoadIndexAsync(string path)
    {
        try
        {
            LoadingPanel.Visibility = Visibility.Visible;
            NovelGrid.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Collapsed;
            PagingPanel.Visibility = Visibility.Collapsed;
            LoadingDetail.Text = path;

            ResourceGrab.Core.Models.NovelIndexFile? idx = null;
            string? err = null;
            try
            {
                idx = await _novelService.LoadAsync(path);
                err = _novelService.LastError;
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }

            if (idx == null)
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                EmptyPanel.Visibility = Visibility.Visible;
                EmptyDetail.Text = string.IsNullOrWhiteSpace(err) ? $"未找到索引: {path}" : err;
                RootPathText.Text = "";
                NovelCount.Text = "0 部";
                return;
            }
            _all = _novelService.Resources.ToList();
            var effRoot = _novelService.EffectiveRoot ?? _novelService.Root ?? "";
            RootPathText.Text = effRoot;
            RootPathText.ToolTip = effRoot;
            try { _searchPanel?.SetData(_novelService.GetStructureTree(), _novelService.TagCounts); } catch { }
            RebuildFilter();
        }
        catch (Exception ex)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
            EmptyDetail.Text = $"加载失败: {ex.Message}";
        }
    }

    private void RebuildFilter()
    {
        try
        {
            var query = _all.Where(Matches);
            _filtered = _sortTag switch
            {
                "SizeDesc" => query.OrderByDescending(r => r.Size).ToList(),
                "SizeAsc" => query.OrderBy(r => r.Size).ToList(),
                _ => query.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList()
            };
            _page = 1;
            _pageCount = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)PageSize));
            NovelCount.Text = _filtered.Count == _all.Count ? $"共 {_all.Count} 部" : $"匹配 {_filtered.Count} / {_all.Count} 部";
            if (_filtered.Count == 0)
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                NovelGrid.Visibility = Visibility.Collapsed;
                EmptyPanel.Visibility = Visibility.Visible;
                EmptyDetail.Text = "没有匹配的小说，换个标签试试";
                PagingPanel.Visibility = Visibility.Collapsed;
                NovelGrid.ItemsSource = null;
                return;
            }
            LoadingPanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Collapsed;
            NovelGrid.Visibility = Visibility.Visible;
            PagingPanel.Visibility = Visibility.Visible;
            RenderPage();
        }
        catch (Exception ex)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
            EmptyDetail.Text = $"筛选失败: {ex.Message}";
        }
    }

    private bool Matches(ResourceGrab.Core.Models.NovelResource r)
    {
        if (_includedTags.Count > 0 && !_includedTags.All(t => r.Tags.Contains(t, StringComparer.OrdinalIgnoreCase) || r.PrimaryTag.Equals(t, StringComparison.OrdinalIgnoreCase))) return false;
        if (_excludedTags.Count > 0 && _excludedTags.Any(t => r.Tags.Contains(t, StringComparer.OrdinalIgnoreCase) || r.PrimaryTag.Equals(t, StringComparison.OrdinalIgnoreCase))) return false;
        if (string.IsNullOrWhiteSpace(_keyword)) return true;
        return r.DisplayName.Contains(_keyword, StringComparison.OrdinalIgnoreCase)
            || r.Tags.Any(t => t.Contains(_keyword, StringComparison.OrdinalIgnoreCase))
            || r.PrimaryTag.Contains(_keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void RenderPage()
    {
        try
        {
            ResourceGrab.Core.Services.NovelReadingHistoryService? hist = null;
            try { hist = App.Services.GetService<ResourceGrab.Core.Services.NovelReadingHistoryService>(); } catch { }
            var start = (_page - 1) * PageSize;
            var pageItems = _filtered.Skip(start).Take(PageSize).Select(r =>
            {
                var prog = hist?.Get(r.FullPath);
                double pct = prog?.Progress ?? 0;
                double barWidth = 0;
                // 估算卡片内容宽度：CardWidth - 20 (padding) - will be approx 160; 用 120 作为进度条底宽
                // 实际用固定 110 减去边距，进度按比例
                barWidth = pct > 0 ? System.Math.Round(110 * pct) : 0;
                return new NovelCardViewModel
                {
                    DisplayName = r.DisplayName,
                    PrimaryTag = r.PrimaryTag,
                    DisplayTags = r.Tags.Take(4).ToList(),
                    SizeText = FormatSize(r.Size),
                    FullPath = r.FullPath,
                    Initial = string.IsNullOrWhiteSpace(r.DisplayName) ? "?" : r.DisplayName.Substring(0,1).ToUpperInvariant(),
                    ProgressText = prog != null && prog.PageCount > 1 ? $"{prog.Page}/{prog.PageCount} {(int)(pct*100)}%" : "",
                    ProgressVisibility = prog != null && prog.PageCount > 1 && prog.Page > 1 ? Visibility.Visible : Visibility.Collapsed,
                    ProgressWidth = barWidth
                };
            }).ToList();
            NovelGrid.ItemsSource = pageItems;
            PageInfo.Text = $"{_page} / {_pageCount}";
            PrevPageBtn.IsEnabled = _page > 1;
            NextPageBtn.IsEnabled = _page < _pageCount;
            NovelGrid.ScrollToTop();
        }
        catch { }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024*1024) return $"{bytes/1024.0:F1} KB";
        return $"{bytes/1024.0/1024.0:F1} MB";
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedItem is ComboBoxItem ci && ci.Tag is string tag) { _sortTag = tag; if (_all.Count>0) RebuildFilter(); }
    }
    private void PrevPage_Click(object sender, RoutedEventArgs e){ if(_page>1){ _page--; RenderPage(); } }
    private void NextPage_Click(object sender, RoutedEventArgs e){ if(_page<_pageCount){ _page++; RenderPage(); } }
    private void ReloadButton_Click(object sender, RoutedEventArgs e){ _loaded=false; _ = LoadIndexAsync(_indexPath); }
    public void SetSort(string tag){ if(string.IsNullOrWhiteSpace(tag)) return; _sortTag = tag; if (_all.Count>0) RebuildFilter(); }
    public void ReloadIndex(){ _loaded=false; _ = LoadIndexAsync(_indexPath); }
    public string CurrentSort => _sortTag;
    private void BackButton_Click(object sender, RoutedEventArgs e) => Navigation.BackHandler?.Invoke();
    public void RefreshHistory(){ try { if(_filtered.Count>0) RenderPage(); } catch{} }
    private void PickFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog{ Filter="JSON|*.json|All|*.*", InitialDirectory=@"E:\备份\小说" };
        if (dlg.ShowDialog()==true) { _indexPath = dlg.FileName; _loaded=false; _ = LoadIndexAsync(_indexPath); }
    }
    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is NovelCardViewModel vm)
        {
            if (!string.IsNullOrWhiteSpace(vm.FullPath) && File.Exists(vm.FullPath))
                Navigation.OpenNovelReaderHandler?.Invoke(vm.FullPath);
            else
                ToastService.Show($"文件不存在: {vm.FullPath}", ToastKind.Info);
        }
    }
}

public class NovelCardViewModel
{
    public string DisplayName { get; set; } = "";
    public string PrimaryTag { get; set; } = "";
    public List<string> DisplayTags { get; set; } = new();
    public string SizeText { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Initial { get; set; } = "?";
    public string ProgressText { get; set; } = "";
    public Visibility ProgressVisibility { get; set; } = Visibility.Collapsed;
    public double ProgressWidth { get; set; }
}
