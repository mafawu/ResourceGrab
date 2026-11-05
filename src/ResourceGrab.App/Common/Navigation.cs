using ResourceGrab.Core.Models;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Sources.Novel;

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
    public static Action<string, int>? OpenNovelReaderAtHandler { get; set; }
    public static Action<INovelSource, NovelSummary>? OpenNovelBookHandler { get; set; }
    public static Action<INovelSource, NovelDetail, int>? OpenNovelOnlineReaderHandler { get; set; }
    public static Action<string, string, string, string>? OpenNovelBookAtHandler { get; set; }
    public static Action<string>? OpenNovelTagHandler { get; set; }
    public static Action? GoNovelOnlineHandler { get; set; }
    public static Action? DownloadedKeysChangedHandler { get; set; }

    /// <summary>打开在线书籍详情页。</summary>
    public static void OpenNovelBook(INovelSource source, NovelSummary summary)
        => OpenNovelBookHandler?.Invoke(source, summary);

    /// <summary>按「源 + 书 + 章」打开详情页并直接续读（阅读历史用）。</summary>
    public static void OpenNovelBookAt(string sourceId, string novelId, string chapterId, string title)
        => OpenNovelBookAtHandler?.Invoke(sourceId, novelId, chapterId, title);

    /// <summary>跳到「在线书库」页（书架空白态 / 详情页的"去逛逛"）。</summary>
    public static void GoNovelOnline() => GoNovelOnlineHandler?.Invoke();

    /// <summary>点书名下的标签：命中分类就跳分类页，否则按标签搜索。</summary>
    public static void OpenNovelTag(string tag) => OpenNovelTagHandler?.Invoke(tag);

    /// <summary>从头（或指定章节）打开在线阅读器。</summary>
    public static void OpenNovelOnlineReader(INovelSource source, NovelDetail detail, int chapterIndex)
        => OpenNovelOnlineReaderHandler?.Invoke(source, detail, chapterIndex);

    /// <summary>打开本地小说，并从指定字符偏移开始（本地章节跳转用）。</summary>
    public static void OpenNovelReaderAt(string path, int charOffset)
        => OpenNovelReaderAtHandler?.Invoke(path, charOffset);

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
