using System.Windows.Media;

namespace ResourceGrab.App.Themes;

/// <summary>
/// 矢量图标库（线性风格，24×24 viewbox）。
/// XAML 中通过 <see cref="System.Windows.Shapes.Path"/> + StaticResource 使用（见 Themes/Icons.xaml），
/// 本类提供等价 Geometry 常量供 C# 动态构建 UI 使用。
/// </summary>
public static class Icons
{
    private static Geometry G(string data) => Geometry.Parse(data);

    public static Geometry Search => G("M 10.5 3.5 a 7 7 0 1 0 0 14 a 7 7 0 1 0 0 -14 M 15.5 15.5 L 20.5 20.5");
    public static Geometry FavoriteStar => G("M 12 3 L 14.8 9.2 L 21.5 9.9 L 16.6 14.6 L 18 21.2 L 12 17.8 L 6 21.2 L 7.4 14.6 L 2.5 9.9 L 9.2 9.2 Z");
    public static Geometry Library => G("M 5.5 3.5 L 5.5 20.5 M 12 3.5 L 12 20.5 M 18.5 3.5 L 18.5 20.5 M 3 20.5 L 21 20.5");
    public static Geometry Download => G("M 12 3.5 L 12 14.5 M 7.5 10 L 12 14.5 L 16.5 10 M 4.5 19.5 L 19.5 19.5");
    public static Geometry Sun => G("M 12 4 a 8 8 0 1 0 0 16 a 8 8 0 1 0 0 -16 M 12 1.5 L 12 4 M 12 20 L 12 22.5 M 1.5 12 L 4 12 M 20 12 L 22.5 12 M 4.4 4.4 L 6.2 6.2 M 17.8 17.8 L 19.6 19.6 M 4.4 19.6 L 6.2 17.8 M 17.8 6.2 L 19.6 4.4");
    public static Geometry Moon => G("M 20.2 14.5 A 8 8 0 1 1 9.5 3.8 A 7 7 0 0 0 20.2 14.5");
    public static Geometry FolderOpen => G("M 3 7 L 10 7 L 12 9 L 21 9 L 21 19 L 3 19 Z M 3 7 L 3 5.5 L 10 5.5 L 12 7.5 L 21 7.5");
    public static Geometry Back => G("M 15 5 L 8 12 L 15 19");
    public static Geometry Refresh => G("M 12 4.5 A 7.5 7.5 0 0 1 19.5 12 M 20.5 3.5 L 20.5 8.5 L 15.5 8.5");
    public static Geometry Close => G("M 6 6 L 18 18 M 18 6 L 6 18");
    public static Geometry Check => G("M 5 12.5 L 10 17.5 L 19 6.5");
    public static Geometry Info => G("M 12 3.5 a 8.5 8.5 0 1 0 0 17 a 8.5 8.5 0 1 0 0 -17 M 12 11 L 12 16.5 M 12 7.5 L 12 8.2");
    public static Geometry SignOut => G("M 10 4 L 4 4 L 4 20 L 10 20 M 14 8 L 19 12 L 14 16 M 19 12 L 7.5 12");
    public static Geometry ChevronLeft => G("M 14 6 L 8 12 L 14 18");
    public static Geometry ChevronRight => G("M 10 6 L 16 12 L 10 18");
    public static Geometry Setting => G("M 12 3 L 12 5.5 M 12 18.5 L 12 21 M 3 12 L 5.5 12 M 18.5 12 L 21 12 M 5.6 5.6 L 7.4 7.4 M 16.6 16.6 L 18.4 18.4 M 5.6 18.4 L 7.4 16.6 M 16.6 7.4 L 18.4 5.6 M 12 8.5 a 3.5 3.5 0 1 0 0 7 a 3.5 3.5 0 1 0 0 -7");
    public static Geometry Heart => G("M 12 19.5 C 6.5 15 3.5 11.5 3.5 8.5 C 3.5 6 5.5 4 8 4 C 9.5 4 11 4.8 12 6.2 C 13 4.8 14.5 4 16 4 C 18.5 4 20.5 6 20.5 8.5 C 20.5 11.5 17.5 15 12 19.5 Z");
    public static Geometry Minimize => G("M 5 12 L 19 12");
    public static Geometry Maximize => G("M 7 7 L 17 7 L 17 17 L 7 17 Z");
    public static Geometry Restore => G("M 5.5 5.5 L 13.5 5.5 L 13.5 13.5 L 5.5 13.5 Z M 8 8 L 16 8 L 16 16 L 8 16 Z");
    public static Geometry CloseWindow => G("M 6.5 6.5 L 17.5 17.5 M 17.5 6.5 L 6.5 17.5");
    public static Geometry Rank => G("M 4 20 L 4 13 M 10 20 L 10 7 M 16 20 L 16 10 M 3 20 L 21 20");
    public static Geometry Category => G("M 4 4 L 10.5 4 L 10.5 10.5 L 4 10.5 Z M 13.5 4 L 20 4 L 20 10.5 L 13.5 10.5 Z M 4 13.5 L 10.5 13.5 L 10.5 20 L 4 20 Z M 13.5 13.5 L 20 13.5 L 20 20 L 13.5 20 Z");
    public static Geometry Weekly => G("M 4 6.5 L 20 6.5 L 20 20 L 4 20 Z M 4 10.5 L 20 10.5 M 8 3.5 L 8 8.5 M 16 3.5 L 16 8.5 M 9.5 15 L 11.5 17 L 15 13");
    public static Geometry Play => G("M 8 5 L 19 12 L 8 19 Z");
    public static Geometry Document => G("M 6 3.5 L 12 3.5 L 18 9.5 L 18 20.5 L 6 20.5 Z M 12 3.5 L 12 9.5 L 18 9.5");
    public static Geometry Loading => G("M 12 4 A 8 8 0 1 1 4.5 8.5");
    public static Geometry PanelToggle => G("M 4.5 4.5 L 19.5 4.5 L 19.5 19.5 L 4.5 19.5 Z M 13 4.5 L 13 19.5");
    public static Geometry Local => G("M 4.4 5.6 Q 4.4 3.6 6.4 3.6 H 17.6 Q 19.6 3.6 19.6 5.6 V 18.4 Q 19.6 20.4 17.6 20.4 H 6.4 Q 4.4 20.4 4.4 18.4 Z M 4.4 11.4 L 19.6 11.4 M 9.4 7.6 L 14.6 7.6 M 9.4 15.4 L 14.6 15.4");
    public static Geometry HeartOutline => G("M 12 19.5 C 6.5 15 3.5 11.5 3.5 8.5 C 3.5 6 5.5 4 8 4 C 9.5 4 11 4.8 12 6.2 C 13 4.8 14.5 4 16 4 C 18.5 4 20.5 6 20.5 8.5 C 20.5 11.5 17.5 15 12 19.5 Z");
    public static Geometry HeartFilled => Heart;
    public static Geometry StarFilled => FavoriteStar;
    public static Geometry ChevronUp => G("M 6 14 L 12 8 L 18 14");
    public static Geometry ChevronDown => G("M 6 10 L 12 16 L 18 10");
    public static Geometry History => G("M 12 4 A 8 8 0 1 1 4.5 8.5 M 12 8 L 12 12 L 15.5 14");
    public static Geometry TaskProgress => G("M 4 6 L 20 6 M 4 12 L 14 12 M 4 18 L 17 18 M 17.5 15.5 L 19.5 17.5 L 22 13");
    public static Geometry FolderPlus => G("M 3 7 L 10 7 L 12 9 L 21 9 L 21 19 L 3 19 Z M 3 7 L 3 5.5 L 10 5.5 L 12 7.5 L 21 7.5 M 12 12 L 12 16 M 10 14 L 14 14");
}
