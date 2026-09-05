using System.Windows.Controls;

namespace ResourceGrab.App.Shell;

/// <summary>
/// 统一导航服务。所有主内容变化必须经过此服务，
/// 不允许页面直接给 PageHost.Content 赋值。
/// </summary>
public sealed class ShellNavigator
{
    private readonly ShellController _controller;
    private readonly Dictionary<string, Func<UserControl>> _routeFactories = new();
    private readonly Dictionary<string, SidebarPolicy> _routePolicies = new();
    private readonly Dictionary<MediaKind, Stack<ShellRoute>> _backStacks = new();
    private readonly Dictionary<MediaKind, ShellRoute?> _lastRoutes = new();

    /// <summary>路由切换后触发，参数为新路由。</summary>
    public event EventHandler<ShellRoute>? RouteChanged;

    /// <summary>返回栈变化（可能为空）时触发。</summary>
    public event EventHandler? BackStackChanged;

    public ShellNavigator(ShellController controller)
    {
        _controller = controller;
        foreach (MediaKind kind in Enum.GetValues<MediaKind>())
        {
            _backStacks[kind] = new Stack<ShellRoute>();
            _lastRoutes[kind] = null;
        }
    }

    /// <summary>注册路由工厂；policy 声明该路由的右侧边栏策略（默认 Hidden）。</summary>
    public void Register(string routeId, Func<UserControl> factory, SidebarPolicy? policy = null)
    {
        _routeFactories[routeId] = factory;
        _routePolicies[routeId] = policy ?? new SidebarPolicy(SidebarMode.Hidden);
    }

    /// <summary>跳转到已注册的路由。</summary>
    public bool GoTo(string routeId)
    {
        if (!_routeFactories.TryGetValue(routeId, out var factory)) return false;
        var kind = InferKind(routeId);
        var current = _lastRoutes[kind];
        if (current != null && current.RouteId != routeId)
            _backStacks[kind].Push(current);
        var route = new ShellRoute(routeId, kind, TitleFromId(routeId), NavItemFromId(routeId), factory,
            PolicyFor(routeId));
        _lastRoutes[kind] = route;
        _controller.SetKind(kind);
        RouteChanged?.Invoke(this, route);
        return true;
    }

    /// <summary>替换当前路由而不压栈。</summary>
    public bool ReplaceCurrent(string routeId)
    {
        if (!_routeFactories.TryGetValue(routeId, out var factory)) return false;
        var kind = InferKind(routeId);
        var route = new ShellRoute(routeId, kind, TitleFromId(routeId), NavItemFromId(routeId), factory,
            PolicyFor(routeId));
        _lastRoutes[kind] = route;
        _controller.SetKind(kind);
        RouteChanged?.Invoke(this, route);
        return true;
    }

    /// <summary>返回上一个路由；无可返回时返回 false。</summary>
    public bool Back()
    {
        var kind = _controller.CurrentKind;
        var stack = _backStacks[kind];
        if (stack.Count == 0) return false;
        var previous = stack.Pop();
        _lastRoutes[kind] = previous;
        RouteChanged?.Invoke(this, previous);
        BackStackChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>当前类型的返回栈是否为空。</summary>
    public bool CanGoBack => _backStacks[_controller.CurrentKind].Count > 0;

    /// <summary>清空指定类型的返回栈。</summary>
    public void ResetStack(MediaKind kind) => _backStacks[kind].Clear();

    /// <summary>切换到指定媒体类型的最近路由；无历史则返回 false。</summary>
    public bool RestoreLast(MediaKind kind)
    {
        if (_lastRoutes.TryGetValue(kind, out var last) && last != null)
        {
            _controller.SetKind(kind);
            RouteChanged?.Invoke(this, last);
            return true;
        }
        return false;
    }

    private static MediaKind InferKind(string routeId)
    {
        if (routeId.StartsWith("manga.")) return MediaKind.Manga;
        if (routeId.StartsWith("video.")) return MediaKind.Video;
        if (routeId.StartsWith("novel.")) return MediaKind.Novel;
        return MediaKind.Manga;
    }

    private static string TitleFromId(string routeId)
    {
        var segment = routeId.Split('.').LastOrDefault() ?? "";
        return char.ToUpper(segment[0]) + segment[1..];
    }

    private static string? NavItemFromId(string routeId) => routeId;

    /// <summary>读取路由注册时声明的侧栏策略；未注册的路由返回 Hidden。</summary>
    public SidebarPolicy PolicyFor(string routeId) =>
        _routePolicies.TryGetValue(routeId, out var policy) ? policy : new SidebarPolicy(SidebarMode.Hidden);
}
