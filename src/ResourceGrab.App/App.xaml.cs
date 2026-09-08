using System.Net.Http;
using System.Threading;
using System.Text;
using System.Windows;
using ResourceGrab.App.Common;
using ResourceGrab.App.Services;
using ResourceGrab.App.Themes;
using ResourceGrab.App.ViewModels;
using ResourceGrab.Core;
using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Downloading;
using ResourceGrab.Core.Http;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Sources.Copymanga;
using ResourceGrab.Core.Sources.Hitomi;
using ResourceGrab.Core.Sources.Jm;
using ResourceGrab.Core.Sources.Baozimh;
using ResourceGrab.Core.Sources.Wnacg;
using ResourceGrab.Core.Sources.VideoSources;
using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Services.VideoScrape.Fetching;
using ResourceGrab.Core.Services.VideoScrape.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App;

/// <summary>
/// 应用入口：注册 DI 服务（多内容源 / ConfigService / DownloadManager / Session / 下载面板），
/// 启动即恢复主题偏好，退出时释放下载资源。
/// 派生应用（如 Copymanga.App）可覆盖 <see cref="DataDirName"/> 与 <see cref="ConfigureSources"/>
/// 以产出独立的数据目录与独立的源集合。
/// </summary>
public partial class App : Application
{
    /// <summary>全局服务容器：主程序与派生 exe（copymanga 版）共用此入口，供各视图/服务解析依赖。</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>供派生 exe（不继承本类的组合式应用）注入服务容器。</summary>
    public static void SetServices(IServiceProvider services) => Services = services;

    /// <summary>数据目录名（派生应用覆盖为独立目录，避免与主程序共享本地库）。</summary>
    protected virtual string DataDirName => "config";

    /// <summary>是否注册禁漫登录会话服务（免登录的 copymanga 版覆盖为 false）。</summary>
    protected virtual bool RegisterSessionService => true;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RunStartup(e);
    }

    /// <summary>
    /// 实际启动逻辑：初始化主题、注册 DI、显示主窗口。
    /// 供派生应用（不继承本类、改用组合方式的独立 exe）复用。
    /// </summary>
    protected void RunStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) => { try { var logDir = System.IO.Path.Combine(AppContext.BaseDirectory, DataDirName, "logs"); System.IO.Directory.CreateDirectory(logDir); var ex = e.ExceptionObject as Exception; System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "crash-" + DateTime.Now.ToString("yyyyMMdd") + ".log"), "[" + DateTime.Now + "] AppDomain.UnhandledException\r\n" + ex + "\r\n\r\n"); System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "jm_crash.log"), "[" + DateTime.Now + "] AppDomain.UnhandledException\r\n" + ex + "\r\n\r\n"); } catch { } };
        DispatcherUnhandledException += (s, e) => { try { var logDir = System.IO.Path.Combine(AppContext.BaseDirectory, DataDirName, "logs"); System.IO.Directory.CreateDirectory(logDir); System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "crash-" + DateTime.Now.ToString("yyyyMMdd") + ".log"), "[" + DateTime.Now + "] DispatcherUnhandledException\r\n" + e.Exception + "\r\n\r\n"); System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "jm_crash.log"), "[" + DateTime.Now + "] DispatcherUnhandledException\r\n" + e.Exception + "\r\n\r\n"); } catch { } e.Handled = true; System.Windows.MessageBox.Show("发生未处理异常：" + e.Exception.Message, "崩溃", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error); };
        AppPaths.DataDirName = DataDirName;

        // 把旧版 %APPDATA% 数据迁移到程序同目录数据文件夹
        AppPaths.MigrateLegacyData();

        // 注：GBK/GB18030 等 Windows 代码页在本 net10.0-windows 目标上由 OS NLS 原生提供，
        // 不再需要 System.Text.Encoding.CodePages 包（NU1510 警告），故移除 RegisterProvider 调用。
        ThemeManager.Initialize();

        var services = new ServiceCollection();
        var configService = new ConfigService(AppPaths.ConfigPath);
        services.AddSingleton(configService);
        services.AddSingleton<ILogger>(sp => new FileLogger(AppPaths.LogsDir));

        // 内容源：派生应用可覆盖此方法以注册不同的源集合
        ConfigureSources(services);

        services.AddSingleton<SourceManager>();
        services.AddSingleton<AggregateSearchService>();
        services.AddSingleton<OnlineReaderService>();

        services.AddSingleton<DownloadManager>(sp => new DownloadManager(
            sp.GetServices<IComicSource>(), sp.GetRequiredService<ConfigService>()));
        if (RegisterSessionService)
        {
            services.AddSingleton<SessionService>();
        }
        services.AddSingleton<LocalLibraryService>();
        services.AddSingleton<JmMatchService>();
        services.AddSingleton<NovelIndexService>();
        services.AddSingleton<NovelReadingHistoryService>();
        services.AddSingleton<NovelReaderSettingsService>();
        services.AddSingleton<AlbumUpdateService>();
        services.AddSingleton<ComicUserDataService>(sp => new ComicUserDataService(AppPaths.ComicUserDataPath));
        services.AddSingleton<OnlinePageCountCacheService>(sp => new OnlinePageCountCacheService(
            AppPaths.OnlinePageCountCachePath));
        services.AddSingleton<VideoLibraryService>(sp => new VideoLibraryService(
            AppPaths.VideoLibraryPath, AppPaths.VideoFoldersPath, sp.GetRequiredService<ILogger>(),
            sp.GetRequiredService<ScrapeReportService>()));
        services.AddSingleton<VideoActorMerger>();
        services.AddSingleton<IVideoActorSource, MinnanoSource>();
        services.AddSingleton<IVideoActorSource, WikipediaActorSource>();
        // GFriends 头像源：HttpClient 走 per-source 工厂（代理 = 全局 VideoScraping.Proxy）
        services.AddSingleton<GFriendsSource>(sp => new GFriendsSource(
            sp.GetRequiredService<IVideoHttpClientFactory>().Create("gfriends"),
            System.IO.Path.Combine(AppPaths.AppDataDir, "gfriends", "Filetree.json"),
            System.IO.Path.Combine(AppPaths.AppDataDir, "avatars"),
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoActorSource>(sp => sp.GetRequiredService<GFriendsSource>());
        services.AddSingleton<ScrapeReportService>();
        services.AddSingleton<VideoScrapeService>(sp => new VideoScrapeService(
            sp.GetRequiredService<VideoLibraryService>(),
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<ILogger>(),
            sp.GetRequiredService<ScrapeReportService>(),
            sp.GetRequiredService<JavBusScraper>(),
            sp.GetRequiredService<JavDbScraper>(),
            sp.GetRequiredService<AiravScraper>()));
        services.AddSingleton<VideoScrapeTaskQueue>(sp => new VideoScrapeTaskQueue(
            Math.Clamp((sp.GetRequiredService<ConfigService>().Current.VideoScraping ?? new VideoScrapeSettings()).Concurrency, 1, 8),
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton<VideoScrapeTaskExecutor>();
        // M3U8/MP4 直存下载：HLS 经本地中继（与播放器同链路，Referer/代理由中继注入）
        services.AddSingleton<VideoDownloadService>(sp => new VideoDownloadService(
            (uri, referer, proxy) => HlsLocalRelay.Instance.Register(uri, referer, proxy),
            url => HlsLocalRelay.Instance.Unregister(url),
            sp.GetRequiredService<ILogger>(),
            () => VideoDownloadService.ResolveDownloadDir(
                configService.Current.VideoScraping?.VideoDownloadDir)));
        // MissAV 在线搜索源：注册为具体单例，供 IVideoSource（在线搜索）与刮削源共用
        services.AddSingleton(sp => new MissAvSource(
            CreateMissAvHttpClient(configService),
            sp.GetRequiredService<ILogger>(),
            (configService.Current.VideoScraping ?? new VideoScrapeSettings()).Proxy));
        services.AddSingleton<ResourceGrab.Core.Sources.IVideoSource>(sp => sp.GetRequiredService<MissAvSource>());
        // JavDB 在线搜索源：Cookie / 域名从配置实时读取，改设置无需重启
        services.AddSingleton(sp => new JavDbSource(
            CreateMissAvHttpClient(configService),
            configService,
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton<ResourceGrab.Core.Sources.IVideoSource>(sp => sp.GetRequiredService<JavDbSource>());
        // 在线详情内存缓存：搜索回填与详情侧栏共用，应用生命周期内同一详情不重复请求
        services.AddSingleton<OnlineVideoDetailCache>();

        // —— 视频刮削新管道（图引擎）——
        // per-source 配置与全局设置：取用户当前配置实例，用户改配置后重启生效。
        var videoScrapeSettings = configService.Current.VideoScraping ?? new VideoScrapeSettings();
        var advancedSettings = videoScrapeSettings.Advanced ?? new VideoScrapeAdvancedSettings();
        services.AddSingleton(advancedSettings);
        services.AddSingleton(videoScrapeSettings);
        services.AddSingleton<DomainCookieJar>(sp => new DomainCookieJar(
            System.IO.Path.Combine(AppPaths.AppDataDir, "video-cookies.json"),
            VideoHttpClientFactory.DefaultUserAgent,
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton<CurlImpersonateClient>(sp => new CurlImpersonateClient(
            Math.Clamp(videoScrapeSettings.TimeoutSeconds, 5, 120), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoHttpClientFactory>(sp => new VideoHttpClientFactory(
            sp.GetRequiredService<VideoScrapeAdvancedSettings>(),
            sp.GetRequiredService<VideoScrapeSettings>(),
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IResilientFetcher>(sp => new MultiTierFetcher(
            sp.GetRequiredService<IVideoHttpClientFactory>(),
            sp.GetRequiredService<VideoScrapeAdvancedSettings>(),
            sp.GetRequiredService<VideoScrapeSettings>(),
            sp.GetRequiredService<CurlImpersonateClient>(),
            sp.GetRequiredService<DomainCookieJar>(),
            sp.GetRequiredService<ILogger>()));
        // 老三样刮削源单例：legacy 路径（VideoScrapeService）与 graph 路径（Legacy*Source 适配器）
        // 共用同一批实例，避免两套构造方式漂移。
        services.AddSingleton<VideoScrapeHttpClient>(sp => new VideoScrapeHttpClient(
            configService.Current.VideoScraping ?? new VideoScrapeSettings()));
        services.AddSingleton(sp => new JavBusScraper(sp.GetRequiredService<VideoScrapeHttpClient>(),
            (configService.Current.VideoScraping ?? new VideoScrapeSettings()).JavBusBaseUrl));
        services.AddSingleton(sp => new JavDbScraper(sp.GetRequiredService<VideoScrapeHttpClient>(),
            (configService.Current.VideoScraping ?? new VideoScrapeSettings()).JavDbBaseUrl));
        services.AddSingleton(sp => new AiravScraper(sp.GetRequiredService<VideoScrapeHttpClient>()));
        services.AddSingleton<IVideoScrapeSource, LegacyJavBusSource>();
        services.AddSingleton<IVideoScrapeSource, LegacyJavDbSource>();
        services.AddSingleton<IVideoScrapeSource, LegacyAiravSource>();
        // —— 阶段2 新实现的图引擎源（构造统一为 (IResilientFetcher, ILogger?)）——
        // MissAV 刮削源：Tier 1 首批必含（复用在线源的搜索/详情/镜像/curl 全套链路）
        services.AddSingleton<IVideoScrapeSource>(sp => new MissAvScrapeSource(
            sp.GetRequiredService<IResilientFetcher>(),
            sp.GetRequiredService<MissAvSource>(),
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new JavLibrarySource(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new Jav321Source(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new IqqtvSource(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new AvsoxSource(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new FreejavbtSource(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new Kin8Source(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new XcitySource(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new GigaSource(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new GetchuSource(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new DahliaSource(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new FalenoSource(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new Fc2ppvdbSource(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new Fc2ClubSource(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new Fc2Source(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new MgstageSource(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new DmmSource(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IVideoScrapeSource>(sp => new PrestigeSource(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>()));
        // ThePornDb 的 API Key 来自 sourceConfigs.theporndb.apiKey（未配置时源自行降级返回 NoMatch）
        services.AddSingleton<IVideoScrapeSource>(sp => new ThePornDbSource(
            sp.GetRequiredService<IResilientFetcher>(), sp.GetRequiredService<ILogger>(),
            advancedSettings.SourceConfigs.TryGetValue("theporndb", out var tpdb) && tpdb.Enabled
                ? tpdb.ApiKey
                : null));
        services.AddSingleton<IVideoScrapeSourceRegistry, VideoScrapeSourceRegistry>();
        services.AddSingleton<IVideoSourceSnapshotCache>(sp => new TtlSnapshotCache(
            new DiskJsonSnapshotCache(AppPaths.VideoSourceCacheDir, sp.GetRequiredService<ILogger>()),
            TimeSpan.FromDays(7), sp.GetRequiredService<ILogger>()));
        services.AddSingleton<VideoFetchGraphScheduler>();
        services.AddSingleton<VideoMetadataMerger>();
        services.AddSingleton<VideoSourceHealthService>(sp => new VideoSourceHealthService(
            System.IO.Path.Combine(AppPaths.AppDataDir, "video-source-health.json"),
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton<VideoGraphScrapeOrchestrator>();
        // 在线搜索兜底：MissAV 关键词搜不到且关键词为番号时，按 contentRoutes 顺序换源按番号取详情
        services.AddSingleton<OnlineVideoFallbackSearchService>();
        services.AddSingleton<DownloadPanelViewModel>();
        Services = services.BuildServiceProvider();

        try
        {
            // 刮削配置校验：路由/字段优先级引用未注册源时打 warning，不阻塞启动
            var registry = Services.GetRequiredService<IVideoScrapeSourceRegistry>();
            var validation = VideoScrapeConfigValidator.Validate(
                Services.GetRequiredService<VideoScrapeAdvancedSettings>(), registry);
            foreach (var warning in validation.Warnings)
                Services.GetRequiredService<ILogger>()?.Warn($"[App] 刮削配置: {warning}");
        }
        catch (Exception ex)
        {
            Services.GetService<ILogger>()?.Error("[App] 刮削配置校验失败", ex);
        }

        try
        {
            var scrapeQueue = Services.GetRequiredService<VideoScrapeTaskQueue>();
            var scrapeExecutor = Services.GetRequiredService<VideoScrapeTaskExecutor>();
            _ = scrapeQueue.StartProcessingAsync((task, ct) => scrapeExecutor.ExecuteAsync(task, ct), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Services.GetService<ILogger>()?.Error("[App] 视频刮削任务队列启动失败", ex);
        }

    static HttpClient CreateMissAvHttpClient(ConfigService config)
    {
        var settings = config.Current.VideoScraping ?? new VideoScrapeSettings();
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        if (!string.IsNullOrWhiteSpace(settings.Proxy))
            handler.Proxy = new System.Net.WebProxy(settings.Proxy);
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 60)) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.TryParseAdd("zh-CN,zh;q=0.9,en-US;q=0.8,ja;q=0.7");
        return client;
    }

        // 用配置中保存的凭据静默恢复登录态（仅注册了会话服务的应用）
        _ = Task.Run(async () =>
        {
            try
            {
                if (Services.GetService<SessionService>() is { } session)
                {
                    await session.TryRestoreAsync();
                }
            }
            catch
            {
                // 静默失败，不打扰用户
            }
        });

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>注册全部内容源。派生应用可覆盖为只注册自己的源。</summary>
    protected virtual void ConfigureSources(IServiceCollection services)
    {
        services.AddSingleton<JmHttpClient>();
        services.AddSingleton<JmSource>();
        services.AddSingleton<WnacgHttpClient>();
        services.AddSingleton<WnacgSource>();
        services.AddSingleton<HitomiHttpClient>();
        services.AddSingleton<HitomiGgResolver>();
        services.AddSingleton<HitomiGalleryClient>();
        services.AddSingleton<HitomiSource>();
        services.AddSingleton<BaozimhHttpClient>();
        services.AddSingleton<BaozimhSource>();
        services.AddSingleton<CopymangaHttpClient>();
        services.AddSingleton<CopymangaSource>();
        services.AddSingleton<IComicSource>(sp => sp.GetRequiredService<JmSource>());
        services.AddSingleton<IComicSource>(sp => sp.GetRequiredService<WnacgSource>());
        services.AddSingleton<IComicSource>(sp => sp.GetRequiredService<HitomiSource>());
        services.AddSingleton<IComicSource>(sp => sp.GetRequiredService<BaozimhSource>());
        services.AddSingleton<IComicSource>(sp => sp.GetRequiredService<CopymangaSource>());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        if (Services.GetService<ILogger>() is FileLogger fileLogger) fileLogger.Dispose();
        if (Services.GetService<DownloadManager>() is { } downloadManager)
        {
            downloadManager.Dispose();
        }
    }
}
