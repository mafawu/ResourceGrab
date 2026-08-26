using System.Text.Json;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services;

public class NovelReaderSettingsService
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;
    public NovelReaderSettings Current { get; private set; } = new();

    public NovelReaderSettingsService() : this(AppPaths.NovelReaderSettingsPath) { }
    public NovelReaderSettingsService(string path) { _path = path; Load(); }

    public void Update(Action<NovelReaderSettings> edit)
    {
        edit(Current);
        Save();
    }

    public void SetBg(int idx) { Current.BgIndex = Math.Clamp(idx, 0, NovelReaderBgPresets.Presets.Length-1); Save(); }
    public void SetScrollMode(bool v) { Current.IsScrollMode = v; Save(); }
    public void SetFontSize(double v) { Current.FontSize = Math.Clamp(v, 10, 24); Save(); }

    private void Load()
    {
        try { if (File.Exists(_path)) { var j = File.ReadAllText(_path); var s = JsonSerializer.Deserialize<NovelReaderSettings>(j, Opts); if (s != null) Current = s; } } catch { }
    }
    private void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? "."); File.WriteAllText(_path, JsonSerializer.Serialize(Current, Opts)); } catch { }
    }
}
