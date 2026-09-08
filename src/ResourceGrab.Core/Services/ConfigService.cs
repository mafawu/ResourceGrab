using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape;

namespace ResourceGrab.Core.Services;

/// <summary>
/// 配置文件读写服务。config.json 结构与原 Tauri 版保持一致，
/// 老用户可直接沿用已有配置。
/// </summary>
public class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>DPAPI 加密值的标识前缀（当前用户作用域）。</summary>
    private const string EncryptedPrefix = "DPAPI:v1:";

    private readonly string _configPath;

    /// <summary>加载到旧版明文凭据时置位，触发自动迁移为加密存储。</summary>
    private bool _hasLegacyPlaintext;

    public ConfigService(string configPath)
    {
        _configPath = configPath;
        Current = Load();
    }

    public Config Current { get; private set; }

    private Config Load()
    {
        var defaultConfig = new Config
        {
            DownloadDir = Path.Combine(AppPaths.AppDataDir, "漫画下载"),
            DownloadFormat = DownloadFormat.Jpeg,
            LocalDirs = new List<string> { Path.Combine(AppPaths.AppDataDir, "漫画下载") },
        };
        defaultConfig.General.MediaLibraryDir = defaultConfig.DownloadDir;

        if (!File.Exists(_configPath))
        {
            Save(defaultConfig);
            return defaultConfig;
        }
        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<Config>(json);
            if (config is null)
            {
                return defaultConfig;
            }
            if (string.IsNullOrEmpty(config.DownloadDir))
            {
                config.DownloadDir = defaultConfig.DownloadDir;
            }
            if (config.LocalDirs is null || config.LocalDirs.Count == 0)
            {
                config.LocalDirs = new List<string> { config.DownloadDir };
            }
            config.TitleTranslate ??= new TitleTranslateOptions();
            config.ReaderScrollSpeed = NormalizeScrollSpeed(config.ReaderScrollSpeed);
            config.VideoScraping ??= new VideoScrapeSettings();
            if (string.IsNullOrWhiteSpace(config.VideoScraping.VideoDownloadDir))
            {
                config.VideoScraping.VideoDownloadDir = Path.Combine(AppPaths.AppDataDir, "视频下载");
            }
            config.VideoScraping.Advanced ??= new VideoScrapeAdvancedSettings();
            config.Manga ??= new MangaSettings();
            config.VideoPlayback = NormalizeVideoPlayback(config.VideoPlayback);
            config.General = NormalizeGeneral(config.General, config.DownloadDir);

            config.Password = Decrypt(config.Password);
            config.TitleTranslate.ApiKey = Decrypt(config.TitleTranslate.ApiKey);
            if (_hasLegacyPlaintext)
            {
                // 旧版明文凭据：首次加载后自动迁移为加密存储，避免明文长期落盘。
                try
                {
                    Save(config);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"凭据加密迁移失败: {ex.Message}");
                }
            }
            return config;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"读取配置失败，使用默认配置: {ex.Message}");
            return defaultConfig;
        }
    }

    public void Save()
    {
        Save(Current);
    }

    public void Save(Config config)
    {
        config.ReaderScrollSpeed = NormalizeScrollSpeed(config.ReaderScrollSpeed);
        config.Manga ??= new MangaSettings();
        config.VideoPlayback = NormalizeVideoPlayback(config.VideoPlayback);
        config.General = NormalizeGeneral(config.General, config.DownloadDir);

        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        var translate = config.TitleTranslate ?? new TitleTranslateOptions();
        var scraping = config.VideoScraping ?? new VideoScrapeSettings();
        var manga = config.Manga;
        var playback = config.VideoPlayback;
        var general = config.General;
        var persisted = new Config
        {
            ApiDomain = config.ApiDomain,
            ApiDomains = config.ApiDomains,
            Username = config.Username,
            Password = Encrypt(config.Password),
            DownloadDir = config.DownloadDir,
            DownloadFormat = config.DownloadFormat,
            LocalDirs = config.LocalDirs,
            ReaderScrollSpeed = config.ReaderScrollSpeed,
            VideoScraping = new VideoScrapeSettings
            {
                Enabled = scraping.Enabled,
                Proxy = scraping.Proxy,
                TimeoutSeconds = scraping.TimeoutSeconds,
                Retry = scraping.Retry,
                Concurrency = scraping.Concurrency,
                RequestIntervalMs = scraping.RequestIntervalMs,
                AutoScrapeNewFiles = scraping.AutoScrapeNewFiles,
                VideoDownloadDir = scraping.VideoDownloadDir,
                DownloadExtraFanart = scraping.DownloadExtraFanart,
                WriteNfo = scraping.WriteNfo,
                JavBusBaseUrl = scraping.JavBusBaseUrl,
                JavBusCookie = scraping.JavBusCookie,
                JavDbBaseUrl = scraping.JavDbBaseUrl,
                JavDbCookie = scraping.JavDbCookie,
                AiravEnabled = scraping.AiravEnabled,
                Advanced = scraping.Advanced,
            },
            TitleTranslate = new TitleTranslateOptions
            {
                Enabled = translate.Enabled,
                BaseUrl = translate.BaseUrl,
                ApiKey = Encrypt(translate.ApiKey),
                Model = translate.Model,
            },
            Manga = new MangaSettings
            {
                ReadingDirection = manga.ReadingDirection,
                PageFitMode = manga.PageFitMode,
                DoublePageSpread = manga.DoublePageSpread,
                PreloadPageCount = Math.Clamp(manga.PreloadPageCount, 0, 10),
                DirectArchiveRead = manga.DirectArchiveRead,
                ExtractCover = manga.ExtractCover,
            },
            VideoPlayback = playback,
            General = general,
        };
        // 临时文件 + 原子替换：崩溃时序不当不会写坏唯一一份配置
        var temp = _configPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(persisted, JsonOptions));
        File.Move(temp, _configPath, true);
    }

    public static double NormalizeScrollSpeed(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0)
        {
            return 1.0;
        }
        return Math.Clamp(v, 0.2, 5.0);
    }

    private static VideoPlaybackSettings NormalizeVideoPlayback(VideoPlaybackSettings? settings)
    {
        settings ??= new VideoPlaybackSettings();
        var rate = settings.DefaultPlaybackRate;
        settings.DefaultPlaybackRate = double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0
            ? 1.0
            : Math.Clamp(rate, 0.25, 4.0);
        settings.SeekStepSeconds = Math.Clamp(settings.SeekStepSeconds, 5, 60);
        return settings;
    }

    private static GeneralSettings NormalizeGeneral(GeneralSettings? settings, string downloadDir)
    {
        settings ??= new GeneralSettings();
        if (string.IsNullOrWhiteSpace(settings.MediaLibraryDir))
        {
            settings.MediaLibraryDir = downloadDir;
        }
        settings.CacheLimitMb = Math.Clamp(settings.CacheLimitMb, 128, 8192);
        if (settings.InterfaceLanguage != "en-US")
        {
            settings.InterfaceLanguage = "zh-CN";
        }
        if (settings.Theme is not ("light" or "dark"))
        {
            settings.Theme = "dark";
        }
        return settings;
    }

    /// <summary>用 Windows DPAPI（当前用户作用域）加密；非 Windows 或失败时退回明文。</summary>
    private static string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return "";
        }
        if (!OperatingSystem.IsWindows())
        {
            return plaintext;
        }
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);
            return EncryptedPrefix + Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"凭据加密失败，退回明文保存: {ex.Message}");
            return plaintext;
        }
    }

    /// <summary>解密 DPAPI 值；旧版明文原样返回并标记迁移，无法解密时返回空串。</summary>
    private string Decrypt(string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return "";
        }
        if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            _hasLegacyPlaintext = true;
            return stored;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("DPAPI 仅在 Windows 可用，凭据无法解密");
            return "";
        }
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(stored[EncryptedPrefix.Length..]),
                null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"凭据解密失败（可能来自其他 Windows 用户或已损坏）: {ex.Message}");
            return "";
        }
    }
}
