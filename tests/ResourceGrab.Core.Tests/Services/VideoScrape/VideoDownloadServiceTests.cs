using ResourceGrab.Core.Services.VideoScrape;
using Xunit;

namespace ResourceGrab.Core.Tests.Services.VideoScrape;

// M3U8 直存下载：纯函数与入队测试（联网/ffmpeg 部分由端到端覆盖）

public class VideoDownloadServiceTests
{
    [Theory]
    [InlineData("https://cdn.example.com/x/playlist.m3u8", true)]
    [InlineData("https://cdn.example.com/x/PLAYLIST.M3U8?token=1", true)]
    [InlineData("https://cdn.example.com/x/preview.mp4", false)]
    [InlineData("", false)]
    public void IsHls_DetectsPlaylist(string url, bool expected)
    {
        Assert.Equal(expected, VideoDownloadService.IsHls(url));
    }

    [Theory]
    [InlineData("12:34", 754.0)]
    [InlineData("1:02:03", 3723.0)]
    [InlineData("90 分钟", 5400.0)]
    [InlineData("", null)]
    [InlineData("未知", null)]
    public void ParseDurationSeconds_ParsesDisplayText(string text, double? expected)
    {
        Assert.Equal(expected, VideoDownloadService.ParseDurationSeconds(text));
    }

    [Fact]
    public void BuildOutputPath_SanitizesAndLimitsLength()
    {
        var path = VideoDownloadService.BuildOutputPath("ABP-123", "a/b:c*d?e\"f<g>h|i" + new string('x', 200));

        Assert.EndsWith(".mp4", path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/", Path.GetFileName(path));
        Assert.DoesNotContain("?", Path.GetFileName(path));
        Assert.True(Path.GetFileName(path).Length <= 124); // 120 + .mp4
    }

    [Fact]
    public void ParseVariants_ParsesMasterPlaylist()
    {
        var text = "#EXTM3U\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS=\"avc1\"\n"
            + "360p/video.m3u8\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=2265400,RESOLUTION=1280x720\n"
            + "https://cdn.example.com/x/720p/video.m3u8\n";
        var master = new Uri("https://cdn.example.com/x/playlist.m3u8");

        var variants = VideoDownloadService.ParseVariants(text, master);

        Assert.Equal(2, variants.Count);
        Assert.Equal("720p", variants[0].Label); // 按码率降序
        Assert.Equal(2265400, variants[0].BandwidthBps);
        Assert.Equal("https://cdn.example.com/x/720p/video.m3u8", variants[0].Uri);
        Assert.Equal("360p", variants[1].Label);
        Assert.Equal("https://cdn.example.com/x/360p/video.m3u8", variants[1].Uri);
    }

    [Fact]
    public void RewriteRelayPrefix_RestoresUpstreamUrls()
    {
        // 中继改写后的文本（含本地 token 地址）换回上游基址，解析出 CDN 真地址
        var text = "#EXTM3U\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=2265400,RESOLUTION=1280x720\n"
            + "http://127.0.0.1:7766/aa39ff569cc54c31bc188ed4744dd1f6/720p/video.m3u8\n";
        var restored = VideoDownloadService.RewriteRelayPrefix(
            text,
            "http://127.0.0.1:7766/aa39ff569cc54c31bc188ed4744dd1f6/",
            "https://surrit.com/7c49dbf3-f9f9-4c57-ae99-7c37064b4a19/");

        var variants = VideoDownloadService.ParseVariants(
            restored, new Uri("https://surrit.com/7c49dbf3-f9f9-4c57-ae99-7c37064b4a19/playlist.m3u8"));

        Assert.Single(variants);
        Assert.Equal("https://surrit.com/7c49dbf3-f9f9-4c57-ae99-7c37064b4a19/720p/video.m3u8", variants[0].Uri);
    }

    [Fact]
    public void ParseVariants_MediaPlaylist_ReturnsEmpty()
    {
        var text = "#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10.0,\nseg0.ts\n#EXT-X-ENDLIST\n";

        var variants = VideoDownloadService.ParseVariants(text, new Uri("https://cdn.example.com/x/index.m3u8"));

        Assert.Empty(variants);
    }

    [Fact]
    public void EnqueueDownload_CarriesPayload()
    {
        using var queue = new VideoScrapeTaskQueue();
        var request = new VideoDownloadRequest("SSIS-960", "标题", "https://cdn.example.com/x/playlist.m3u8",
            "https://site.example/", null, "12:34");

        var id = queue.EnqueueDownload(request);

        var task = queue.GetTask(id);
        Assert.NotNull(task);
        Assert.Equal(VideoTaskType.DownloadVideo, task!.Type);
        Assert.Equal("SSIS-960", task.Number);
        Assert.Equal(100, task.Total);
        Assert.Equal("https://cdn.example.com/x/playlist.m3u8", task.DownloadUrl);
        Assert.Equal("标题", task.DownloadTitle);
        Assert.Equal("https://site.example/", task.DownloadReferer);
        Assert.Equal("12:34", task.DownloadDurationText);
    }
}
