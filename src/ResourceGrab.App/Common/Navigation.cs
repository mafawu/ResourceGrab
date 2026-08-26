using ResourceGrab.Core.Models;
using ResourceGrab.Core.Sources;

namespace ResourceGrab.App.Common;

/// <summary>页面导航：MainWindow 注册处理器，页面内触发跳转。</summary>
public static class Navigation
{
    public static Action<string, string>? OpenComicHandler { get; set; }
    public static Action<LocalComic>? OpenReaderHandler { get; set; }
    public static Action<LocalComic>? OpenLocalDetailHandler { get; set; }
    public static Action? CloseLocalDetailHandler { get; set; }
    public static Action? BackHandler { get; set; }
    public static Action<RankPeriod>? OpenRankHandler { get; set; }
    public static Action<string, string?>? OpenSearchHandler { get; set; }
    public static Action<IComicSource, IReadOnlyList<Chapter>, int>? OpenOnlineReaderHandler { get; set; }
    public static Action<string>? OpenNovelReaderHandler { get; set; }
    public static Action? DownloadedKeysChangedHandler { get; set; }

    /// <summary>打开指定源的漫画详情。</summary>
    public static void OpenComic(string sourceId, string comicId) => OpenComicHandler?.Invoke(sourceId, comicId);

    /// <summary>禁漫快捷入口（旧调用点保持可用）。</summary>
    public static void OpenAlbum(long albumId) => OpenComicHandler?.Invoke("jm", albumId.ToString());

    public static void OpenReader(LocalComic comic) => OpenReaderHandler?.Invoke(comic);

    public static void OpenLocalDetail(LocalComic comic) => OpenLocalDetailHandler?.Invoke(comic);

    public static void CloseLocalDetail() => CloseLocalDetailHandler?.Invoke();

    public static void Back() => BackHandler?.Invoke();

    public static void OpenRank(RankPeriod period) => OpenRankHandler?.Invoke(period);

    /// <summary>按关键词打开搜索页；sourceId 非空时优先只选该来源。</summary>
    public static void OpenSearch(string keyword, string? sourceId = null)
        => OpenSearchHandler?.Invoke(keyword, sourceId);

    /// <summary>打开在线阅读器：从指定章节开始浏览整本。</summary>
    public static void OpenNovelReader(string path) => OpenNovelReaderHandler?.Invoke(path);

    public static void OpenOnlineReader(IComicSource source, IReadOnlyList<Chapter> chapters, int startIndex)
        => OpenOnlineReaderHandler?.Invoke(source, chapters, startIndex);

    public static void NotifyDownloadedKeysChanged() => DownloadedKeysChangedHandler?.Invoke();
}
