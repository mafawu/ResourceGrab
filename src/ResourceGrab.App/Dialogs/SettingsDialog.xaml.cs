using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ResourceGrab.App.Common;
using ResourceGrab.App.Themes;
using ResourceGrab.App.Views;
using ResourceGrab.Core;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services;

namespace ResourceGrab.App.Dialogs;

/// <summary>设置弹窗：按漫画 / 小说 / 视频 / 通用分组管理应用偏好。</summary>
public partial class SettingsDialog : Window
{
    private readonly ConfigService _configService;
    private readonly NovelReaderSettingsService _novelSettings;
    private readonly bool _initialThemeIsDark;

    public SettingsDialog()
    {
        InitializeComponent();
        _configService = App.Services.GetRequiredService<ConfigService>();
        _novelSettings = App.Services.GetRequiredService<NovelReaderSettingsService>();
        _initialThemeIsDark = ThemeManager.IsDark;

        LoadFromConfig(_configService.Current);
        RefreshSliderValueTexts();
        SelectPage(MangaNavItem);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DwmWindowCorner.Apply(this);
    }

    private void LoadFromConfig(Config config)
    {
        DownloadDirBox.Text = config.DownloadDir;
        var domains = config.ApiDomains.Count > 0
            ? config.ApiDomains
            : string.IsNullOrWhiteSpace(config.ApiDomain) ? [] : new List<string> { config.ApiDomain };
        ApiDomainsBox.Text = string.Join(", ", domains);
        SelectComboByTag(FormatBox, config.DownloadFormat.ToString());

        ScrollSpeedSlider.Value = ConfigService.NormalizeScrollSpeed(config.ReaderScrollSpeed);

        var manga = config.Manga ?? new MangaSettings();
        DirectionRtlRadio.IsChecked = manga.ReadingDirection == ComicReadingDirection.RightToLeft;
        DirectionLtrRadio.IsChecked = manga.ReadingDirection == ComicReadingDirection.LeftToRight;
        DirectionVerticalRadio.IsChecked = manga.ReadingDirection == ComicReadingDirection.Vertical;
        SelectComboByTag(PageFitBox, manga.PageFitMode.ToString());
        DoublePageBox.IsChecked = manga.DoublePageSpread;
        PreloadSlider.Value = Math.Clamp(manga.PreloadPageCount, 0, 10);
        DirectArchiveBox.IsChecked = manga.DirectArchiveRead;
        ExtractCoverBox.IsChecked = manga.ExtractCover;

        TranslateEnabledBox.IsChecked = config.TitleTranslate.Enabled;
        TranslateBaseUrlBox.Text = config.TitleTranslate.BaseUrl;
        TranslateApiKeyBox.Text = config.TitleTranslate.ApiKey;
        TranslateModelBox.Text = config.TitleTranslate.Model;

        var novel = _novelSettings.Current;
        FontFamilyBox.Text = novel.FontFamily;
        NovelFontSizeSlider.Value = novel.FontSize;
        LineHeightSlider.Value = novel.LineHeight;
        IndentSlider.Value = novel.ParagraphIndentEm;
        NovelScrollModeRadio.IsChecked = novel.IsScrollMode;
        NovelPageModeRadio.IsChecked = !novel.IsScrollMode;
        AutoDetectEncodingBox.IsChecked = novel.AutoDetectEncoding;
        ChapterPatternBox.Text = novel.ChapterPattern;

        var playback = config.VideoPlayback ?? new VideoPlaybackSettings();
        HardwareDecodeBox.IsChecked = playback.HardwareDecode;
        RememberProgressBox.IsChecked = playback.RememberProgress;
        AutoPlayNextBox.IsChecked = playback.AutoPlayNext;
        PlaybackRateSlider.Value = playback.DefaultPlaybackRate;
        SelectComboByTag(SeekStepBox, playback.SeekStepSeconds.ToString());
        LoadSubtitleBox.IsChecked = playback.LoadExternalSubtitles;
        SelectComboByTag(SubtitleLanguageBox, playback.PreferredSubtitleLanguage);

        var scraping = config.VideoScraping ?? new VideoScrapeSettings();
        ScrapeEnabledBox.IsChecked = scraping.Enabled;
        AutoScrapeNewFilesBox.IsChecked = scraping.AutoScrapeNewFiles;
        WriteNfoBox.IsChecked = scraping.WriteNfo;
        AiravEnabledBox.IsChecked = scraping.AiravEnabled;
        ScrapeProxyBox.Text = scraping.Proxy;
        JavBusUrlBox.Text = scraping.JavBusBaseUrl;
        JavDbUrlBox.Text = scraping.JavDbBaseUrl;
        JavBusCookieBox.Text = scraping.JavBusCookie;

        // 高级刮削设置
        ScrapeTimeoutBox.Text = scraping.TimeoutSeconds.ToString();
        ScrapeRetryBox.Text = scraping.Retry.ToString();
        ScrapeConcurrencyBox.Text = scraping.Concurrency.ToString();
        ScrapeIntervalBox.Text = scraping.RequestIntervalMs.ToString();
        CacheEnabledBox.IsChecked = true;
        PopulateSourceToggles(scraping);
        PopulateContentRoutes(scraping);

        var general = config.General ?? new GeneralSettings();
        SelectComboByTag(LanguageBox, general.InterfaceLanguage);
        DarkThemeRadio.IsChecked = ThemeManager.IsDark;
        LightThemeRadio.IsChecked = !ThemeManager.IsDark;
        MediaLibraryDirBox.Text = string.IsNullOrWhiteSpace(general.MediaLibraryDir)
            ? config.DownloadDir
            : general.MediaLibraryDir;
        StartupScanBox.IsChecked = general.StartupScan;
        CacheLimitSlider.Value = Math.Clamp(general.CacheLimitMb, 128, 8192);
        CloseToTrayBox.IsChecked = general.CloseToTray;
    }

    private static void SelectComboByTag(ComboBox comboBox, string tag)
    {
        foreach (ComboBoxItem item in comboBox.Items)
        {
            if (string.Equals((string?)item.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
        comboBox.SelectedIndex = 0;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (MangaPage is null || NovelPage is null || VideoPage is null || GeneralPage is null)
        {
            return;
        }
        if (sender is RadioButton nav)
        {
            SelectPage(nav);
        }
    }

    private void SelectPage(RadioButton nav)
    {
        MangaPage.Visibility = nav == MangaNavItem ? Visibility.Visible : Visibility.Collapsed;
        NovelPage.Visibility = nav == NovelNavItem ? Visibility.Visible : Visibility.Collapsed;
        VideoPage.Visibility = nav == VideoNavItem ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = nav == GeneralNavItem ? Visibility.Visible : Visibility.Collapsed;
    }

    // XAML 解析期间设置 Minimum/Value 就会触发 ValueChanged，此时标签元素尚未创建，必须判空
    private void ScrollSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScrollSpeedValueText != null) ScrollSpeedValueText.Text = $"{e.NewValue:0.0}x";
    }

    private void PreloadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PreloadValueText != null) PreloadValueText.Text = e.NewValue.ToString("0");
    }

    private void NovelFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (NovelFontSizeText != null) NovelFontSizeText.Text = $"{e.NewValue:0.#} pt";
    }

    private void LineHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LineHeightText != null) LineHeightText.Text = $"{e.NewValue:0.00}";
    }

    private void IndentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IndentText != null) IndentText.Text = $"{e.NewValue:0.#} em";
    }

    private void PlaybackRateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PlaybackRateText != null) PlaybackRateText.Text = $"{e.NewValue:0.00}x";
    }

    private void CacheLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CacheLimitText != null) CacheLimitText.Text = $"{e.NewValue / 1024.0:0.#} GB";
    }

    private void RefreshSliderValueTexts()
    {
        ScrollSpeedValueText.Text = $"{ScrollSpeedSlider.Value:0.0}x";
        PreloadValueText.Text = PreloadSlider.Value.ToString("0");
        NovelFontSizeText.Text = $"{NovelFontSizeSlider.Value:0.#} pt";
        LineHeightText.Text = $"{LineHeightSlider.Value:0.00}";
        IndentText.Text = $"{IndentSlider.Value:0.#} em";
        PlaybackRateText.Text = $"{PlaybackRateSlider.Value:0.00}x";
        CacheLimitText.Text = $"{CacheLimitSlider.Value / 1024.0:0.#} GB";
    }

    private void BrowseDownloadDir_Click(object sender, RoutedEventArgs e)
        => BrowseFolder(DownloadDirBox, "选择漫画下载目录");

    private void BrowseMediaLibrary_Click(object sender, RoutedEventArgs e)
        => BrowseFolder(MediaLibraryDirBox, "选择媒体库目录");

    private static void BrowseFolder(TextBox target, string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (Directory.Exists(target.Text))
        {
            dialog.InitialDirectory = target.Text;
        }
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            target.Text = dialog.FolderName;
        }
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = CreateDefaultConfig();
        LoadFromConfig(defaults);
        ShowError("已载入默认值，点击「保存」后生效。");
    }

    private static Config CreateDefaultConfig()
    {
        var downloadDir = Path.Combine(AppPaths.AppDataDir, "漫画下载");
        return new Config
        {
            DownloadDir = downloadDir,
            ApiDomains = [],
            DownloadFormat = DownloadFormat.Jpeg,
            ReaderScrollSpeed = 1.0,
            Manga = new MangaSettings(),
            TitleTranslate = new TitleTranslateOptions(),
            VideoPlayback = new VideoPlaybackSettings(),
            VideoScraping = new VideoScrapeSettings(),
            General = new GeneralSettings { MediaLibraryDir = downloadDir },
        };
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DownloadDirBox.Text))
        {
            ShowError("下载目录不能为空。");
            return;
        }
        if (string.IsNullOrWhiteSpace(MediaLibraryDirBox.Text))
        {
            ShowError("媒体库目录不能为空。");
            return;
        }
        if (!IsValidRegex(ChapterPatternBox.Text))
        {
            ShowError("章节切分规则不是有效的正则表达式。");
            return;
        }

        var config = _configService.Current;
        config.DownloadDir = Path.GetFullPath(DownloadDirBox.Text.Trim());
        config.ApiDomains = SplitDomains(ApiDomainsBox.Text);
        config.ApiDomain = "";
        if (FormatBox.SelectedItem is ComboBoxItem { Tag: string formatTag }
            && Enum.TryParse<DownloadFormat>(formatTag, true, out var format))
        {
            config.DownloadFormat = format;
        }

        config.ReaderScrollSpeed = ConfigService.NormalizeScrollSpeed(ScrollSpeedSlider.Value);
        config.Manga ??= new MangaSettings();
        config.Manga.ReadingDirection =
            DirectionLtrRadio.IsChecked == true ? ComicReadingDirection.LeftToRight :
            DirectionVerticalRadio.IsChecked == true ? ComicReadingDirection.Vertical :
            ComicReadingDirection.RightToLeft;
        if (PageFitBox.SelectedItem is ComboBoxItem { Tag: string fitTag }
            && Enum.TryParse<ComicPageFitMode>(fitTag, true, out var pageFit))
        {
            config.Manga.PageFitMode = pageFit;
        }
        config.Manga.DoublePageSpread = DoublePageBox.IsChecked == true;
        config.Manga.PreloadPageCount = (int)Math.Round(PreloadSlider.Value);
        config.Manga.DirectArchiveRead = DirectArchiveBox.IsChecked == true;
        config.Manga.ExtractCover = ExtractCoverBox.IsChecked == true;

        config.TitleTranslate.Enabled = TranslateEnabledBox.IsChecked == true;
        config.TitleTranslate.BaseUrl = TranslateBaseUrlBox.Text.Trim();
        config.TitleTranslate.ApiKey = TranslateApiKeyBox.Text.Trim();
        config.TitleTranslate.Model = TranslateModelBox.Text.Trim();

        _novelSettings.Update(novel =>
        {
            novel.FontFamily = string.IsNullOrWhiteSpace(FontFamilyBox.Text) ? "Microsoft YaHei UI" : FontFamilyBox.Text.Trim();
            novel.FontSize = NovelFontSizeSlider.Value;
            novel.LineHeight = LineHeightSlider.Value;
            novel.ParagraphIndentEm = IndentSlider.Value;
            novel.IsScrollMode = NovelScrollModeRadio.IsChecked == true;
            novel.AutoDetectEncoding = AutoDetectEncodingBox.IsChecked == true;
            novel.ChapterPattern = ChapterPatternBox.Text.Trim();
        });

        config.VideoPlayback ??= new VideoPlaybackSettings();
        config.VideoPlayback.HardwareDecode = HardwareDecodeBox.IsChecked == true;
        config.VideoPlayback.RememberProgress = RememberProgressBox.IsChecked == true;
        config.VideoPlayback.AutoPlayNext = AutoPlayNextBox.IsChecked == true;
        config.VideoPlayback.DefaultPlaybackRate = PlaybackRateSlider.Value;
        if (SeekStepBox.SelectedItem is ComboBoxItem { Tag: string seekTag } && int.TryParse(seekTag, out var seekStep))
        {
            config.VideoPlayback.SeekStepSeconds = seekStep;
        }
        config.VideoPlayback.LoadExternalSubtitles = LoadSubtitleBox.IsChecked == true;
        if (SubtitleLanguageBox.SelectedItem is ComboBoxItem { Tag: string language })
        {
            config.VideoPlayback.PreferredSubtitleLanguage = language;
        }

        var scraping = config.VideoScraping ??= new VideoScrapeSettings();
        scraping.Enabled = ScrapeEnabledBox.IsChecked == true;
        scraping.AutoScrapeNewFiles = AutoScrapeNewFilesBox.IsChecked == true;
        scraping.WriteNfo = WriteNfoBox.IsChecked == true;
        scraping.AiravEnabled = AiravEnabledBox.IsChecked == true;
        scraping.Proxy = ScrapeProxyBox.Text.Trim();
        scraping.JavBusBaseUrl = string.IsNullOrWhiteSpace(JavBusUrlBox.Text) ? "https://www.javbus.com" : JavBusUrlBox.Text.Trim();
        scraping.JavDbBaseUrl = string.IsNullOrWhiteSpace(JavDbUrlBox.Text) ? "https://javdb.com" : JavDbUrlBox.Text.Trim();
        scraping.JavBusCookie = JavBusCookieBox.Text.Trim();

        // 高级刮削设置保存
        if (int.TryParse(ScrapeTimeoutBox.Text, out var timeout)) scraping.TimeoutSeconds = Math.Clamp(timeout, 3, 120);
        if (int.TryParse(ScrapeRetryBox.Text, out var retry)) scraping.Retry = Math.Clamp(retry, 0, 10);
        if (int.TryParse(ScrapeConcurrencyBox.Text, out var concurrency)) scraping.Concurrency = Math.Clamp(concurrency, 1, 16);
        if (int.TryParse(ScrapeIntervalBox.Text, out var interval)) scraping.RequestIntervalMs = Math.Clamp(interval, 0, 10000);
        SaveSourceToggles(scraping);
        SaveContentRoutes(scraping);

        config.General ??= new GeneralSettings();
        if (LanguageBox.SelectedItem is ComboBoxItem { Tag: string uiLanguage })
        {
            config.General.InterfaceLanguage = uiLanguage;
        }
        var selectedDark = DarkThemeRadio.IsChecked == true;
        config.General.Theme = selectedDark ? "dark" : "light";
        config.General.MediaLibraryDir = Path.GetFullPath(MediaLibraryDirBox.Text.Trim());
        config.General.StartupScan = StartupScanBox.IsChecked == true;
        config.General.CacheLimitMb = (int)Math.Round(CacheLimitSlider.Value);
        config.General.CloseToTray = CloseToTrayBox.IsChecked == true;

        _configService.Save();
        ThemeManager.Apply(selectedDark);
        DialogResult = true;
    }

    private static List<string> SplitDomains(string input) => input
        .Split([',', '，', ';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ManageLocalDirs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LocalDirsDialog { Owner = this };
        if (dialog.ShowDialog() == true && Owner is MainWindow mw)
        {
            _ = mw.TriggerLocalRefreshAsync();
        }
    }

    private async void RefreshLocal_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "手动刷新将对全部本地目录进行全量重新扫描，目录较多时可能较慢。是否继续？",
            "全量重新扫描", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (Owner is MainWindow mw)
            {
                await mw.TriggerLocalRefreshAsync();
            }
            else
            {
                var localView = new LocalView();
                await localView.RequestRefreshAsync();
            }
        }
        catch (Exception ex)
        {
            ShowError($"刷新失败：{ex.Message}");
        }
    }

    private void ViewLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Directory.Exists(AppPaths.LogsDir) ? AppPaths.LogsDir : AppPaths.AppDataDir;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError($"打开日志目录失败：{ex.Message}");
        }
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var sizeBefore = GetCacheSizeBytes();
        var result = MessageBox.Show(this,
            $"将清理本地库索引、在线页数缓存和视频封面缓存，预计释放 {FormatBytes(sizeBefore)}。\n媒体文件、配置和阅读进度不会被删除。",
            "清理缓存", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        var clearedFiles = new List<string>();
        try
        {
            clearedFiles.AddIfNotNull(TryDeleteInsideAppData(AppPaths.LocalLibraryCachePath));
            clearedFiles.AddIfNotNull(TryDeleteInsideAppData(AppPaths.OnlinePageCountCachePath));
            clearedFiles.AddIfNotNull(TryDeleteInsideAppData(AppPaths.VideoArtworkDir));
            clearedFiles.AddIfNotNull(TryDeleteInsideDir(AppPaths.VideoSourceCacheDir));

            var freed = GetCacheSizeBytes() >= sizeBefore
                ? sizeBefore - GetCacheSizeBytes()
                : sizeBefore;
            MessageBox.Show(this, $"缓存已清理，释放约 {FormatBytes(freed)}。", "完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            foreach (var path in clearedFiles.Where(Directory.Exists)) Directory.CreateDirectory(path);
            ShowError($"清理失败：{ex.Message}");
        }
    }

    private static long GetCacheSizeBytes()
    {
        long total = 0;
        foreach (var path in new[] { AppPaths.LocalLibraryCachePath, AppPaths.OnlinePageCountCachePath, AppPaths.VideoArtworkDir })
        {
            if (File.Exists(path)) total += new FileInfo(path).Length;
            else if (Directory.Exists(path)) total += new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        return total;
    }

    private static string? TryDeleteInsideAppData(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return null;
        var appDataRoot = Path.GetFullPath(AppPaths.AppDataDir + Path.DirectorySeparatorChar);
        var fullTarget = Path.GetFullPath(path);
        if (!fullTarget.StartsWith(appDataRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("缓存路径不在应用数据目录内。");
        }
        if (File.Exists(path)) File.Delete(path);
        else Directory.Delete(path, recursive: true);
        return path;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB"
    };

    private void ClearVideoLibrary_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "确定要清空视频库吗？\r\n\r\n所有视频记录（含刮削结果、收藏、观看进度）将从库中移除，\r\n但磁盘上的视频文件不会被删除。",
            "清空视频库", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            App.Services.GetRequiredService<VideoLibraryService>().ClearAll();
            MessageBox.Show(this, "视频库已清空。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowError($"清空失败：{ex.Message}");
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
    // ===== 高级刮削设置辅助方法 =====

    private readonly Dictionary<string, CheckBox> _sourceCheckBoxes = new(StringComparer.OrdinalIgnoreCase);

    private void PopulateSourceToggles(VideoScrapeSettings scraping)
    {
        SourceToggleGrid.Children.Clear();
        _sourceCheckBoxes.Clear();
        var advanced = scraping.Advanced ?? new VideoScrapeAdvancedSettings();
        var allSourceIds = new[] {
            ("javbus", "JavBus"), ("javdb", "JavDB"), ("airav", "AirAv"), ("dmm", "DMM"),
            ("iqqtv", "IQQTV"), ("avsox", "Avsox"), ("freejavbt", "FreeJavBT"),
            ("fc2ppvdb", "FC2PPVDB"), ("fc2", "FC2"), ("fc2club", "FC2Club"),
            ("mgstage", "MGStage"), ("theporndb", "ThePornDB"), ("getchu", "Getchu"),
            ("official", "Official"), ("prestige", "Prestige"), ("r18dev", "R18Dev"),
            ("xcity", "XCity"), ("giga", "GIGA"), ("kin8", "Kin8"),
            ("dahlia", "Dahlia"), ("faleno", "Faleno"), ("minnano", "Minnano"),
            ("gfriends", "GFriends"), ("wikipedia", "Wikipedia")
        };
        var row = 0; var col = 0;
        foreach (var (id, name) in allSourceIds)
        {
            var enabled = advanced.IsSourceEnabled(id);
            var cb = new CheckBox { Content = name, IsChecked = enabled, Margin = new Thickness(0, 4, 16, 4), FontSize = 12 };
            Grid.SetRow(cb, row); Grid.SetColumn(cb, col);
            SourceToggleGrid.Children.Add(cb);
            _sourceCheckBoxes[id] = cb;
            col++; if (col >= 4) { col = 0; row++; }
        }
    }

    private void SaveSourceToggles(VideoScrapeSettings scraping)
    {
        var advanced = scraping.Advanced ?? new VideoScrapeAdvancedSettings();
        foreach (var (id, cb) in _sourceCheckBoxes)
        {
            if (advanced.SourceConfigs.TryGetValue(id, out var cfg))
                cfg.Enabled = cb.IsChecked == true;
            else
                advanced.SourceConfigs[id] = new VideoSourceConfig { Enabled = cb.IsChecked == true };
        }
        scraping.Advanced = advanced;
    }

    private void PopulateContentRoutes(VideoScrapeSettings scraping)
    {
        ContentRoutePanel.Children.Clear();
        var advanced = scraping.Advanced ?? new VideoScrapeAdvancedSettings();
        var kindNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Censored"] = "有码", ["Uncensored"] = "无码", ["Fc2"] = "FC2",
            ["Chinese"] = "中文", ["Amateur"] = "素人", ["Western"] = "欧美", ["Hentai"] = "里番"
        };
        foreach (var route in advanced.ContentRoutes)
        {
            var kindName = kindNames.TryGetValue(route.Kind.ToString(), out var cn) ? cn : route.Kind.ToString();
            var sources = string.Join(" → ", route.Sources);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock { Text = $"{kindName}:", FontSize = 12, FontWeight = FontWeights.SemiBold, Width = 50, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = sources, FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
            ContentRoutePanel.Children.Add(row);
        }
    }

    private void SaveContentRoutes(VideoScrapeSettings scraping) { /* 路由当前为只读展示，后续可扩展 */ }



    private static string? TryDeleteInsideDir(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return null;
        try { Directory.Delete(dirPath, recursive: true); return dirPath; } catch { return null; }
    }
}

file static class ListExtensions
{
    public static void AddIfNotNull(this List<string> list, string? value)
    {
        if (value is not null) list.Add(value);
    }

}
