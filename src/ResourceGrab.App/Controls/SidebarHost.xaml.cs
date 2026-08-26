using System.Windows;
using System.Windows.Controls;

namespace ResourceGrab.App.Controls;

/// <summary>右侧边栏面板必须实现的接口。</summary>
public interface ISidebarPanel
{
    string PanelId { get; }
    string Title { get; }
    object? Icon { get; }
    FrameworkElement Content { get; }
}

/// <summary>
/// 右侧边栏受控插槽。统一管理标题、图标、关闭按钮和内容区域，
/// 页面不再直接修改主窗口布局。
/// </summary>
public partial class SidebarHost : UserControl
{
    /// <summary>用户点击关闭按钮时触发。</summary>
    public event EventHandler? SidebarCloseRequested;

    public SidebarHost()
    {
        InitializeComponent();
    }

    /// <summary>设置侧栏显示的面板内容。</summary>
    public void Show(ISidebarPanel panel)
    {
        HeaderText.Text = panel.Title;
        HeaderIcon.Content = null;
        BodyHost.Content = panel.Content;
        PrimaryActionButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>设置自定义头部内容和主体。</summary>
    public void Show(string title, object? icon, FrameworkElement body)
    {
        HeaderText.Text = title;
        HeaderIcon.Content = icon;
        BodyHost.Content = body;
        PrimaryActionButton.Visibility = Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SidebarCloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
