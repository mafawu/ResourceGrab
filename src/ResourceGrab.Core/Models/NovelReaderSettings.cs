using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Models;

public class NovelReaderSettings
{
    /// <summary>排版版本：0 = 旧版紧凑排版，1 = 新版居中大字号。用于一次性升级老配置。</summary>
    [JsonPropertyName("layoutVersion")] public int LayoutVersion { get; set; }

    [JsonPropertyName("isScrollMode")] public bool IsScrollMode { get; set; } = false;
    [JsonPropertyName("bgIndex")] public int BgIndex { get; set; } = 0;
    [JsonPropertyName("fontSize")] public double FontSize { get; set; } = 17;
    [JsonPropertyName("charsPerPage")] public int CharsPerPage { get; set; } = 1000;
    [JsonPropertyName("fontFamily")] public string FontFamily { get; set; } = "Microsoft YaHei UI";
    [JsonPropertyName("lineHeight")] public double LineHeight { get; set; } = 1.9;
    [JsonPropertyName("paragraphIndentEm")] public double ParagraphIndentEm { get; set; } = 2;
    [JsonPropertyName("autoDetectEncoding")] public bool AutoDetectEncoding { get; set; } = true;
    [JsonPropertyName("chapterPattern")] public string ChapterPattern { get; set; } = @"^\s*(第[0-9零一二三四五六七八九十百千万]+[章节卷回部]|Chapter\s+\d+).*$";
}

public static class NovelReaderBgPresets
{
    public static readonly (string Bg, string Fg, string Name)[] Presets = new[]
    {
        ("#FFFFFF", "#1A1A1A", "白"),
        ("#FDF6E3", "#5B4636", "纸"),
        ("#EEF5E2", "#2E3B2E", "绿"),
        ("#1A1A1E", "#D8D8D8", "夜"),
        ("#2C2C30", "#CFCFCF", "灰"),
    };
}
