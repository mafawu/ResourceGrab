using System.Text.Json.Serialization;

namespace ResourceGrab.Core.Models;

public class NovelReaderSettings
{
    [JsonPropertyName("isScrollMode")] public bool IsScrollMode { get; set; } = false;
    [JsonPropertyName("bgIndex")] public int BgIndex { get; set; } = 0;
    [JsonPropertyName("fontSize")] public double FontSize { get; set; } = 14;
    [JsonPropertyName("charsPerPage")] public int CharsPerPage { get; set; } = 1000;
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
