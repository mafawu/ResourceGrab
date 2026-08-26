using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceGrab.App.Common;
using ResourceGrab.App.ViewModels;

namespace ResourceGrab.App.Controls;

/// <summary>本地漫画卡片：封面 + 名字 + 标签，悬停显示"打开目录"，点击打开所在文件夹。</summary>
public partial class LocalComicCard : UserControl
{
    /// <summary>封面宽高比 166:222，高度随卡片宽度自适应，窗口缩放时保持不变形。</summary>
    private const double CoverAspect = 222.0 / 166.0;

    public LocalComicCard()
    {
        InitializeComponent();
    }

    private void Card_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CoverHost.ActualWidth > 0)
        {
            CoverHost.Height = CoverHost.ActualWidth * CoverAspect;
        }
    }

    private void Root_MouseEnter(object sender, MouseEventArgs e) => Overlay.Visibility = Visibility.Visible;

    private void Root_MouseLeave(object sender, MouseEventArgs e) => Overlay.Visibility = Visibility.Collapsed;

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 按钮自身已处理点击（Handled），不会走到这里；卡片点击打开右侧本地详情
        if (DataContext is LocalComicViewModel { Source: { } comic })
        {
            Navigation.OpenLocalDetail(comic);
        }
    }
}
