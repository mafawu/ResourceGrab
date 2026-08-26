using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ResourceGrab.App.Controls;

/// <summary>标签输入控件：回车 / 逗号添加标签，点 ✕ 移除；空框退格删除最后一个。</summary>
public partial class TagEditorInput : UserControl
{
    public static readonly DependencyProperty TagsProperty =
        DependencyProperty.Register(nameof(Tags), typeof(ObservableCollection<string>), typeof(TagEditorInput),
            new PropertyMetadata(null));

    /// <summary>当前标签集合（就地修改，调用方持有引用）。</summary>
    public ObservableCollection<string> Tags
    {
        get => (ObservableCollection<string>)GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    public TagEditorInput()
    {
        InitializeComponent();
        Tags ??= new ObservableCollection<string>();
        ChipItems.ItemsSource = Tags;
        InputBox.CaretBrush = TryFindResource("TextPrimaryBrush") as System.Windows.Media.Brush;
    }

    private void AddFromInput()
    {
        var text = InputBox.Text.Trim().TrimEnd(',').Trim();
        if (text.Length == 0) return;

        // 逗号分隔批量添加
        foreach (var part in text.Split(',', '，'))
        {
            var tag = part.Trim();
            if (tag.Length == 0) continue;
            if (!Tags.Contains(tag))
            {
                Tags.Add(tag);
            }
        }
        InputBox.Clear();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                AddFromInput();
                e.Handled = true;
                break;
            case Key.Back when InputBox.Text.Length == 0 && Tags.Count > 0:
                Tags.RemoveAt(Tags.Count - 1);
                e.Handled = true;
                break;
        }
    }

    private void InputBox_LostFocus(object sender, RoutedEventArgs e) => AddFromInput();

    private void RemoveChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string tag })
        {
            Tags.Remove(tag);
        }
    }

    private void Host_Click(object sender, MouseButtonEventArgs e)
    {
        // 点空白区域聚焦输入框
        if (e.OriginalSource is DependencyObject d && !IsDescendantOf(InputBox, d))
        {
            InputBox.Focus();
        }

        static bool IsDescendantOf(DependencyObject ancestor, DependencyObject node)
        {
            while (node is not null)
            {
                if (ReferenceEquals(node, ancestor)) return true;
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }
            return false;
        }
    }
}
