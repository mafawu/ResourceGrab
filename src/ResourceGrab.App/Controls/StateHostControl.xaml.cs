using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ResourceGrab.App.Controls;

public enum StateKind
{
    Hint,
    Loading,
    Empty,
    Error,
    Result,
}

public partial class StateHostControl : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(StateKind), typeof(StateHostControl), new PropertyMetadata(StateKind.Hint));

    public static readonly DependencyProperty ResultProperty = DependencyProperty.Register(
        nameof(Result), typeof(object), typeof(StateHostControl));

    public static readonly DependencyProperty ResultTemplateProperty = DependencyProperty.Register(
        nameof(ResultTemplate), typeof(DataTemplate), typeof(StateHostControl));

    public static readonly DependencyProperty HintIconProperty = DependencyProperty.Register(
        nameof(HintIcon), typeof(Geometry), typeof(StateHostControl));

    public static readonly DependencyProperty HintTitleProperty = DependencyProperty.Register(
        nameof(HintTitle), typeof(string), typeof(StateHostControl), new PropertyMetadata("暂无内容"));

    public static readonly DependencyProperty HintDescriptionProperty = DependencyProperty.Register(
        nameof(HintDescription), typeof(string), typeof(StateHostControl), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty LoadingTextProperty = DependencyProperty.Register(
        nameof(LoadingText), typeof(string), typeof(StateHostControl), new PropertyMetadata("加载中…"));

    public static readonly DependencyProperty EmptyTitleProperty = DependencyProperty.Register(
        nameof(EmptyTitle), typeof(string), typeof(StateHostControl), new PropertyMetadata("暂无数据"));

    public static readonly DependencyProperty EmptyDescriptionProperty = DependencyProperty.Register(
        nameof(EmptyDescription), typeof(string), typeof(StateHostControl), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ErrorTitleProperty = DependencyProperty.Register(
        nameof(ErrorTitle), typeof(string), typeof(StateHostControl), new PropertyMetadata("加载失败"));

    public static readonly DependencyProperty ErrorDescriptionProperty = DependencyProperty.Register(
        nameof(ErrorDescription), typeof(string), typeof(StateHostControl), new PropertyMetadata("请检查网络后重试"));

    public static readonly DependencyProperty RetryTextProperty = DependencyProperty.Register(
        nameof(RetryText), typeof(string), typeof(StateHostControl), new PropertyMetadata("重试"));

    public static readonly DependencyProperty RetryCommandProperty = DependencyProperty.Register(
        nameof(RetryCommand), typeof(ICommand), typeof(StateHostControl));

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(StateHostControl), new PropertyMetadata(160d));

    public static readonly DependencyProperty SlotWidthProperty = DependencyProperty.Register(
        nameof(SlotWidth), typeof(double), typeof(StateHostControl), new PropertyMetadata(176d));

    public static readonly DependencyProperty DesiredColumnsProperty = DependencyProperty.Register(
        nameof(DesiredColumns), typeof(int), typeof(StateHostControl), new PropertyMetadata(3));

    public StateKind State { get => (StateKind)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public object Result { get => GetValue(ResultProperty); set => SetValue(ResultProperty, value); }
    public DataTemplate ResultTemplate { get => (DataTemplate)GetValue(ResultTemplateProperty); set => SetValue(ResultTemplateProperty, value); }
    public Geometry? HintIcon { get => (Geometry?)GetValue(HintIconProperty); set => SetValue(HintIconProperty, value); }
    public string HintTitle { get => (string)GetValue(HintTitleProperty); set => SetValue(HintTitleProperty, value); }
    public string HintDescription { get => (string)GetValue(HintDescriptionProperty); set => SetValue(HintDescriptionProperty, value); }
    public string LoadingText { get => (string)GetValue(LoadingTextProperty); set => SetValue(LoadingTextProperty, value); }
    public string EmptyTitle { get => (string)GetValue(EmptyTitleProperty); set => SetValue(EmptyTitleProperty, value); }
    public string EmptyDescription { get => (string)GetValue(EmptyDescriptionProperty); set => SetValue(EmptyDescriptionProperty, value); }
    public string ErrorTitle { get => (string)GetValue(ErrorTitleProperty); set => SetValue(ErrorTitleProperty, value); }
    public string ErrorDescription { get => (string)GetValue(ErrorDescriptionProperty); set => SetValue(ErrorDescriptionProperty, value); }
    public string RetryText { get => (string)GetValue(RetryTextProperty); set => SetValue(RetryTextProperty, value); }
    public ICommand RetryCommand { get => (ICommand)GetValue(RetryCommandProperty); set => SetValue(RetryCommandProperty, value); }
    public double CardWidth { get => (double)GetValue(CardWidthProperty); set => SetValue(CardWidthProperty, value); }
    public double SlotWidth { get => (double)GetValue(SlotWidthProperty); set => SetValue(SlotWidthProperty, value); }
    public int DesiredColumns { get => (int)GetValue(DesiredColumnsProperty); set => SetValue(DesiredColumnsProperty, value); }

    public StateHostControl()
    {
        InitializeComponent();
    }
}
