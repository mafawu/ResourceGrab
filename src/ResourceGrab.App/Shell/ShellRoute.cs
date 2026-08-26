using System.Windows.Controls;

namespace ResourceGrab.App.Shell;

/// <summary>描述一次中间面板的路由跳转。</summary>
public sealed record ShellRoute(
    string RouteId,
    MediaKind Kind,
    string Title,
    string? NavItemId,
    Func<UserControl> CreateView,
    SidebarPolicy SidebarPolicy,
    bool IsImmersive = false);
