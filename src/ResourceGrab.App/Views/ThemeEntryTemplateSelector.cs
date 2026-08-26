using System.Windows;
using System.Windows.Controls;
using ResourceGrab.Core.Models;

namespace ResourceGrab.App.Views;

/// <summary>主题条目模板选择器：分类条目用单选胶囊（可携带子分类），标签条目用普通胶囊。</summary>
public class ThemeEntryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CategoryTemplate { get; set; }
    public DataTemplate? TagTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is ThemeEntry { IsCategory: true } ? CategoryTemplate : TagTemplate;
}
