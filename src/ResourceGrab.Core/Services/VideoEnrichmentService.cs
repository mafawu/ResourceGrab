using ResourceGrab.Core.Models;
using ResourceGrab.Core.Utils;

namespace ResourceGrab.Core.Services;

/// <summary>
/// 视频元数据补齐协调器：后台逐个为缺失时长 / 分辨率 / 缩略图的视频文件生成数据。
/// 全部 CPU/IO 密集操作在后台线程执行，UI 通过 onFileUpdated 回调刷新对应卡片。
/// </summary>
public static class VideoEnrichmentService
{
    /// <summary>
    /// 补齐一个文件夹内所有文件的元数据。
    /// 直接修改传入的 folder 对象（模型为 POCO，调用方负责持久化）。
    /// </summary>
    /// <param name="folder">目标文件夹。</param>
    /// <param name="onFileUpdated">每个文件处理完成后回调（后台线程，注意调度回 UI 线程）。</param>
    /// <returns>发生变化的文件数量。</returns>
    public static async Task<int> EnrichFolderAsync(
        VideoFolder folder,
        Action<VideoFile>? onFileUpdated = null,
        CancellationToken ct = default)
    {
        var pending = folder.Files
            .Where(f => f.FileExists)
            .Where(f => f.DurationSeconds <= 0
                        || string.IsNullOrEmpty(f.Resolution)
                        || string.IsNullOrEmpty(f.ThumbnailPath))
            .ToList();

        var changed = 0;
        foreach (var file in pending)
        {
            ct.ThrowIfCancellationRequested();

            if (file.DurationSeconds <= 0 || string.IsNullOrEmpty(file.Resolution))
            {
                var meta = await Task.Run(() => VideoMetadataReader.Read(file.FilePath), ct);
                if (meta is { } m)
                {
                    if (m.DurationSeconds > 0 && file.DurationSeconds <= 0)
                    {
                        file.DurationSeconds = m.DurationSeconds;
                    }
                    var resolution = m.Width > 0 && m.Height > 0 ? $"{m.Width}x{m.Height}" : "";
                    if (resolution != "" && file.Resolution != resolution)
                    {
                        file.Resolution = resolution;
                    }
                }
            }

            if (string.IsNullOrEmpty(file.ThumbnailPath) || !File.Exists(file.ThumbnailPath))
            {
                // 缩略图服务内部会在成功后触发 ThumbnailSaved 事件供卡片刷新
                var thumb = await VideoThumbnailService.GenerateAsync(file.FilePath, ct: ct);
                if (!string.IsNullOrEmpty(thumb))
                {
                    file.ThumbnailPath = thumb;
                }
            }

            changed++;
            onFileUpdated?.Invoke(file);
        }

        return changed;
    }
}
