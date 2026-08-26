using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 列表页的标准结果区域。统一管理 Loading / Empty / Error / Ready 状态，
/// 内部复用 VirtualizedCardGrid，禁止页面手写 Visibility 面板。
/// </summary>
public partial class MediaResultHost : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(ResultState<object>), typeof(MediaResultHost),
        new PropertyMetadata(null, OnStateChanged));

    /// <summary>当前状态（泛型擦除后用 object 存储）。</summary>
    public ResultState<object>? State
    {
        get => (ResultState<object>?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Ready 状态下要渲染的卡片列表。</summary>
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(object), typeof(MediaResultHost),
        new PropertyMetadata(null));

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>结果区域的 DataTemplate。</summary>
    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate), typeof(DataTemplate), typeof(MediaResultHost),
        new PropertyMetadata(null));

    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    private ICommand? _retryCommand;

    public event EventHandler<RoutedEventArgs>? RetryRequested;

    public MediaResultHost()
    {
        InitializeComponent();
    }

    /// <summary>设置状态并切换可见面板。</summary>
    public void SetState<T>(ResultState<T> state)
    {
        _retryCommand = state.RetryCommand;

        LoadingPanel.Visibility = state.Status == ResultStatus.Loading ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = state.Status == ResultStatus.Empty ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = state.Status == ResultStatus.Error ? Visibility.Visible : Visibility.Collapsed;
        ResultHost.Visibility = state.Status == ResultStatus.Ready ? Visibility.Visible : Visibility.Collapsed;

        if (state.Status == ResultStatus.Empty)
        {
            EmptyTitleText.Text = state.EmptyTitle ?? "暂无数据";
            EmptyMessageText.Text = state.EmptyMessage ?? "";
        }
        else if (state.Status == ResultStatus.Error)
        {
            ErrorDescriptionText.Text = state.ErrorMessage ?? "";
        }
        else if (state.Status == ResultStatus.Ready && state.Items is IReadOnlyList<object> items)
        {
            ItemsSource = items;
            ResultHost.ContentTemplate = ItemTemplate;
        }
    }

    /// <summary>便捷方法：设置为 Ready 并填充内容。</summary>
    public void SetReady(object items, DataTemplate template)
    {
        SetState(new ResultState<object>(ResultStatus.Ready, Items: [items]));
        ItemsSource = items;
        ResultHost.ContentTemplate = template;
        ResultHost.Visibility = Visibility.Visible;
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryRequested?.Invoke(this, e);
        _retryCommand?.Execute(null);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MediaResultHost host && e.NewValue is ResultState<object> state)
            host.SetState(state);
    }
}
