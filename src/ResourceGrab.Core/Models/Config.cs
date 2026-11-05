using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Models;

public class Config
{
    [JsonPropertyName("apiDomain")] public string ApiDomain { get; set; } = "";

    [JsonPropertyName("apiDomains")] public List<string> ApiDomains { get; set; } = new();
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    [JsonPropertyName("downloadDir")] public string DownloadDir { get; set; } = "";
    [JsonPropertyName("downloadFormat")] public DownloadFormat DownloadFormat { get; set; } = DownloadFormat.Jpeg;
    [JsonPropertyName("localDirs")] public List<string> LocalDirs { get; set; } = new();
    [JsonPropertyName("titleTranslate")] public TitleTranslateOptions TitleTranslate { get; set; } = new();
    [JsonPropertyName("readerScrollSpeed")] public double ReaderScrollSpeed { get; set; } = 1.0;
    [JsonPropertyName("videoScraping")] public VideoScrapeSettings? VideoScraping { get; set; }

    [JsonPropertyName("mangaSettings")] public MangaSettings Manga { get; set; } = new();
    [JsonPropertyName("videoPlayback")] public VideoPlaybackSettings VideoPlayback { get; set; } = new();
    [JsonPropertyName("generalSettings")] public GeneralSettings General { get; set; } = new();

    /// <summary>短剧页设置（下载目录 / 线路 / 并发）。null 表示用户从未打开过短剧设置。</summary>
    [JsonPropertyName("shortDrama")] public ShortDramaSettings? ShortDrama { get; set; }

    /// <summary>在线小说（爱丽丝书屋）设置：18+ 确认、开关与专用代理。</summary>
    [JsonPropertyName("novelOnline")] public NovelOnlineSettings? NovelOnline { get; set; }
}

/// <summary>
/// 在线小说源设置。<see cref="Enabled"/> 控制整组入口是否显示；
/// <see cref="Proxy"/> 留空表示跟随系统代理。
/// </summary>
public sealed class NovelOnlineSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    [JsonPropertyName("proxy")] public string Proxy { get; set; } = "";

    public bool IsProxyConfigured => !string.IsNullOrWhiteSpace(Proxy);
}

public class TitleTranslateOptions
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "gpt-4o-mini";
}

public enum ComicReadingDirection
{
    LeftToRight,
    RightToLeft,
    Vertical
}

public enum ComicPageFitMode
{
    FitWidth,
    FitHeight,
    OriginalSize
}

public sealed class MangaSettings
{
    [JsonPropertyName("readingDirection")] public ComicReadingDirection ReadingDirection { get; set; } = ComicReadingDirection.RightToLeft;
    [JsonPropertyName("pageFitMode")] public ComicPageFitMode PageFitMode { get; set; } = ComicPageFitMode.FitWidth;
    [JsonPropertyName("doublePageSpread")] public bool DoublePageSpread { get; set; }
    [JsonPropertyName("preloadPageCount")] public int PreloadPageCount { get; set; } = 3;
    [JsonPropertyName("directArchiveRead")] public bool DirectArchiveRead { get; set; } = true;
    [JsonPropertyName("extractCover")] public bool ExtractCover { get; set; } = true;
}

public sealed class VideoPlaybackSettings
{
    [JsonPropertyName("hardwareDecode")] public bool HardwareDecode { get; set; } = true;
    [JsonPropertyName("rememberProgress")] public bool RememberProgress { get; set; } = true;
    [JsonPropertyName("autoPlayNext")] public bool AutoPlayNext { get; set; }
    [JsonPropertyName("defaultPlaybackRate")] public double DefaultPlaybackRate { get; set; } = 1.0;
    [JsonPropertyName("seekStepSeconds")] public int SeekStepSeconds { get; set; } = 10;
    [JsonPropertyName("loadExternalSubtitles")] public bool LoadExternalSubtitles { get; set; } = true;
    [JsonPropertyName("preferredSubtitleLanguage")] public string PreferredSubtitleLanguage { get; set; } = "zh-CN";

    /// <summary>在线播放音量（0-100），跨会话记住。</summary>
    [JsonPropertyName("volume")] public int Volume { get; set; } = 100;

    /// <summary>在线播放是否静音，跨会话记住。</summary>
    [JsonPropertyName("muted")] public bool Muted { get; set; }
}

public sealed class GeneralSettings
{
    [JsonPropertyName("interfaceLanguage")] public string InterfaceLanguage { get; set; } = "zh-CN";
    [JsonPropertyName("theme")] public string Theme { get; set; } = "dark";
    [JsonPropertyName("mediaLibraryDir")] public string MediaLibraryDir { get; set; } = "";
    [JsonPropertyName("startupScan")] public bool StartupScan { get; set; } = true;
    [JsonPropertyName("cacheLimitMb")] public int CacheLimitMb { get; set; } = 1024;
    [JsonPropertyName("closeToTray")] public bool CloseToTray { get; set; }
}

/// <summary>
/// 短剧页设置。
/// <para>
/// <see cref="DownloadDir"/> 留空表示用默认目录（<see cref="AppPaths.ShortDramaDownloadDir"/>，即 config\短剧下载）——
/// 保持"便携"的默认体验，但又允许用户把它挪到容量更大的盘。
/// </para>
/// </summary>
public sealed class ShortDramaSettings
{
    /// <summary>下载根目录；留空 = config\短剧下载。</summary>
    [JsonPropertyName("downloadDir")] public string DownloadDir { get; set; } = "";

    /// <summary>线路基址（如 https://xiguaai.net）；留空 = 内置默认主站。</summary>
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "";

    /// <summary>同时下载的集数上限（1–4）。短剧单集 30–70 MB，默认 2 以免把带宽吃满。</summary>
    [JsonPropertyName("downloadConcurrency")] public int DownloadConcurrency { get; set; } = 2;

    /// <summary>自动下载新集（追更）开关；默认关，P4 功能预留。</summary>
    [JsonPropertyName("autoDownloadNewEpisodes")] public bool AutoDownloadNewEpisodes { get; set; }

    /// <summary>解析生效的下载根目录（空/非法回退默认目录）。</summary>
    public string ResolveDownloadDir() => ResolveDownloadDir(DownloadDir);

    public static string ResolveDownloadDir(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var full = Path.GetFullPath(configured.Trim());
                if (!string.IsNullOrEmpty(full)) return full;
            }
            catch { /* 非法路径回退默认 */ }
        }
        return AppPaths.ShortDramaDownloadDir;
    }
}
