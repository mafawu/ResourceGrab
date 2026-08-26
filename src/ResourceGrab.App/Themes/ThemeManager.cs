using System.IO;
using System.Text.Json;
using System.Windows;
using ResourceGrab.Core;

namespace ResourceGrab.App.Themes;

/// <summary>
/// 主题管理：动态切换浅色/深色配色字典（Colors.xaml ↔ DarkColors.xaml），
/// 并将偏好持久化到 AppDataDir/theme.json。
/// </summary>
public static class ThemeManager
{
    private static readonly string SettingsPath = Path.Combine(AppPaths.AppDataDir, "theme.json");

    public static bool IsDark { get; private set; }

    public static void Initialize()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                IsDark = JsonSerializer.Deserialize<bool>(File.ReadAllText(SettingsPath));
            }
        }
        catch
        {
            IsDark = false;
        }
        Apply(IsDark);
    }

    public static void Toggle() => Apply(!IsDark);

    public static void Apply(bool isDark)
    {
        IsDark = isDark;
        var app = Application.Current;
        if (app is not null)
        {
            var merged = app.Resources.MergedDictionaries;
            for (var i = 0; i < merged.Count; i++)
            {
                var source = merged[i].Source?.OriginalString;
                if (source is null ||
                    (!source.EndsWith("Colors.xaml") && !source.EndsWith("DarkColors.xaml")))
                {
                    continue;
                }
                // 兼容两种来源：
                //  - 主程序：相对 URI "Themes/Colors.xaml"（本程序集）
                //  - 派生 exe（copymanga 版）：绝对 pack URI 指向 ResourceGrab.App 程序集
                // 替换时保持与源相同的 URI 形式，确保跨程序集也能解析。
                var newSource = source.Contains(";component/")
                    ? $"pack://application:,,,/ResourceGrab.App;component/Themes/{(isDark ? "DarkColors.xaml" : "Colors.xaml")}"
                    : "Themes/" + (isDark ? "DarkColors.xaml" : "Colors.xaml");
                merged[i] = new ResourceDictionary
                {
                    Source = new Uri(newSource, UriKind.RelativeOrAbsolute),
                };
                break;
            }
        }
        Save();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(IsDark));
        }
        catch
        {
            // 忽略持久化失败
        }
    }
}
