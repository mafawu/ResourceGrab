using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 统一的列表页工具栏：搜索框 + 筛选 chips + 排序区 + 附加动作区 + 卡片尺寸 + 搜索按钮。
/// 各段可独立隐藏（ShowXxx 属性），替代原先各页面手搓的三种工具栏布局。
/// </summary>
public partial class BrowserToolbar : UserControl
{
    public static readonly DependencyProperty WatermarkProperty = DependencyProperty.Register(
        nameof(Watermark), typeof(string), typeof(BrowserToolbar),
        new PropertyMetadata("", (d, e) => ((BrowserToolbar)d).PART_SearchBox.Watermark = (string)e.NewValue));

    public static readonly DependencyProperty ShowSearchBoxProperty = DependencyProperty.Register(
        nameof(ShowSearchBox), typeof(bool), typeof(BrowserToolbar),
        new PropertyMetadata(true, (d, e) => ((BrowserToolbar)d).UpdateSegments()));

    public static readonly DependencyProperty ShowSearchButtonProperty = DependencyProperty.Register(
        nameof(ShowSearchButton), typeof(bool), typeof(BrowserToolbar),
        new PropertyMetadata(true, (d, e) => ((BrowserToolbar)d).UpdateSegments()));

    public static readonly DependencyProperty ShowSizeSliderProperty = DependencyProperty.Register(
        nameof(ShowSizeSlider), typeof(bool), typeof(BrowserToolbar),
        new PropertyMetadata(true, (d, e) => ((BrowserToolbar)d).UpdateSegments()));

    public static readonly DependencyProperty ShowChipsProperty = DependencyProperty.Register(
        nameof(ShowChips), typeof(bool), typeof(BrowserToolbar),
        new PropertyMetadata(true, (d, e) => ((BrowserToolbar)d).UpdateSegments()));

    public static readonly DependencyProperty SizeSliderColumnsProperty = DependencyProperty.Register(
        nameof(SizeSliderColumns), typeof(int), typeof(BrowserToolbar),
        new PropertyMetadata(0));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(object), typeof(BrowserToolbar),
        new PropertyMetadata(null));

    public string Watermark
    {
        get => (string)GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public bool ShowSearchBox { get => (bool)GetValue(ShowSearchBoxProperty); set => SetValue(ShowSearchBoxProperty, value); }
    public bool ShowSearchButton { get => (bool)GetValue(ShowSearchButtonProperty); set => SetValue(ShowSearchButtonProperty, value); }
    public bool ShowSizeSlider { get => (bool)GetValue(ShowSizeSliderProperty); set => SetValue(ShowSizeSliderProperty, value); }
    public bool ShowChips { get => (bool)GetValue(ShowChipsProperty); set => SetValue(ShowChipsProperty, value); }

    /// <summary>卡片尺寸滑杆的目标列数（双向绑定到内部 CardSizeSlider.Columns）。</summary>
    public int SizeSliderColumns
    {
        get => (int)GetValue(SizeSliderColumnsProperty);
        set => SetValue(SizeSliderColumnsProperty, value);
    }

    /// <summary>左侧标题区内容（如「本地视频 + 数量徽标」）；为空时隐藏。</summary>
    public object? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>搜索框文本变化（实时）。</summary>
    public event EventHandler<string>? SearchChanged;

    /// <summary>搜索框提交（回车）。</summary>
    public event EventHandler<string>? SearchSubmitted;

    /// <summary>点击搜索按钮。</summary>
    public event EventHandler<RoutedEventArgs>? SearchButtonClick;

    /// <summary>内部命名部件，供页面代码后置直接操作（构建 chips、读写关键词等）。</summary>
    public SearchBoxControl SearchBox => PART_SearchBox;
    public WrapPanel Chips => PART_Chips;
    public CardSizeSlider SizeSlider => PART_SizeSlider;
    public ContentPresenter SortContent => PART_Sort;
    public ContentPresenter Actions => PART_Actions;
    public ContentPresenter TitleContent => PART_Title;

    public BrowserToolbar()
    {
        InitializeComponent();
        PART_SearchBox.SearchChanged += (_, e) => SearchChanged?.Invoke(this, e);
        PART_SearchBox.SearchSubmitted += (_, e) => SearchSubmitted?.Invoke(this, e);
        BindingOperations.SetBinding(PART_SizeSlider, CardSizeSlider.ColumnsProperty,
            new System.Windows.Data.Binding(nameof(SizeSliderColumns)) { Source = this, Mode = System.Windows.Data.BindingMode.TwoWay });
        DataContextChanged += (_, _) => UpdateSegments();
        UpdateSegments();
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
        => SearchButtonClick?.Invoke(this, e);

    private void UpdateSegments()
    {
        PART_SearchBox.Visibility = ShowSearchBox ? Visibility.Visible : Visibility.Collapsed;
        PART_SearchButton.Visibility = ShowSearchButton ? Visibility.Visible : Visibility.Collapsed;
        PART_SizeSlider.Visibility = ShowSizeSlider ? Visibility.Visible : Visibility.Collapsed;
        PART_Chips.Visibility = ShowChips ? Visibility.Visible : Visibility.Collapsed;
        PART_Title.Visibility = Title is null ? Visibility.Collapsed : Visibility.Visible;
        PART_Sort.Visibility = PART_Sort.Content is null ? Visibility.Collapsed : Visibility.Visible;
        PART_Actions.Visibility = PART_Actions.Content is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
