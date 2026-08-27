namespace ResourceGrab.Core;

public static class AppPaths
{
    public static string DataDirName { get; set; } = "config";
    public static string AppDataDir =>
        Path.Combine(AppContext.BaseDirectory, DataDirName);
    public static string LegacyDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "jmcomic-downloader");
    public static string ConfigPath => Path.Combine(AppDataDir, "config.json");
    public static string LocalLibraryCachePath => Path.Combine(AppDataDir, "local-library-cache.json");
    public static string DownloadHistoryPath => Path.Combine(AppDataDir, "download-history.json");
    public static string NovelHistoryPath => Path.Combine(AppDataDir, "novel-history.json");
    public static string NovelIndexPath => Path.Combine(AppDataDir, "novel-index.json");
    public static string NovelReaderSettingsPath => Path.Combine(AppDataDir, "novel-reader-settings.json");
    public static string ComicUserDataPath => Path.Combine(AppDataDir, "comic-user-data.json");
    public static string OnlinePageCountCachePath => Path.Combine(AppDataDir, "online-page-count-cache.json");
    public static string VideoFoldersPath => Path.Combine(AppDataDir, "video-folders.json");
    public static string VideoLibraryPath => Path.Combine(AppDataDir, "video-library.json");
    public static string VideoArtworkDir => Path.Combine(AppDataDir, "artwork", "videos");
    public static string VideoScrapeMetadataPath => Path.Combine(AppDataDir, "video-scrape-metadata.json");
    public static string VideoSourceCacheDir => Path.Combine(AppDataDir, "video-source-cache");
    public static string VideoSidebarCountsPath => Path.Combine(AppDataDir, "video-sidebar-counts.json");
    public static string VideoResourcesDir => Path.Combine(AppDataDir, "video-resources");
    public static string MetadataBackfillLogPath => Path.Combine(AppDataDir, "backfill-metadata.log");
    public static string LogsDir => Path.Combine(AppDataDir, "logs");
    public static void MigrateLegacyData()
    {
        try
        {
            if (!Directory.Exists(LegacyDataDir)) return;
            Directory.CreateDirectory(AppDataDir);
            foreach (var name in new[] { "config.json", "local-library-cache.json", "reading-progress.json", "theme.json", "novel-history.json", "novel-reader-settings.json" })
            {
                var source = Path.Combine(LegacyDataDir, name);
                var target = Path.Combine(AppDataDir, name);
                if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target);
            }
        }
        catch { }
    }
}
