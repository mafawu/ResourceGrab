using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ResourceGrab.App.ViewModels;

namespace ResourceGrab.App.Controls;

/// <summary>字符串非空（非 null、非 ""）时返回 Visible。</summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → ♥ / ♡。</summary>
public sealed class FavoriteTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "♥" : "♡";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>根据 MediaVisualKind 选择卡片渲染模板。</summary>
public sealed class MediaCardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PortraitTemplate { get; set; }
    public DataTemplate? WideThumbTemplate { get; set; }
    public DataTemplate? TextCoverTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not MediaCardViewModel vm) return null;
        return vm.VisualKind switch
        {
            MediaVisualKind.PortraitCover => PortraitTemplate,
            MediaVisualKind.WideThumb => WideThumbTemplate,
            MediaVisualKind.TextCover => TextCoverTemplate,
            _ => PortraitTemplate
        };
    }
}

/// <summary>统一媒体卡片控件。只负责根据 ViewModel 渲染，不理解具体业务。</summary>
public partial class MediaCard : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(MediaCardViewModel), typeof(MediaCard),
        new PropertyMetadata(null));

    public MediaCardViewModel? Card
    {
        get => (MediaCardViewModel?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    public MediaCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Card = DataContext as MediaCardViewModel;
        // 点卡片 = OpenCommand（详情）；框选模式下由适配器切换为选中。
        // 浮层按钮/徽章通过 MouseBinding 消费点击并标记 Handled，不会冒泡到这里。
        MouseLeftButtonUp += (_, e) =>
        {
            if (Card?.OpenCommand is { } cmd && cmd.CanExecute(null))
            {
                cmd.Execute(null);
                e.Handled = true;
            }
        };
    }
}
