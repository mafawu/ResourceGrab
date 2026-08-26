using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ResourceGrab.App.Common;

/// <summary>
/// 无边框窗口圆角工具：通过 DWM 让窗口四角被系统裁剪为圆角（Windows 11 生效，Win10 静默忽略）。
/// <para>
/// 必须配合 WindowChrome（AllowsTransparency=False）使用：AllowsTransparency=True 会把窗口
/// 变成分层窗口，WPF 每帧把 DirectX 渲染结果整幅像素拷贝到 GDI 表面（分辨率越高拷贝越多），
/// 且整个窗口退化为软件渲染路径。
/// </para>
/// </summary>
internal static class DwmWindowCorner
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int DwmwcpDoNotRound = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    /// <summary>按当前窗口状态设置圆角（最大化时取消圆角，避免四角露桌面）。</summary>
    public static void Apply(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source)
        {
            return;
        }
        var preference = window.WindowState == WindowState.Maximized ? DwmwcpDoNotRound : DwmwcpRound;
        _ = DwmSetWindowAttribute(source.Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }
}
