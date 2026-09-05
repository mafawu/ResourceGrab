using System.Windows;
using ResourceGrab.App.Controls;
using ResourceGrab.App.Filtering;

namespace ResourceGrab.App.Views;

/// <summary>NovelSearchPanel 实现 ISidebarPanel，作为 Filter 模式挂到 SidebarHost。</summary>
public partial class NovelSearchPanel : ISidebarPanel
{
    public string PanelId => "filter.novel";
    public string Title => "小说筛选";
    public object? Icon => null;
    public new FrameworkElement Content => this;
}

/// <summary>
/// NovelSearchPanel 的统一筛选契约实现。
/// 选项 Id 约定：tag:{完整标签路径}。
/// </summary>
public partial class NovelSearchPanel : IFilterPanel
{
    private event EventHandler<FilterStateChangedEventArgs>? _contractFilterChanged;

    event EventHandler<FilterStateChangedEventArgs>? IFilterPanel.FilterChanged
    {
        add => _contractFilterChanged += value;
        remove => _contractFilterChanged -= value;
    }

    FilterSelection IFilterPanel.GetSelection()
    {
        var states = new Dictionary<string, FilterTriState>();
        foreach (var t in _included) states[$"tag:{t}"] = FilterTriState.Included;
        foreach (var t in _excluded) states[$"tag:{t}"] = FilterTriState.Excluded;
        return new FilterSelection
        {
            Keyword = KeywordBox.Text?.Trim() ?? "",
            OptionStates = states,
        };
    }

    void IFilterPanel.ApplySelection(FilterSelection selection)
    {
        try { KeywordBox.Text = selection.Keyword; } catch { }
        _included.Clear();
        _excluded.Clear();
        foreach (var (id, state) in selection.OptionStates)
        {
            if (state == FilterTriState.None || !id.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)) continue;
            (state == FilterTriState.Included ? _included : _excluded).Add(id[4..]);
        }
        Rebuild();
        UpdateUi();
    }

    void IFilterPanel.Clear() => ClearButton_Click(this, new RoutedEventArgs());

    void IFilterPanel.SetSections(IReadOnlyList<FilterSection> sections)
    {
        foreach (var section in sections)
        {
            if (section.Id != "tags") continue;
            var tree = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in section.Options)
            {
                var parts = o.Label.Split('/');
                var top = parts.Length > 1 ? string.Join('/', parts[..^1]) : o.Label;
                if (!tree.TryGetValue(top, out var subs)) tree[top] = subs = new List<string>();
                if (parts.Length > 1) subs.Add(o.Label);
                counts[o.Label] = o.Count;
            }
            SetData(tree, counts);
        }
    }
}
