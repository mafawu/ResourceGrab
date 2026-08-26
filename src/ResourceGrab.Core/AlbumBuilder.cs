using ResourceGrab.Core.Models;
using ResourceGrab.Core.Utils;

namespace ResourceGrab.Core;

/// <summary>
/// 将 API 返回的 AlbumRespData 组装为 UI 使用的 Album（对应原 Rust types.rs 的 from_album_resp_data）。
/// 同时根据下载目录检测每个章节是否已下载。
/// </summary>
public static class AlbumBuilder
{
    public static Album Build(AlbumRespData album, string downloadDir)
    {
        var albumTitle = FilenameFilter.Filter(album.Name);

        var chapterInfos = new List<ChapterInfo>();
        foreach (var series in album.Series)
        {
            if (!long.TryParse(series.Id, out var chapterId))
            {
                continue;
            }
            var chapterTitle = $"第{series.Sort}话";
            if (!string.IsNullOrEmpty(series.Name))
            {
                chapterTitle += $" {FilenameFilter.Filter(series.Name)}";
            }
            var downloadPath = Path.Combine(downloadDir, albumTitle, chapterTitle);
            chapterInfos.Add(new ChapterInfo
            {
                ChapterId = chapterId,
                AlbumId = album.Id,
                AlbumTitle = albumTitle,
                ChapterTitle = chapterTitle,
                IsDownloaded = Directory.Exists(downloadPath),
            });
        }

        // 如果没有章节信息，就添加一个默认的章节信息（与原版一致）
        if (chapterInfos.Count == 0)
        {
            chapterInfos.Add(new ChapterInfo
            {
                AlbumId = album.Id,
                AlbumTitle = albumTitle,
                ChapterId = album.Id,
                ChapterTitle = "第1话",
                IsDownloaded = false,
            });
        }

        return new Album
        {
            Id = album.Id,
            Name = album.Name,
            Addtime = album.Addtime,
            Description = album.Description,
            TotalViews = album.TotalViews,
            Likes = album.Likes,
            ChapterInfos = chapterInfos,
            SeriesId = album.SeriesId,
            Series = album.Series,
            CommentTotal = album.CommentTotal,
            Author = album.Author,
            Tags = album.Tags,
            Works = album.Works,
            Actors = album.Actors,
            RelatedList = album.RelatedList,
            Liked = album.Liked,
            IsFavorite = album.IsFavorite,
            IsAids = album.IsAids,
        };
    }
}
