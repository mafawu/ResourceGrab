using System.Windows;
using ResourceGrab.App.Controls;

namespace ResourceGrab.App.Views;

/// <summary>DownloadPanel 实现 ISidebarPanel，作为 Task 模式挂到 SidebarHost。</summary>
public partial class DownloadPanel : ISidebarPanel
{
    public string PanelId => "task.downloads";
    public string Title => "下载队列";
    public object? Icon => null;
    public new FrameworkElement Content => this;
}

