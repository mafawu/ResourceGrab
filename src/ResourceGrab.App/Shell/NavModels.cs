namespace ResourceGrab.App.Shell;

/// <summary>左侧导航栏的单个条目。</summary>
public sealed record NavItem(
    string Id,
    string Title,
    object? Icon = null,
    bool IsEnabled = true);

/// <summary>左侧导航栏的分组（如"发现"、"我的"）。</summary>
public sealed record NavSection(
    string Title,
    IReadOnlyList<NavItem> Items);

/// <summary>每种媒体类型提供自己的导航分组集合。</summary>
public interface INavSectionProvider
{
    MediaKind Kind { get; }
    IReadOnlyList<NavSection> GetSections();
}
