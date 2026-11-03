using System.Windows;
using System.Windows.Media;

namespace ResourceGrab.App.Common;

/// <summary>
/// 短剧封面兜底：站点封面 CDN（pic.drleao.cn）目前对合法路径返回的是加密字节，
/// 任何客户端都解不出图（实测浏览器下载到的文件与本机请求逐字节相同，WPF/GDI+ 均无法解码）。
/// 这里按剧集 id 生成**确定性**渐变色，保证同一部剧每次刷新颜色一致，
/// 真图恢复后图片层会自然盖上去，不需要改调用方。
/// </summary>
public static class DramaCoverPlaceholder
{
    private static readonly Dictionary<string, Brush> Cache = [];

    /// <summary>同一 key 永远得到同一个画刷（带缓存，网格滚动时不重复计算）。</summary>
    public static Brush Build(string? key)
    {
        var k = string.IsNullOrEmpty(key) ? "default" : key;
        lock (Cache)
        {
            if (Cache.TryGetValue(k, out var cached)) return cached;
            var brush = Create(k);
            Cache[k] = brush;
            return brush;
        }
    }

    /// <summary>兜底层中间显示的首字。</summary>
    public static string FirstChar(string? title)
        => string.IsNullOrWhiteSpace(title) ? "剧" : title.Trim()[..1].ToUpperInvariant();

    private static Brush Create(string key)
    {
        var hash = 0;
        foreach (var c in key) hash = unchecked(hash * 31 + c);
        var hue = Math.Abs(hash) % 360;

        var start = FromHsl(hue, 0.30, 0.44);
        var end = FromHsl((hue + 42) % 360, 0.28, 0.30);
        var brush = new LinearGradientBrush(start, end, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    private static Color FromHsl(double hueDegrees, double saturation, double lightness)
    {
        var h = hueDegrees / 360.0;
        double r, g, b;
        if (saturation <= 0)
        {
            r = g = b = lightness;
        }
        else
        {
            var q = lightness < 0.5
                ? lightness * (1 + saturation)
                : lightness + saturation - lightness * saturation;
            var p = 2 * lightness - q;
            r = HueToRgb(p, q, h + 1.0 / 3);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3);
        }
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
