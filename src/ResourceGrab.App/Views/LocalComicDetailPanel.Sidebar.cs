using System.Windows;
using ResourceGrab.App.Controls;

namespace ResourceGrab.App.Views;

/// <summary>LocalComicDetailPanel 实现 ISidebarPanel，作为 Context 模式。</summary>
public partial class LocalComicDetailPanel : ISidebarPanel
{
    public string PanelId => "context.manga.local";
    public string Title => "漫画详情";
    public object? Icon => null;
    public new FrameworkElement Content => this;
}

