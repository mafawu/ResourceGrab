using ResourceGrab.Core.Downloading;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources;
using Xunit;

namespace ResourceGrab.Core.Tests.Downloading;

/// <summary>章节失败后的重试：沿用同一事件 id 重新入队，页面不会多出一行。</summary>
public class DownloadManagerRetryTests
{
    [Fact]
    public async Task RetryChapterAsync_RequeuesFailedChapter_WithSameEventId()
    {
        var root = Path.Combine(Path.GetTempPath(), "rg-dl-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new ConfigService(Path.Combine(root, "config.json"));
            config.Current.DownloadDir = Path.Combine(root, "out");
            Directory.CreateDirectory(config.Current.DownloadDir);

            var source = new FlakyComicSource();
            using var manager = new DownloadManager(new IComicSource[] { source }, config);

            var failed = new TaskCompletionSource<ChapterEndEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var succeeded = new TaskCompletionSource<ChapterEndEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.ChapterEnd += (_, e) =>
            {
                if (e.ErrMsg is null) succeeded.TrySetResult(e);
                else failed.TrySetResult(e);
            };

            await manager.SubmitChapterAsync(new Chapter
            {
                Id = "c1",
                Title = "第1话",
                ComicId = "1",
                ComicTitle = "测试漫画",
                SourceId = "fake",
            });

            var end = await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("boom", end.ErrMsg);

            source.Healthy = true;
            // 引擎在事件回调之后才清理"运行中"标记，重试按钮点击可能紧贴着失败事件到达
            var deadline = DateTime.UtcNow.AddSeconds(5);
            var retried = false;
            while (!retried && DateTime.UtcNow < deadline)
            {
                retried = await manager.RetryChapterAsync(end.ChapterId);
                if (!retried) await Task.Delay(20);
            }
            Assert.True(retried, "失败章节应当可以重新入队");

            var ok = await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(end.ChapterId, ok.ChapterId);
            Assert.Null(ok.ErrMsg);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 临时目录清理失败不影响测试结论 */ }
        }
    }

    [Fact]
    public async Task RetryChapterAsync_ReturnsFalse_ForUnknownChapter()
    {
        var root = Path.Combine(Path.GetTempPath(), "rg-dl-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new ConfigService(Path.Combine(root, "config.json"));
            config.Current.DownloadDir = Path.Combine(root, "out");
            Directory.CreateDirectory(config.Current.DownloadDir);

            using var manager = new DownloadManager(new IComicSource[] { new FlakyComicSource() }, config);

            Assert.False(await manager.RetryChapterAsync(123456));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 同上 */ }
        }
    }

    private sealed class FlakyComicSource : IComicSource
    {
        /// <summary>置为 true 后章节图片列表返回空（整章成功）。</summary>
        public volatile bool Healthy;

        public ComicSourceInfo Info { get; } = new() { Id = "fake", DisplayName = "测试源" };

        public Task<SearchResult> SearchAsync(string keyword, int page, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ComicDetail> GetComicAsync(string comicId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public async Task<IReadOnlyList<ImagePage>> GetChapterImagesAsync(Chapter chapter, CancellationToken ct = default)
        {
            await Task.Yield();
            if (!Healthy) throw new InvalidOperationException("boom");
            return Array.Empty<ImagePage>();
        }
    }
}
