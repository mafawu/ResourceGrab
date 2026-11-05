using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Sources.Novel;

namespace ResourceGrab.App.Services;

/// <summary>
/// 小说书源管理：与漫画的 <see cref="SourceManager"/> 同构，但只装 <see cref="INovelSource"/>。
/// 单独一份是为了不把小说源混进漫画的聚合搜索 / 分类 / 排行。
/// </summary>
public class NovelSourceManager
{
    private readonly IReadOnlyList<INovelSource> _sources;
    private INovelSource? _current;

    public NovelSourceManager(IEnumerable<INovelSource> sources)
    {
        _sources = sources.ToList();
        _current = _sources.FirstOrDefault();
    }

    public IReadOnlyList<INovelSource> Sources => _sources;

    public INovelSource? Current
    {
        get => _current ?? _sources.FirstOrDefault();
        set => _current = value;
    }

    public INovelSource? Get(string sourceId) =>
        _sources.FirstOrDefault(s => s.Info.Id == sourceId) ?? Current;

    public bool HasSources => _sources.Count > 0;
}
