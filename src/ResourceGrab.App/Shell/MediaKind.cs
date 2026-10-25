namespace ResourceGrab.App.Shell;

/// <summary>四种媒体内容的第一级分类。与 Core 的 ResourceKind 一一对应，用于返回栈隔离。</summary>
public enum MediaKind
{
    Manga,
    Video,
    Novel,
    Photobook,

    /// <summary>跨媒体的下载页：独立返回栈，不占用各媒体自己的「最近路由」。</summary>
    Download
}
