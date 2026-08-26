using ResourceGrab.Core.Errors;

namespace ResourceGrab.App.Services;

public enum ToastKind
{
    Info,
    Success,
    Error,
}

/// <summary>轻量通知条（Snackbar）服务：由 MainWindow 注册宿主。</summary>
public static class ToastService
{
    public static Action<string, ToastKind>? ShowHandler { get; set; }

    public static void Show(string message, ToastKind kind = ToastKind.Info)
        => ShowHandler?.Invoke(message, kind);

    /// <summary>显示异常的友好提示（经 JmErrorClassifier 分类，避免展示原始异常串）。</summary>
    public static void ShowError(Exception ex, string prefix = "")
        => Show(prefix + JmErrorClassifier.Classify(ex).Message, ToastKind.Error);
}
