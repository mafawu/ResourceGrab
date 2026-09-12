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
