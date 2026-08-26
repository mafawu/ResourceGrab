using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ResourceGrab.Core.Utils;

/// <summary>
/// WebP 解码辅助：用 ImageSharp 解码 WebP（本地文件或内存字节）并缩放到目标宽度，
/// 输出 Bgra32 像素数据（供 WPF 构造 BitmapSource，无需 PNG/JPEG 转码中间步骤）。
/// 用于系统 WIC 不支持 WebP 时的兜底路径。
/// </summary>
public static class WebpImageDecoder
{
    public sealed record DecodedImage(int Width, int Height, byte[] BgraPixels);

    /// <summary>解码 WebP 文件；宽超限时按比例缩放到 maxWidth。失败返回 null。</summary>
    public static DecodedImage? Decode(string path, int maxWidth)
        => DecodeCore(() => Image.Load<Bgra32>(path), maxWidth);

    /// <summary>解码 WebP 字节流（在线阅读内存链路）；宽超限时按比例缩放到 maxWidth。失败返回 null。</summary>
    public static DecodedImage? Decode(byte[] bytes, int maxWidth)
        => DecodeCore(() => Image.Load<Bgra32>(bytes), maxWidth);

    private static DecodedImage? DecodeCore(Func<Image<Bgra32>> load, int maxWidth)
    {
        try
        {
            using var image = load();
            if (image.Width > maxWidth)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(maxWidth, maxWidth),
                    Mode = ResizeMode.Max,
                }));
            }

            var pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);
            return new DecodedImage(image.Width, image.Height, pixels);
        }
        catch
        {
            // 文件损坏 / 不是有效 WebP 时返回 null，由调用方决定占位处理
            return null;
        }
    }
}