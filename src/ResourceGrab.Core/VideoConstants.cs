namespace ResourceGrab.Core;

/// <summary>
/// 视频链路统一常量。
/// <para>
/// 在线搜索 / 详情抓取 / m3u8 与分片请求 / ffmpeg 下载 / 播放器取流**共用同一个 User-Agent**。
/// 部分 CDN（MissAV 系等）会把签名 token 与请求方 UA 绑定：取 m3u8 用一个 UA、播放器拉分片换
/// 另一个 UA，就会被判 403，表现为「能取到播放地址但一点播放就失败」。
/// 此前链路里 120 / 128 / 136 三个版本混用，改这里即全链路生效。
/// </para>
/// </summary>
public static class VideoConstants
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36";
}
