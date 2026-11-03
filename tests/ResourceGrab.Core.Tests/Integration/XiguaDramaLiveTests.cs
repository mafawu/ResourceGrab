using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services.ShortDrama;
using ResourceGrab.Core.Services.VideoScrape;
using ResourceGrab.Core.Sources.ShortDrama;
using Xunit;

namespace ResourceGrab.Core.Tests.Integration;

/// <summary>
/// 短剧**真实链路**集成测试（默认跳过，避免 CI 联网/下载）。
/// <para>
/// 打开方式：
/// <c>$env:RG_LIVE_DRAMA=1</c> 只跑网络链路（列表 → 详情 → 取流）；
/// 再加 <c>$env:RG_LIVE_DRAMA_DOWNLOAD=1</c> + <c>$env:RG_FFMPEG_DIR=...\ffmpeg</c>
/// 会把第 1 集真的下下来并校验 drama.json 是否被回填（约 1 分钟 / 40 MB）。
/// 本机需要代理时设 <c>RG_PROXY=http://127.0.0.1:10809</c>。
/// </para>
/// </summary>
public class XiguaDramaLiveTests
{
    private static bool LiveEnabled =>
        Environment.GetEnvironmentVariable("RG_LIVE_DRAMA") == "1";

    private static bool LiveDownloadEnabled =>
        LiveEnabled && Environment.GetEnvironmentVariable("RG_LIVE_DRAMA_DOWNLOAD") == "1";

    private static string? Proxy =>
        Environment.GetEnvironmentVariable("RG_PROXY")
        ?? Environment.GetEnvironmentVariable("HTTPS_PROXY")
        ?? Environment.GetEnvironmentVariable("HTTP_PROXY");

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
        };
        if (!string.IsNullOrWhiteSpace(Proxy)) handler.Proxy = new System.Net.WebProxy(Proxy);
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(XiguaDramaConstants.UserAgent);
        return client;
    }

    [Fact]
    public async Task Live_Feed_Detail_PlayFrame()
    {
        if (!LiveEnabled) return;

        var http = CreateHttpClient();
        var client = new XiguaDramaHttpClient(http, null, null);
        var source = new XiguaDramaSource(client, null);

        // 先单独验一层：拿到的 HTML 是不是真页面（Cloudflare 挑战页只有几 KB）
        var html = await client.GetStringAsync("/newest");
        Assert.True(html.Length > 20_000, $"列表 HTML 太短（{html.Length}），可能被拦截：{html}");

        var feed = await source.GetFeedAsync(DramaFeedKind.Newest, 1);
        Assert.NotEmpty(feed.Items);
        var first = feed.Items[0];
        Assert.False(string.IsNullOrWhiteSpace(first.Title));
        Assert.StartsWith("http", first.CoverUrl);
        Assert.StartsWith("http", first.DetailUrl);

        var detail = await source.GetDetailAsync(first.Id);
        Assert.NotNull(detail);
        Assert.NotEmpty(detail!.Episodes);
        Assert.True(detail.Episodes.Select(e => e.Number).SequenceEqual(
            Enumerable.Range(1, detail.Episodes.Count)));

        var frame = await source.GetPlayFrameAsync(detail.Id, 1);
        Assert.NotNull(frame);
        Assert.Contains(".m3u8", frame!.StreamUrl);
        Assert.Contains("auth_key=", frame.StreamUrl);
    }

    [Fact]
    public async Task Live_Cover_DecryptsToRealJpeg()
    {
        if (!LiveEnabled) return;

        var http = CreateHttpClient();
        var source = new XiguaDramaSource(new XiguaDramaHttpClient(http, null, null), null);
        var cacheDir = Path.Combine(Path.GetTempPath(), "rg-drama-covers-" + Guid.NewGuid().ToString("N")[..8]);
        var covers = new DramaCoverService(http, null, cacheDir);

        var feed = await source.GetFeedAsync(DramaFeedKind.Newest, 1);
        var card = feed.Items.First(i => !string.IsNullOrWhiteSpace(i.CoverUrl));

        var id = "live-" + Guid.NewGuid().ToString("N")[..6];
        var path = await covers.EnsureAsync(id, card.CoverUrl);
        Assert.False(string.IsNullOrWhiteSpace(path), "封面解密失败（密钥可能变了）");

        var bytes = await File.ReadAllBytesAsync(path!);
        Assert.True(bytes.Length > 10_000, $"封面太小：{bytes.Length}");
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
        Assert.Equal(0xFF, bytes[2]);
        // 尾部必须是 EOI，说明 NoPadding 的块对齐垃圾被裁掉了
        Assert.Equal(0xD9, bytes[^1]);
        Assert.Equal(0xFF, bytes[^2]);

        try { Directory.Delete(cacheDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Live_Import_LocalFoldersGetOnlineInfo()
    {
        if (!LiveEnabled) return;

        var http = CreateHttpClient();
        var source = new XiguaDramaSource(new XiguaDramaHttpClient(http, null, null), null);

        var root = Path.Combine(Path.GetTempPath(), "rg-drama-import-" + Guid.NewGuid().ToString("N")[..8]);
        var libDir = Path.Combine(root, "_lib");
        var coverDir = Path.Combine(root, "_covers");
        var dramaFolder = Path.Combine(root, "苗疆少女");
        var unknownFolder = Path.Combine(root, "这个剧名站点上肯定没有");
        Directory.CreateDirectory(dramaFolder);
        Directory.CreateDirectory(unknownFolder);
        // 假视频文件：导入只认文件名，不解码内容
        await File.WriteAllBytesAsync(Path.Combine(dramaFolder, "第01集.mp4"), new byte[2048]);
        await File.WriteAllBytesAsync(Path.Combine(dramaFolder, "第02集.mp4"), new byte[2048]);
        await File.WriteAllBytesAsync(Path.Combine(unknownFolder, "第01集.mp4"), new byte[2048]);

        var library = new ShortDramaLibraryService(() => new ShortDramaSettings { DownloadDir = libDir });
        var covers = new DramaCoverService(http, null, coverDir);
        var import = new DramaLocalImportService(source, library, covers, null);

        try
        {
            var report = await import.ImportAsync(root);

            Assert.Equal(2, report.Items.Count);
            Assert.True(report.Succeeded >= 1, "至少应匹配上「苗疆少女」");
            var matched = report.Items.Single(i => i.Success);
            Assert.Contains("苗疆", matched.MatchedTitle);

            // 元数据落盘 + 集号按文件名映射
            var metaPath = Path.Combine(dramaFolder, "drama.json");
            Assert.True(File.Exists(metaPath));
            var local = library.TryLoad(dramaFolder);
            Assert.NotNull(local);
            Assert.Equal("第01集.mp4", local!.Metadata.Episodes.Single(e => e.Number == 1).File);
            Assert.Equal(2, local.Metadata.Episodes.Count);

            // 外部目录登记后，Load() 也能读到它
            Assert.Contains(library.Load(), d => d.FolderPath == dramaFolder);

            // 认不出来的那条要如实报告失败，不能瞎关联
            Assert.Contains(report.Items, i => !i.Success);
        }
        finally
        {
            library.UnregisterExternal(dramaFolder);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Live_Download_OneEpisode_BackfillsMetadata()
    {
        if (!LiveDownloadEnabled) return;

        // ffmpeg 由 VideoDownloadService.FindFfmpeg() 按 PATH/程序目录查找，
        // 测试进程把发布包的 ffmpeg 目录塞进 PATH 即可被找到（ffmpeg.exe 自己会按同目录找 DLL）。
        var ffmpegDir = Environment.GetEnvironmentVariable("RG_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(ffmpegDir))
        {
            Environment.SetEnvironmentVariable("PATH",
                ffmpegDir + ";" + Environment.GetEnvironmentVariable("PATH"));
        }
        Assert.NotNull(VideoDownloadService.FindFfmpeg());

        var root = Path.Combine(Path.GetTempPath(), "rg-drama-live-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var settings = new ShortDramaSettings { DownloadDir = root, DownloadConcurrency = 1 };
            var http = CreateHttpClient();
            var source = new XiguaDramaSource(new XiguaDramaHttpClient(http, null, null), null);
            var library = new ShortDramaLibraryService(() => settings);
            var queue = new VideoScrapeTaskQueue(2);
            var downloader = new VideoDownloadService();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));

            // 复刻 VideoScrapeTaskExecutor 的下载分支（同样的字段搬运），
            // 目的是让"队列 → ffmpeg → 完成事件 → 回填 drama.json"整条链路真的跑一遍。
            _ = queue.StartProcessingAsync(async (task, ct) =>
            {
                var request = new VideoDownloadRequest(
                    task.Number, task.DownloadTitle ?? "", task.DownloadUrl ?? "",
                    task.DownloadReferer ?? "", task.DownloadProxy, task.DownloadDurationText ?? "",
                    task.DownloadMasterUrl ?? "", task.DownloadVariantLabel ?? "", task.DownloadOutputDir);
                var result = await downloader.DownloadAsync(request, null, ct);
                task.DownloadOutputPath = result.OutputPath;
                if (result.Skipped) task.Status = VideoTaskStatus.Skipped;
                else task.SuccessCount = 1;
            }, cts.Token);

            var service = new ShortDramaDownloadService(
                source, queue, library, http, () => settings, () => Proxy, null);

            var detail = await source.GetDetailAsync("5033");
            Assert.NotNull(detail);

            var queued = await service.EnqueueEpisodeAsync(detail!, 1, cts.Token);
            Assert.Equal(1, queued.Queued);

            // 下载页靠这两个字段区分「短剧 剧名·第N集」和普通视频
            var task = queue.GetAllTasks().SingleOrDefault(t => t.Type == VideoTaskType.DownloadVideo);
            Assert.NotNull(task);
            Assert.Equal("shortdrama", task!.DownloadCategory);
            Assert.Equal(detail!.Title, task.Number);
            Assert.Equal("第01集", task.DownloadTitle);
            Assert.False(string.IsNullOrWhiteSpace(task.DownloadOutputDir));

            // 等回填：drama.json 里第 1 集出现 file 字段
            string? file = null;
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                var local = library.Load().FirstOrDefault();
                file = local?.Metadata.Episodes.FirstOrDefault(e => e.Number == 1)?.File;
                if (!string.IsNullOrWhiteSpace(file)) break;
                await Task.Delay(2000, cts.Token);
            }

            Assert.False(string.IsNullOrWhiteSpace(file), "下载完成但没有回填 drama.json");
            var drama = library.Load().Single();
            var mp4 = Path.Combine(drama.FolderPath, file!);
            Assert.True(File.Exists(mp4));
            Assert.True(new FileInfo(mp4).Length > 1024 * 1024, "文件太小，可能不是完整视频");
            Assert.Equal(1, drama.DownloadedCount);

            queue.CancelAll();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
