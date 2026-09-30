using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 推荐页分组块：标题 + 计数 + "查看全部/收起" + 自适应换行卡片网格。
/// 采用竖向整页滚动，块内卡片随宽度自动换行，取消嵌套横向滚动，避免滚轮被行抢走。
/// </summary>
public sealed class RecommendSection : UserControl
{
    private readonly TextBlock _title;
    private readonly TextBlock _count;
    private readonly Button _toggle;
    private readonly WrapPanel _wrap;
    private readonly TextBlock _overflow;

    private IReadOnlyList<object> _items = Array.Empty<object>();
    private Func<object, FrameworkElement>? _factory;
    private int _initialCount = 12;
    private int _maxCount = 60;
    private bool _expanded;

    /// <summary>展开前默认展示的卡片数。</summary>
    public int InitialCount
    {
        get => _initialCount;
        set => _initialCount = Math.Max(1, value);
    }

    /// <summary>展开后最多渲染的卡片数（防止超大库一次性铺太多控件）。</summary>
    public int MaxCount
    {
        get => _maxCount;
        set => _maxCount = Math.Max(1, value);
    }

    public RecommendSection(string title, bool sub = false)
    {
        _title = new TextBlock
        {
            Text = title,
            FontSize = sub ? 12.5 : 15,
            FontWeight = sub ? FontWeights.SemiBold : FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _count = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _count.SetResourceReference(TextBlock.ForegroundProperty, "TextDisabledBrush");

        _toggle = new Button
        {
            Content = "查看全部",
            FontSize = 12,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
        };
        _toggle.SetResourceReference(FrameworkElement.StyleProperty, "GhostButtonStyle");
        _toggle.Click += (_, _) => Toggle();

        _overflow = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _overflow.SetResourceReference(TextBlock.ForegroundProperty, "TextDisabledBrush");

        _wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };

        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_title, 0);
        Grid.SetColumn(_count, 1);
        Grid.SetColumn(_toggle, 2);
        header.Children.Add(_title);
        header.Children.Add(_count);
        header.Children.Add(_toggle);

        var root = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        root.Children.Add(header);
        root.Children.Add(_wrap);
        root.Children.Add(_overflow);
        Content = root;
    }

    /// <summary>设置条目与卡片工厂；超过 InitialCount 时显示"查看全部"。</summary>
    public void SetItems<T>(IReadOnlyList<T> items, Func<T, FrameworkElement> factory, int? initialCount = null, int? maxCount = null)
    {
        _items = items.Cast<object>().ToList();
        _factory = item => factory((T)item);
        _initialCount = Math.Max(1, initialCount ?? _initialCount);
        _maxCount = Math.Max(_initialCount, maxCount ?? _maxCount);
        _expanded = false;
        Render();
    }

    public void Clear()
    {
        _items = Array.Empty<object>();
        _factory = null;
        _wrap.Children.Clear();
        _count.Text = "";
        _toggle.Visibility = Visibility.Collapsed;
        _overflow.Visibility = Visibility.Collapsed;
    }

    private void Toggle()
    {
        _expanded = !_expanded;
        Render();
    }

    private void Render()
    {
        _wrap.Children.Clear();
        _overflow.Visibility = Visibility.Collapsed;

        if (_factory is null)
        {
            _count.Text = "";
            _toggle.Visibility = Visibility.Collapsed;
            return;
        }

        var total = _items.Count;
        _count.Text = total == 0 ? "" : $"共 {total}";

        var visible = _expanded ? Math.Min(total, _maxCount) : Math.Min(total, _initialCount);
        for (var i = 0; i < visible; i++)
        {
            _wrap.Children.Add(_factory(_items[i]));
        }

        _toggle.Visibility = total > _initialCount ? Visibility.Visible : Visibility.Collapsed;
        _toggle.Content = _expanded ? "收起" : "查看全部";

        if (_expanded && total > _maxCount)
        {
            _overflow.Text = $"仅显示前 {_maxCount} 项，更多请到本地页查看";
            _overflow.Visibility = Visibility.Visible;
        }
    }
}
