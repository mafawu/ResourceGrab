using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Common;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Services;
using ResourceGrab.App.ViewModels;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Services.ShortDrama;

namespace ResourceGrab.App.Views;

/// <summary>
/// 短剧本地库：**只显示已下载的剧集**（离线可播）。
/// 进行中的下载任务统一在「下载」页查看，避免两处重复展示同一批队列任务。
/// </summary>
public partial class ShortDramaLocalView : CardGridViewBase
{
    private readonly ShortDramaLibraryService? _library;
    private readonly ILogger? _logger;

    private readonly List<ShortDramaCardAdapter> _cards = [];
    private IReadOnlyList<LocalDrama> _loaded = [];

    /// <summary>请求打开某部本地剧（MainWindow 跳本地详情）。</summary>
    public event EventHandler<LocalDrama>? OpenRequested;

    public ShortDramaLocalView()
    {
        InitializeComponent();
        _library = App.Services?.GetService<ShortDramaLibraryService>();
        _logger = App.Services?.GetService<ILogger>();

        Loaded += (_, _) => Refresh();
    }

    /// <summary>页面被切回来时调用（NavRail 路由工厂里调）。</summary>
    public void OnShown() => Refresh();

    /// <summary>重新扫描本地库。</summary>
    public void Refresh()
    {
        if (_library is null)
        {
            StateHost.State = StateKind.Error;
            StateHost.ErrorDescription = "本地库服务未注册";
            return;
        }

        try
        {
            _loaded = _library.Load();
            _cards.Clear();
            foreach (var drama in _loaded)
                _cards.Add(new ShortDramaCardAdapter(drama, Open));

            StateHost.Result = _cards;
            StateHost.State = _cards.Count > 0 ? StateKind.Result : StateKind.Empty;

            var episodes = _loaded.Sum(d => d.DownloadedCount);
            var bytes = _loaded.Sum(d => d.TotalBytes);
            StatsText.Text = _loaded.Count == 0
                ? ""
                : $"{_loaded.Count} 部 · {episodes} 集 · 占用 {FormatSize(bytes)}";
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[ShortDramaLocal] 扫描失败：{ex.Message}");
            StateHost.State = StateKind.Error;
            StateHost.ErrorDescription = $"读取本地库失败：{ex.Message}";
        }
    }

    private void Open(LocalDrama drama) => OpenRequested?.Invoke(this, drama);

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void OpenRootDir_Click(object sender, RoutedEventArgs e)
    {
        var root = _library?.RootDir;
        if (string.IsNullOrEmpty(root)) return;
        try
        {
            Directory.CreateDirectory(root);
            Process.Start(new ProcessStartInfo(root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex, "打开目录失败：");
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Dialogs.SettingsDialog { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true) Refresh();
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex, "打开设置失败：");
        }
    }

    /// <summary>
    /// 导入用户原本就有的短剧：扫目录 → 猜剧名 → 站点搜同一部 → 补元数据/封面 → 登记进本地库。
    /// 视频文件不移动、不复制、不重命名。
    /// </summary>
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var service = App.Services?.GetService<DramaLocalImportService>();
        if (service is null)
        {
            ToastService.Show("导入服务未注册");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择短剧文件夹（每部剧一个子文件夹）",
            Multiselect = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        ImportButton.IsEnabled = false;
        try
        {
            ToastService.Show("正在扫描并匹配在线信息，首次会比较慢…");
            var report = await service.ImportAsync(dialog.FolderName);
            Refresh();

            ToastService.Show($"导入完成：成功 {report.Succeeded} 部，未匹配 {report.Unmatched} 部，失败 {report.Failed} 部");

            foreach (var item in report.Items.Where(i => !i.Success))
                _logger?.Info($"[DramaImport] 未导入：{item.GuessedTitle}（{item.Error ?? "站点未搜到同名剧"}）");
        }
        catch (Exception ex)
        {
            _logger?.Warn($"[DramaImport] 导入异常：{ex.Message}");
            ToastService.ShowError(ex, "导入失败：");
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "0 MB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F0} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB",
    };
}
