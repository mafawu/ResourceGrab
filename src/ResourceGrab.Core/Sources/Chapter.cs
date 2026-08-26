namespace ResourceGrab.Core.Sources;

/// <summary>章节（画廊/单册在无章节站点中也建模为单个 Chapter）。</summary>
public class Chapter
{
    /// <summary>站点侧章节 id（如禁漫的系列 id）。</summary>
    public string Id { get; init; } = "";

    /// <summary>章节标题（如 "第1话"），同时用于下载目录命名。</summary>
    public string Title { get; init; } = "";

    /// <summary>所属漫画 id。</summary>
    public string ComicId { get; init; } = "";

    /// <summary>所属漫画标题（过滤非法字符后），用于下载目录命名。</summary>
    public string ComicTitle { get; init; } = "";

    /// <summary>所属内容源 id（如 "jm"、"wnacg"），下载引擎按此解析源与限流配置。</summary>
    public string SourceId { get; init; } = "";

    /// <summary>站点 id 为纯数字时的数值形式（如禁漫）；非数字站点为 null。</summary>
    public long? NumericId { get; init; }

    /// <summary>章节排序值（站点顺序；无顺序概念的源默认 0）。</summary>
    public double OrderValue { get; init; }
}
