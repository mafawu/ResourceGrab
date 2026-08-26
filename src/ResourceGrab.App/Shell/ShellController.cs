using ResourceGrab.App.Common;

namespace ResourceGrab.App.Shell;

/// <summary>
/// 全局壳状态控制器：不绘制 UI，只维护当前媒体类型、路由和侧栏模式。
/// MainWindow 订阅其属性变化来更新 LeftNavHost / CenterRouteHost / RightSidebarHost。
/// </summary>
public sealed class ShellController : ObservableObject
{
    private MediaKind _currentKind = MediaKind.Manga;
    private ShellRoute? _currentRoute;
    private SidebarMode _sidebarMode = SidebarMode.Hidden;
    private bool _leftRailVisible = true;
    private bool _rightSidebarVisible;

    /// <summary>按媒体类型维护独立返回栈。</summary>
    private readonly Dictionary<MediaKind, Stack<ShellRoute>> _backStacks = new();

    public MediaKind CurrentKind => _currentKind;
    public ShellRoute? CurrentRoute => _currentRoute;
    public SidebarMode SidebarMode => _sidebarMode;
    public bool LeftRailVisible => _leftRailVisible;
    public bool RightSidebarVisible => _rightSidebarVisible;

    public ShellController()
    {
        foreach (MediaKind kind in Enum.GetValues<MediaKind>())
            _backStacks[kind] = new Stack<ShellRoute>();
    }

    /// <summary>切换媒体类型，恢复该类型最近的侧栏可见性。</summary>
    public void SetKind(MediaKind kind)
    {
        if (kind == _currentKind) return;
        _currentKind = kind;
        OnPropertyChanged(nameof(CurrentKind));
    }

    /// <summary>导航到新路由。ReplaceCurrent 为 true 时不压栈。</summary>
    public void Navigate(ShellRoute route)
    {
        if (_currentRoute != null && route.RouteId != _currentRoute.RouteId)
        {
            _backStacks[_currentKind].Push(_currentRoute);
        }
        _currentRoute = route;
        ApplySidebarPolicy(route);
        OnPropertyChanged(nameof(CurrentRoute));
    }

    /// <summary>返回上一个路由；无可返回时返回 false。</summary>
    public bool Back()
    {
        var stack = _backStacks[_currentKind];
        if (stack.Count == 0) return false;
        var previous = stack.Pop();
        _currentRoute = previous;
        ApplySidebarPolicy(previous);
        OnPropertyChanged(nameof(CurrentRoute));
        return true;
    }

    /// <summary>清空指定类型的返回栈。</summary>
    public void ResetStack(MediaKind kind) => _backStacks[kind].Clear();

    public void SetSidebarMode(SidebarMode mode)
    {
        if (mode == _sidebarMode) return;
        _sidebarMode = mode;
        OnPropertyChanged(nameof(SidebarMode));
    }

    public void SetLeftRailVisible(bool visible)
    {
        if (visible == _leftRailVisible) return;
        _leftRailVisible = visible;
        OnPropertyChanged(nameof(LeftRailVisible));
    }

    public void SetRightSidebarVisible(bool visible)
    {
        if (visible == _rightSidebarVisible) return;
        _rightSidebarVisible = visible;
        OnPropertyChanged(nameof(RightSidebarVisible));
    }

    private void ApplySidebarPolicy(ShellRoute route)
    {
        var policy = route.SidebarPolicy;
        SetSidebarMode(policy.Mode);

        // 沉浸式页面隐藏左右侧栏。
        SetLeftRailVisible(!route.IsImmersive);
        SetRightSidebarVisible(policy.Mode != SidebarMode.Hidden);
    }
}
