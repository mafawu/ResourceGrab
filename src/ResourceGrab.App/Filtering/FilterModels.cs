namespace ResourceGrab.App.Filtering;

/// <summary>三态筛选值。</summary>
public enum FilterTriState
{
    None,
    Included,
    Excluded
}

/// <summary>单个筛选选项（标签/作者/评分等）。</summary>
public sealed record FilterOption(
    string Id,
    string Label,
    int Count = 0,
    FilterTriState State = FilterTriState.None);

/// <summary>筛选面板内一个分组的布局方式。</summary>
public enum FilterSectionLayout
{
    Wrap,
    Tree,
    Radio,
    Range
}

/// <summary>筛选面板的一个分组（如"标签"、"作者"）。</summary>
public sealed record FilterSection(
    string Id,
    string Title,
    FilterSectionLayout Layout,
    IReadOnlyList<FilterOption> Options);

/// <summary>当前筛选选择状态。</summary>
public sealed record FilterSelection
{
    public string Keyword { get; init; } = "";
    public IReadOnlyDictionary<string, FilterTriState> OptionStates { get; init; } = new Dictionary<string, FilterTriState>();
    public static readonly FilterSelection Empty = new();
}

/// <summary>筛选状态变化事件参数。</summary>
public sealed class FilterStateChangedEventArgs : EventArgs
{
    public FilterSelection Selection { get; }
    public FilterStateChangedEventArgs(FilterSelection selection) => Selection = selection;
}

/// <summary>
/// 统一筛选面板接口。三个媒体类型的筛选侧栏都必须实现此接口，
/// 外层挂到 SidebarHost 后由 ShellController 控制显示。
/// </summary>
public interface IFilterPanel
{
    event EventHandler<FilterStateChangedEventArgs>? FilterChanged;

    void SetSections(IReadOnlyList<FilterSection> sections);
    FilterSelection GetSelection();
    void ApplySelection(FilterSelection selection);
    void Clear();
}
