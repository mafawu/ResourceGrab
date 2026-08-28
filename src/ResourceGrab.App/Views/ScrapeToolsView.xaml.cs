using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ResourceGrab.App.Common;
using ResourceGrab.App.Dialogs;
using ResourceGrab.App.Services;
using ResourceGrab.App.Themes;
using ResourceGrab.Core;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Views;

/// <summary>
/// 刮削工具页：参考 MDCX 的「工具集合」设计（tool.tsx），用分组卡片组织刮削相关操作。
/// 卡片分组：刮削工具（批量扫描 + 单本刮削）、本地库工具、来源设置工具、演员工具。
/// 每个操作触发真实后端任务，并在忙碌时禁用按钮、显示旋转图标与进度条。
/// </summary>
public partial class ScrapeToolsView : UserControl
{
    private readonly SourceManager _sourceManager;
    private readonly ConfigService _config;
    private readonly LocalLibraryService _localLibrary;
    private readonly SessionService _session;

    public ScrapeToolsView()
    {
        InitializeComponent();
        _sourceManager = App.Services.GetRequiredService<SourceManager>();
        _config = App.Services.GetRequiredService<ConfigService>();
        _localLibrary = App.Services.GetRequiredService<LocalLibraryService>();
        _session = App.Services.GetRequiredService<SessionService>();
    }

    /// <summary>每次进入页面时刷新来源列表（来源可能被切换）。</summary>
    public void OnShown()
    {
        PopulateSources();
    }

    // ============ 来源下拉框 ============

    private void PopulateSources()
    {
        var mangaSources = _sourceManager.Sources
            .Where(s => s.Info.Kind == ResourceKind.Manga)
            .ToList();
        FillSourceBox(SingleSourceBox, mangaSources);
        FillSourceBox(SettingsSourceBox, mangaSources);
        SelectSource(SingleSourceBox, _sourceManager.Current);
        SelectSource(SettingsSourceBox, _sourceManager.Current);
    }

    private static void FillSourceBox(ComboBox box, IReadOnlyList<IComicSource> sources)
    {
        box.Items.Clear();
        foreach (var src in sources)
        {
            box.Items.Add(new ComboBoxItem { Content = src.Info.DisplayName, Tag = src });
        }
    }

    private static void SelectSource(ComboBox box, IComicSource current)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (ReferenceEquals(item.Tag, current))
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static IComicSource? SelectedSource(ComboBox box)
        => box.SelectedItem is ComboBoxItem { Tag: IComicSource src } ? src : null;

    // ============ 卡片 1：扫描本地库 / 单本刮削 ============

    private async void StartScrape_Click(object sender, RoutedEventArgs e)
        => await RunLibraryScanAsync(StartScrapeButton, StartScrapeLabel, StartScrapeIcon, "正在扫描...", "开始扫描");

    /// <summary>扫描已配置的本地目录、重建索引缓存，并按需补齐漫画名称。</summary>
    private async Task RunLibraryScanAsync(Button btn, TextBlock label, System.Windows.Shapes.Path icon, string busyText, string doneText)
    {
        var dirs = _config.Current.LocalDirs
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (dirs.Count == 0)
        {
            ToastService.Show("请先在「管理本地目录」中添加扫描目录", ToastKind.Info);
            return;
        }

        var backfill = BackfillNamesBox.IsChecked == true;
        SetBusy(btn, label, icon, busyText, true);
        ScrapeProgress.Visibility = Visibility.Visible;
        try
        {
            var roots = new Dictionary<string, List<LocalComic>>(StringComparer.OrdinalIgnoreCase);
            var total = 0;
            await Task.Run(() =>
            {
                foreach (var dir in dirs)
                {
                    var comics = _localLibrary.Scan(dir, countImages: true);
                    if (backfill)
                    {
                        _localLibrary.BackfillExtractedNames(comics);
                    }
                    roots[dir] = comics;
                    total += comics.Count;
                }
            });
            _localLibrary.SaveCache(AppPaths.LocalLibraryCachePath, roots);
            ToastService.Show($"已扫描 {total} 部漫画，索引已更新", ToastKind.Success);
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex, "扫描本地库失败：");
        }
        finally
        {
            ScrapeProgress.Visibility = Visibility.Collapsed;
            SetBusy(btn, label, icon, doneText, false);
        }
    }

    private async void SingleScrape_Click(object sender, RoutedEventArgs e)
        => await SingleScrapeAsync();

    private void SingleUrlBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            _ = SingleScrapeAsync();
        }
    }

    /// <summary>按链接 / ID 抓取单本漫画详情并打开章节详情页。</summary>
    private async Task SingleScrapeAsync()
    {
        var source = SelectedSource(SingleSourceBox) ?? _sourceManager.Current;
        var input = SingleUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(input))
        {
            ToastService.Show("请输入漫画链接或 ID", ToastKind.Info);
            return;
        }
        var id = ExtractComicId(input);
        if (string.IsNullOrEmpty(id))
        {
            ToastService.Show("无法从输入中识别漫画 ID", ToastKind.Info);
            return;
        }

        SetBusy(SingleScrapeButton, SingleScrapeLabel, SingleScrapeIcon, "正在刮削...", true);
        try
        {
            var detail = await source.GetComicAsync(id);
            ToastService.Show($"已刮削：《{detail.Title}》", ToastKind.Success);
            Navigation.OpenComic(source.Info.Id, detail.Id);
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex, "刮削失败：");
        }
        finally
        {
            SetBusy(SingleScrapeButton, SingleScrapeLabel, SingleScrapeIcon, "刮削", false);
        }
    }

    /// <summary>从链接或原始 ID 中提取来源编号：取最后一个路径段，去掉查询与片段。</summary>
    private static string ExtractComicId(string input)
    {
        var s = input.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }
        var hash = s.IndexOf('#');
        if (hash >= 0) s = s.Substring(0, hash);
        var query = s.IndexOf('?');
        if (query >= 0) s = s.Substring(0, query);

        var lastSlash = s.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < s.Length - 1)
        {
            s = s.Substring(lastSlash + 1);
        }
        return s.Trim();
    }

    // ============ 卡片 2：本地目录管理 ============

    private void ManageDirs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LocalDirsDialog { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    // ============ 卡片 3：来源设置 ============

    private void CheckCookie_Click(object sender, RoutedEventArgs e)
    {
        var source = SelectedSource(SettingsSourceBox) ?? _sourceManager.Current;
        if (!source.Info.SupportsFavorites)
        {
            ToastService.Show($"来源「{source.Info.DisplayName}」无需登录", ToastKind.Info);
            return;
        }
        if (_session.IsLoggedIn)
        {
            ToastService.Show($"Cookie 有效：当前已登录为 {_session.Username}", ToastKind.Success);
            return;
        }
        var dialog = new LoginDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            ToastService.Show($"登录成功：{_session.Username}", ToastKind.Success);
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    // ============ 卡片 4：演员工具 ============

    private void Actors_Click(object sender, RoutedEventArgs e)
        => (App.Current.MainWindow as MainWindow)?.OpenVideoActorsTool();

    // ============ 忙碌态辅助 ============

    private static void SetBusy(Button btn, TextBlock label, System.Windows.Shapes.Path icon, string text, bool busy)
    {
        btn.IsEnabled = !busy;
        label.Text = text;
        icon.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            StartSpin(icon);
        }
        else
        {
            StopSpin(icon);
        }
    }

    private static void StartSpin(FrameworkElement element)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new RotateTransform(0);
        ((RotateTransform)element.RenderTransform).BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private static void StopSpin(FrameworkElement element)
    {
        element.RenderTransform?.BeginAnimation(RotateTransform.AngleProperty, null);
        element.RenderTransform = null;
    }
}
