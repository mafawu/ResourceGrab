using System.Windows;
using System.Windows.Controls;
using ResourceGrab.App.ViewModels;

namespace ResourceGrab.App.Controls;

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
    }
}
