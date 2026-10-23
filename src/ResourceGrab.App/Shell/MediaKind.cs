namespace ResourceGrab.App.Shell;

/// <summary>四种媒体内容的第一级分类。与 Core 的 ResourceKind 一一对应，用于返回栈隔离。</summary>
public enum MediaKind
{
    Manga,
    Video,
    Novel,
    Photobook
}
