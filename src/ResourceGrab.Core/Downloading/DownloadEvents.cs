namespace ResourceGrab.Core.Downloading;

/// <summary>章节进入下载队列（等待开始）。</summary>
public class ChapterPendingEventArgs : EventArgs
{
    public ChapterPendingEventArgs(long chapterId, string chapterTitle, string albumTitle)
    {
        ChapterId = chapterId;
        ChapterTitle = chapterTitle;
        AlbumTitle = albumTitle;
    }

    public long ChapterId { get; }
    public string ChapterTitle { get; }
    public string AlbumTitle { get; }
}

/// <summary>章节开始下载，total 为该章节图片总数。</summary>
public class ChapterStartEventArgs : EventArgs
{
    public ChapterStartEventArgs(long chapterId, long total)
    {
        ChapterId = chapterId;
        Total = total;
    }

    public long ChapterId { get; }
    public long Total { get; }
}

/// <summary>一张图片下载成功。</summary>
public class ImageSuccessEventArgs : EventArgs
{
    public ImageSuccessEventArgs(long chapterId, string savePath, long downloadedCount)
    {
        ChapterId = chapterId;
        SavePath = savePath;
        DownloadedCount = downloadedCount;
    }

    public long ChapterId { get; }
    public string SavePath { get; }
    public long DownloadedCount { get; }
}

/// <summary>一张图片下载/保存失败。</summary>
public class ImageErrorEventArgs : EventArgs
{
    public ImageErrorEventArgs(long chapterId, string url, string error)
    {
        ChapterId = chapterId;
        Url = url;
        Error = error;
    }

    public long ChapterId { get; }
    public string Url { get; }
    public string Error { get; }
}

/// <summary>章节下载结束（errMsg 为 null 表示成功）。</summary>
public class ChapterEndEventArgs : EventArgs
{
    public ChapterEndEventArgs(long chapterId, string? errMsg)
    {
        ChapterId = chapterId;
        ErrMsg = errMsg;
    }

    public long ChapterId { get; }
    public string? ErrMsg { get; }
}

/// <summary>全局下载进度。</summary>
public class OverallProgressEventArgs : EventArgs
{
    public OverallProgressEventArgs(long downloadedImageCount, long totalImageCount, double percentage)
    {
        DownloadedImageCount = downloadedImageCount;
        TotalImageCount = totalImageCount;
        Percentage = percentage;
    }

    public long DownloadedImageCount { get; }
    public long TotalImageCount { get; }
    public double Percentage { get; }
}

/// <summary>下载速度（每秒更新）。</summary>
public class SpeedEventArgs : EventArgs
{
    public SpeedEventArgs(string speed)
    {
        Speed = speed;
    }

    public string Speed { get; }
}
