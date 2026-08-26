using System.Windows;
using ResourceGrab.App.Controls;

namespace ResourceGrab.App.Views;

/// <summary>VideoSearchPanel 实现 ISidebarPanel。</summary>
public partial class VideoSearchPanel : ISidebarPanel
{
    public string PanelId => "filter.video";
    public string Title => "视频筛选";
    public object? Icon => null;
    public new FrameworkElement Content => this;
}

