using ResourceGrab.App.Common;
using ResourceGrab.Core.Sources;

namespace ResourceGrab.App.Services;

/// <summary>
/// 当前内容源管理：持有全部已注册源与当前选中源。
/// 顶部下拉切换 Current；能力驱动导航与各页面据此取源。
/// </summary>
public class SourceManager : ObservableObject
{
    private readonly IReadOnlyList<IComicSource> _sources;
    private IComicSource _current;

    public SourceManager(IEnumerable<IComicSource> sources)
    {
        _sources = sources.ToList();
        _current = _sources.FirstOrDefault(s => s.Info.Id == "jm") ?? _sources[0];
    }

    public IReadOnlyList<IComicSource> Sources => _sources;

    public IComicSource Current
    {
        get => _current;
        set
        {
            if (ReferenceEquals(_current, value))
            {
                return;
            }
            _current = value;
            OnPropertyChanged(nameof(Current));
            CurrentChanged?.Invoke();
        }
    }

    /// <summary>按源 id 取源；未知 id 回退到当前源。</summary>
    public IComicSource Get(string sourceId)
        => _sources.FirstOrDefault(s => s.Info.Id == sourceId) ?? _current;

    public event Action? CurrentChanged;
}
