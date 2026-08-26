using System.Windows;
using System.Windows.Controls;
using ResourceGrab.App.Shell;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 数据驱动的左侧导航栏控件。只负责渲染 NavSection 列表，
/// 不理解漫画、视频或小说业务逻辑。
/// </summary>
public partial class NavRail : UserControl
{
    public static readonly DependencyProperty SectionsProperty =
        DependencyProperty.Register(nameof(Sections), typeof(IReadOnlyList<NavSection>),
            typeof(NavRail), new PropertyMetadata(null, OnSectionsChanged));

    /// <summary>导航项被点击时触发，参数为 NavItem。</summary>
    public event EventHandler<NavItem>? ItemClicked;

    private readonly List<RadioButton> _radioButtons = new();

    public IReadOnlyList<NavSection>? Sections
    {
        get => (IReadOnlyList<NavSection>?)GetValue(SectionsProperty);
        set => SetValue(SectionsProperty, value);
    }

    public string? SelectedItemId => _radioButtons
        .FirstOrDefault(rb => rb.IsChecked == true)?.Content is NavItem { Id: string id } ? id : null;

    public NavRail()
    {
        InitializeComponent();
        DataContext = this;
    }

    private static void OnSectionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NavRail rail) rail.Rebuild();
    }

    private void Rebuild()
    {
        var selectedItemId = SelectedItemId;
        SectionsHost.ItemsSource = Sections;
        _radioButtons.Clear();

        // 延迟收集 RadioButton；若新菜单仍包含原入口，则保持选中状态。
        Dispatcher.BeginInvoke(() =>
        {
            CollectRadioButtons(SectionsHost);
            if (!string.IsNullOrEmpty(selectedItemId))
            {
                var restored = FindRadioButtonByContent(selectedItemId);
                if (restored != null)
                {
                    restored.IsChecked = true;
                    return;
                }
            }

            if (_radioButtons.Count > 0)
                _radioButtons[0].IsChecked = true;
        });
    }

    private void CollectRadioButtons(DependencyObject parent)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is RadioButton rb)
            {
                _radioButtons.Add(rb);
            }
            else
            {
                CollectRadioButtons(child);
            }
        }
    }

    private void RadioButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Content: NavItem item })
            ItemClicked?.Invoke(this, item);
    }

    /// <summary>按 Id 选中导航项；未找到则不改变当前选中。</summary>
    public void SelectItem(string itemId)
    {
        foreach (var section in Sections ?? [])
        {
            foreach (var item in section.Items)
            {
                if (item.Id == itemId)
                {
                    var rb = FindRadioButtonByContent(itemId);
                    if (rb != null) rb.IsChecked = true;
                    return;
                }
            }
        }
    }

    private RadioButton? FindRadioButtonByContent(string itemId)
    {
        return _radioButtons.FirstOrDefault(rb => rb.Content is NavItem { Id: string id } && id == itemId);
    }
}
