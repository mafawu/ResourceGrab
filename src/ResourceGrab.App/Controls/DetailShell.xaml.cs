using System.Windows;
using System.Windows.Controls;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 统一详情页外壳。提供返回按钮 + 标题 + 操作区 + 可滚动内容区域。
/// 漫画章节页、本地漫画详情、视频详情逐步迁入。
/// </summary>
public partial class DetailShell : UserControl
{
    /// <summary>用户点击返回时触发。</summary>
    public event EventHandler? BackRequested;

    public DetailShell()
    {
        InitializeComponent();
    }

    /// <summary>设置详情页标题和内容。</summary>
    public void Show(string title, FrameworkElement body)
    {
        HeaderText.Text = title;
        BodyHost.Content = body;
    }

    /// <summary>向操作区添加按钮。</summary>
    public void AddActionButton(Button button)
    {
        ActionHost.Children.Add(button);
    }

    /// <summary>清空所有操作按钮。</summary>
    public void ClearActions() => ActionHost.Children.Clear();

    private void Back_Click(object sender, RoutedEventArgs e)
        => BackRequested?.Invoke(this, EventArgs.Empty);
}
