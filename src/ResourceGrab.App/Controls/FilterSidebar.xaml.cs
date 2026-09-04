using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ResourceGrab.App.Filtering;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 统一筛选侧栏控件。实现 IFilterPanel 接口，
/// 三个媒体类型的筛选面板共用此控件的视觉和交互。
/// </summary>
public partial class FilterSidebar : UserControl, IFilterPanel
{
    private IReadOnlyList<FilterSection> _sections = [];
    private readonly Dictionary<string, FilterTriState> _optionStates = new();

    public event EventHandler<FilterStateChangedEventArgs>? FilterChanged;

    public FilterSidebar()
    {
        InitializeComponent();
    }

    public void SetSections(IReadOnlyList<FilterSection> sections)
    {
        _sections = sections;
        SectionsHost.ItemsSource = sections;
    }

    public FilterSelection GetSelection()
    {
        var keyword = KeywordBox.Text?.Trim() ?? "";
        return new FilterSelection { Keyword = keyword, OptionStates = new Dictionary<string, FilterTriState>(_optionStates) };
    }

    public void ApplySelection(FilterSelection selection)
    {
        KeywordBox.Text = selection.Keyword;
        _optionStates.Clear();
        foreach (var kv in selection.OptionStates)
            _optionStates[kv.Key] = kv.Value;
        RefreshChipStates();
    }

    public void Clear()
    {
        KeywordBox.Text = "";
        _optionStates.Clear();
        RefreshChipStates();
        FilterChanged?.Invoke(this, new FilterStateChangedEventArgs(FilterSelection.Empty));
    }

    private void Keyword_Changed(object sender, TextChangedEventArgs e)
        => EmitChanged();

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: FilterOption option }) return;
        var current = _optionStates.GetValueOrDefault(option.Id, FilterTriState.None);
        var next = current switch
        {
            FilterTriState.None => FilterTriState.Included,
            FilterTriState.Included => FilterTriState.Excluded,
            _ => FilterTriState.None
        };
        _optionStates[option.Id] = next;
        if (sender is ToggleButton tb)
            tb.IsChecked = next == FilterTriState.Included;
        EmitChanged();
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Clear();

    private void RefreshChipStates()
    {
        // 视觉状态刷新由 ToggleButton 绑定驱动，此处仅确保数据一致。
    }

    private void EmitChanged() =>
        FilterChanged?.Invoke(this, new FilterStateChangedEventArgs(GetSelection()));
}
