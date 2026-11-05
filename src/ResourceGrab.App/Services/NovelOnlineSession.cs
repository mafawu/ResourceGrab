using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources.Novel;

namespace ResourceGrab.App.Services;

/// <summary>
/// 在线阅读会话：一本书 + 当前章 + 正文缓存。
/// 阅读器只跟这个对象打交道，换章、预取、离线都收敛在这里。
/// </summary>
public sealed class NovelOnlineSession
{
    private readonly NovelChapterCache _cache;
    private readonly NovelShelfService? _shelf;
    private readonly Dictionary<string, string> _memory = new(StringComparer.OrdinalIgnoreCase);

    public NovelOnlineSession(INovelSource source, NovelDetail detail, NovelChapterCache cache,
        NovelShelfService? shelf = null)
    {
        Source = source;
        Detail = detail;
        _cache = cache;
        _shelf = shelf;
    }

    public INovelSource Source { get; }

    public NovelDetail Detail { get; }

    public IReadOnlyList<NovelChapter> Chapters => Detail.Chapters;

    public int CurrentIndex { get; private set; }

    public bool HasPrev => CurrentIndex > 0;

    public bool HasNext => CurrentIndex < Chapters.Count - 1;

    /// <summary>阅读进度键：按"源 + 书 + 章"分开存，不与本地文件路径混。</summary>
    public string HistoryKey(int index)
    {
        if (index < 0 || index >= Chapters.Count) return "online:" + Source.Info.Id + ":" + Detail.Id;
        return $"online:{Source.Info.Id}:{Detail.Id}:{Chapters[index].Id}";
    }

    /// <summary>取正文：内存 → 磁盘缓存 → 站点；取到即写缓存。</summary>
    public async Task<string> LoadTextAsync(int index, CancellationToken ct = default)
    {
        if (Chapters.Count == 0) throw new InvalidOperationException("这本书没有可读章节");
        index = Math.Clamp(index, 0, Chapters.Count - 1);
        var chapter = Chapters[index];

        if (_memory.TryGetValue(chapter.Id, out var cachedText))
        {
            CurrentIndex = index;
            return cachedText;
        }

        var text = await _cache.TryReadAsync(Source.Info.Id, chapter.BookId, chapter.Key, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = await Source.GetChapterTextAsync(chapter, ct);
            await _cache.WriteAsync(Source.Info.Id, chapter.BookId, chapter.Key, text, ct);
        }

        _memory[chapter.Id] = text;
        CurrentIndex = index;
        RecordShelfProgress(index);
        return text;
    }

    /// <summary>书在书架里才记进度——没收藏的书不往书架塞数据。</summary>
    private void RecordShelfProgress(int index)
    {
        try
        {
            if (_shelf is null || index < 0 || index >= Chapters.Count) return;
            var chapter = Chapters[index];
            _shelf.RecordProgress(Source.Info.Id, Detail.Id, index + 1, chapter.Title, Chapters.Count);
        }
        catch
        {
            // 进度记录失败不影响阅读
        }
    }

    /// <summary>预取下一章：滚动到章尾前先请求，切章时不空白。</summary>
    public async Task PrefetchAsync(int index, CancellationToken ct = default)
    {
        try
        {
            if (index < 0 || index >= Chapters.Count) return;
            await LoadTextAsync(index, ct);
        }
        catch
        {
            // 预取失败不影响当前章节
        }
    }

    public bool IsCached(NovelChapter chapter) =>
        _memory.ContainsKey(chapter.Id) || _cache.Exists(Source.Info.Id, chapter.BookId, chapter.Key);

    public int CachedCount() => _cache.CountCached(Source.Info.Id, Detail.Id);
}
