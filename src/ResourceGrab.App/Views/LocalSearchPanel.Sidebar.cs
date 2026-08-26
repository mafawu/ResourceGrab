using System.Windows;
using ResourceGrab.App.Controls;

namespace ResourceGrab.App.Views;

/// <summary>LocalSearchPanel 实现 ISidebarPanel，作为 Filter 模式挂到 SidebarHost。</summary>
public partial class LocalSearchPanel : ISidebarPanel
{
    public string PanelId => "filter.manga";
    public string Title => "本地筛选";
    public object? Icon => null;
    public new FrameworkElement Content => this;
}

