namespace ResourceGrab.Core.Sources.Copymanga;

/// <summary>拷贝漫画站点常量：域名、UA、版本、并发配置（对齐 copymanga-downloader）。</summary>
public static class CopymangaConstants
{
    /// <summary>API 域名（copymanga-downloader 当前使用的默认域名）。</summary>
    public const string ApiDomain = "api.copy202601.com";

    /// <summary>API 请求的 User-Agent（对齐参考项目：COPY/3.0.0）。</summary>
    public const string UserAgent = "COPY/3.0.0";

    /// <summary>接口校验的版本号（对齐参考项目 2025.08.15）。</summary>
    public const string ApiVersion = "2025.08.15";

    /// <summary>搜索每页条数（站点固定 20）。</summary>
    public const int SearchPageSize = 20;

    /// <summary>章节列表每页条数（参考项目用 100）。</summary>
    public const int ChapterPageSize = 100;
}
