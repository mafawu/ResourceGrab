using System.Text.Json;
using System.Text.RegularExpressions;
using ResourceGrab.Core.Models;

namespace ResourceGrab.Core.Services;

public class NovelReaderSettingsService
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;
    public NovelReaderSettings Current { get; private set; } = new();

    public NovelReaderSettingsService() : this(AppPaths.NovelReaderSettingsPath) { }
    public NovelReaderSettingsService(string path)
    {
        _path = path;
        Load();
        if (MigrateLayout(Current)) { Normalize(Current); Save(); }
        else Normalize(Current);
    }

    /// <summary>
    /// 老配置（layoutVersion = 0）用的还是紧凑排版（14px / 1.6 行距），
    /// 升级到新阅读页的一次性迁移：只改字号与行距，其它用户选择都不动。
    /// </summary>
    private static bool MigrateLayout(NovelReaderSettings settings)
    {
        if (settings.LayoutVersion >= 1) return false;

        // 只把"还在用旧默认值"的配置升到新排版；用户自己调到 16 以上的保持不动
        if (settings.FontSize <= 14.5) settings.FontSize = 17;
        if (settings.LineHeight < 1.75) settings.LineHeight = 1.9;
        settings.LayoutVersion = 1;
        return true;
    }

    public void Update(Action<NovelReaderSettings> edit)
    {
        edit(Current);
        Normalize(Current);
        Save();
    }

    public void SetBg(int idx) => Update(s => s.BgIndex = idx);
    public void SetScrollMode(bool v) => Update(s => s.IsScrollMode = v);
    public void SetFontSize(double v) => Update(s => s.FontSize = v);

    private static void Normalize(NovelReaderSettings settings)
    {
        settings.FontSize = double.IsNaN(settings.FontSize) || settings.FontSize <= 0
            ? 17
            : Math.Clamp(settings.FontSize, 10, 24);
        settings.LineHeight = settings.LineHeight < 1.0 || settings.LineHeight > 2.5 ? 1.9 : settings.LineHeight;
        settings.ParagraphIndentEm = Math.Clamp(settings.ParagraphIndentEm, 0, 8);
        settings.CharsPerPage = Math.Clamp(settings.CharsPerPage, 200, 5000);
        if (string.IsNullOrWhiteSpace(settings.FontFamily))
        {
            settings.FontFamily = "Microsoft YaHei UI";
        }
        if (string.IsNullOrWhiteSpace(settings.ChapterPattern))
        {
            settings.ChapterPattern = @"^\s*(第[0-9零一二三四五六七八九十百千万]+[章节卷回部]|Chapter\s+\d+).*$";
        }
        else
        {
            try { _ = new Regex(settings.ChapterPattern); }
            catch
            {
                settings.ChapterPattern = @"^\s*(第[0-9零一二三四五六七八九十百千万]+[章节卷回部]|Chapter\s+\d+).*$";
            }
        }
        settings.BgIndex = Math.Clamp(settings.BgIndex, 0, NovelReaderBgPresets.Presets.Length - 1);
    }

    private void Load()
    {
        try { if (File.Exists(_path)) { var j = File.ReadAllText(_path); var s = JsonSerializer.Deserialize<NovelReaderSettings>(j, Opts); if (s != null) Current = s; } } catch { }
    }
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, Opts));
        }
        catch { }
    }
}
