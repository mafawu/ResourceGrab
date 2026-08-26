namespace ResourceGrab.App.Shell;

/// <summary>右侧边栏的显示模式。</summary>
public enum SidebarMode
{
    Hidden,
    Filter,
    Context,
    Task
}

/// <summary>路由对右侧边栏的策略声明。</summary>
public sealed record SidebarPolicy(
    SidebarMode Mode,
    string? PanelId = null);
