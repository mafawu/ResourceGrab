using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ResourceGrab.App.Services;

/// <summary>
/// 离线封面：没有网络 / 还没刮到封面时，用书名生成一张稳定的"书封"。
/// 同一本书永远同一套配色（按书名哈希），所以翻页、重启都不会变样。
/// </summary>
public static class NovelCoverArt
{
    private const int Width = 320;
    private const int Height = 184;

    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource Get(string title)
    {
        var key = string.IsNullOrWhiteSpace(title) ? "?" : title.Trim();
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        var image = Render(key);
        lock (Cache) Cache[key] = image;
        return image;
    }

    private static ImageSource Render(string title)
    {
        var hash = StableHash(title);
        var hue = hash % 360;
        var top = FromHsv(hue, 0.52, 0.46);
        var bottom = FromHsv((hue + 38) % 360, 0.62, 0.26);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, Width, Height);
            dc.DrawRectangle(new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(1, 1)), null, rect);

            // 斜向高光，避免纯色块太"死"
            var sheen = new LinearGradientBrush(
                Color.FromArgb(38, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                new Point(0, 0), new Point(1, 1));
            dc.DrawRectangle(sheen, null, rect);

            // 右下角的大字：书名首字，低透明度当水印
            var glyph = title.Length >= 2 ? title[..2] : title;
            var text = new FormattedText(glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                84, new SolidColorBrush(Color.FromArgb(58, 255, 255, 255)), 1.0)
            {
                MaxTextWidth = Width - 20,
                MaxTextHeight = Height - 12,
                TextAlignment = TextAlignment.Right,
            };
            dc.DrawText(text, new Point(0, Height - text.Height - 6));
        }

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 17;
            foreach (var ch in text) hash = hash * 31 + ch;
            return Math.Abs(hash);
        }
    }

    private static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
