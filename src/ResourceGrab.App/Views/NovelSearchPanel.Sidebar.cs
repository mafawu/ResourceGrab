using System.Windows;
using ResourceGrab.App.Controls;

namespace ResourceGrab.App.Views;

/// <summary>NovelSearchPanel 实现 ISidebarPanel。</summary>
public partial class NovelSearchPanel : ISidebarPanel
{
    public string PanelId => "filter.novel";
    public string Title => "小说筛选";
    public object? Icon => null;
    public new FrameworkElement Content => this;
}

