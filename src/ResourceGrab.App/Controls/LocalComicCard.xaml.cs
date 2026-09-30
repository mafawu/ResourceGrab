using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceGrab.App.Common;
using ResourceGrab.App.ViewModels;

namespace ResourceGrab.App.Controls;

/// <summary>本地漫画卡片：封面 + 名字 + 标签，悬停显示"打开目录"，点击打开所在文件夹。</summary>
[System.Obsolete("Use MediaCard + MediaCardAdapters instead")]
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
        // 用网格绑定的显式 Width（滚动/回收时稳定）而非 ActualWidth（布局中会瞬变），
        // 并做变化阈值判断，避免高度反复重算导致从底部上滚时闪烁。
        var width = !double.IsNaN(Width) && Width > 0 ? Width : CoverHost.ActualWidth;
        if (width <= 0) return;
        var target = width * CoverAspect;
        if (Math.Abs(CoverHost.Height - target) > 0.5)
            CoverHost.Height = target;
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

