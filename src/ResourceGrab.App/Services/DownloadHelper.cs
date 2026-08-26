using ResourceGrab.Core;
using ResourceGrab.Core.Downloading;
using ResourceGrab.Core.Http;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Sources.Jm;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceGrab.App.Services;

/// <summary>下载辅助：拉取漫画详情并整本加入下载队列（站点差异收敛到 IComicSource）。</summary>
public static class DownloadHelper
{
    /// <summary>通用入口：任意内容源整本下载。</summary>
    public static async Task<(int Count, string ComicTitle)> EnqueueAllAsync(
        IComicSource source, ConfigService config, DownloadManager manager,
        string comicId, CancellationToken ct = default)
    {
        var detail = await source.GetComicAsync(comicId, ct);
        foreach (var chapter in detail.Chapters)
        {
            await manager.SubmitChapterAsync(chapter, ct);
        }
        // 禁漫写专辑元数据（album.json）；其余源写通用来源元数据（source.json，供「已下载」徽章与离线展示）
        if (source is JmSource jmSource)
        {
            var resp = await jmSource.GetAlbumRawAsync(comicId, ct);
            var album = AlbumBuilder.Build(resp, config.Current.DownloadDir);
            if (album is not null)
            {
                App.Services.GetRequiredService<LocalLibraryService>()
                    .SaveMetadataForAlbum(config.Current.DownloadDir, album);
            }
        }
        else
        {
            App.Services.GetRequiredService<LocalLibraryService>()
                .SaveSourceMetadata(config.Current.DownloadDir, source.Info.Id, detail);
        }
        return (detail.Chapters.Count, detail.Title);
    }

    /// <summary>禁漫旧入口（Rank/Weekly/Favorite/Category 等页面沿用）。</summary>
    public static Task<(int Count, string ComicTitle)> EnqueueAllAsync(
        JmHttpClient client, ConfigService config, DownloadManager manager,
        long albumId, CancellationToken ct = default)
        => EnqueueAllAsync(new JmSource(client), config, manager, albumId.ToString(), ct);
}


