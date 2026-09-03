using System.Windows;
using System.Windows.Controls;
using ResourceGrab.App.Services;
using ResourceGrab.App.ViewModels;

namespace ResourceGrab.App.Common;

/// <summary>剪贴板写入兜底：系统剪贴板可能被其他进程短暂占用而抛出异常。</summary>
public static class ClipboardHelper
{
    /// <summary>复制文本并弹出提示；label 形如「标题」「简介」「评论」。</summary>
    public static void CopyWithToast(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ToastService.Show($"{label}为空，无法复制", ToastKind.Info);
            return;
        }
        try
        {
            Clipboard.SetText(text);
            ToastService.Show($"已复制{label}", ToastKind.Success);
        }
        catch (Exception ex)
        {
            ToastService.ShowError(ex);
        }
    }

    /// <summary>右键菜单「复制评论」：取菜单目标 Border 绑定的 CommentViewModel，复制其正文。</summary>
    public static void CopyCommentFromMenu(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: CommentViewModel vm } } })
        {
            CopyWithToast(vm.Content, "评论");
        }
    }
}
