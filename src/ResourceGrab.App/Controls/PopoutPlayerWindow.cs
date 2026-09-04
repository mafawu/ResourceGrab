using System.Windows;
using System.Windows.Media;

namespace ResourceGrab.App.Controls;

/// <summary>
/// 独立放大播放窗口：在线详情"⛶"按钮打开，黑底大屏播放同一流。
/// 与主界面播放器是**两个实例**：LibVLCSharp.WPF 的 VideoView（HwndHost + ForegroundWindow）
/// 不支持跨窗口迁移（Hwnd 重建后不重绑、覆盖层窗口对不上位置），
/// 所以改用"接力续播"——打开/关闭小窗时把播放位置与播放/暂停状态交接给对方，
/// 从同一位置继续播，进度与状态保持同步。
/// </summary>
public sealed class PopoutPlayerWindow : Window
{
    private readonly OnlineVideoPreviewPlayer _player;

    public PopoutPlayerWindow(string title, string streamUrl, string referer, string posterUrl,
        long startPositionMs, bool startPaused, Action onClosed)
    {
        Title = title;
        Width = 960;
        Height = 640;
        MinWidth = 480;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current.MainWindow;
        Background = Brushes.Black;
        _player = new OnlineVideoPreviewPlayer
        {
            StreamUrl = streamUrl,
            Referer = referer,
            PosterUrl = posterUrl,
            ShowPlayButton = !string.IsNullOrEmpty(streamUrl),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Content = _player;
        Loaded += (_, _) => _player.PlayFrom(startPositionMs, startPaused);
        Closed += (_, _) => onClosed();
    }

    /// <summary>当前播放位置（毫秒），供关闭时交接回主界面。</summary>
    public long CurrentPositionMs => _player.CurrentPositionMs;

    /// <summary>是否正在播放。</summary>
    public bool IsPlaying => _player.IsPlaying;

    /// <summary>是否处于暂停态。</summary>
    public bool IsPaused => _player.IsPaused;
}
