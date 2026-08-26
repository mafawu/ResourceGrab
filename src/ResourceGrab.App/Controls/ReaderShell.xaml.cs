using System.Windows;
using System.Windows.Controls;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 统一阅读器外壳。提供顶部工具栏 + 内容区 + 底部控制栏。
/// 漫画本地/在线阅读器和小说阅读器共用，沉浸模式可隐藏工具栏和底栏。
/// </summary>
public partial class ReaderShell : UserControl
{
    /// <summary>用户点击返回时触发。</summary>
    public event EventHandler? BackRequested;

    private bool _isImmersive;

    public ReaderShell()
    {
        InitializeComponent();
    }

    /// <summary>设置标题、内容视图和工具栏按钮。</summary>
    public void Show(string title, FrameworkElement content, params Button[] toolbarButtons)
    {
        ReaderTitle.Text = title;
        ContentViewHost.Content = content;
        ToolbarActionsHost.Children.Clear();
        foreach (var btn in toolbarButtons)
            ToolbarActionsHost.Children.Add(btn);
    }

    /// <summary>向底部控制栏添加控件。</summary>
    public void AddBottomBar(UIElement element) => BottomBarHost.Children.Add(element);

    /// <summary>切换沉浸模式：隐藏/显示顶部工具栏和底部控制栏。</summary>
    public void SetImmersive(bool immersive)
    {
        _isImmersive = immersive;
        TopToolbar.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        BottomBar.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>当前是否处于沉浸模式。</summary>
    public bool IsImmersive => _isImmersive;

    private void Back_Click(object sender, RoutedEventArgs e)
        => BackRequested?.Invoke(this, EventArgs.Empty);
}
