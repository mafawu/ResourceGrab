using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace ResourceGrab.Core.Services;

public static class VideoImageService
{
    /// <summary>JavBus 横版封面右侧通常是海报主体；按高度全高、约 2:3 裁剪。</summary>
    public static async Task<string> CreatePosterAsync(string fanartPath, string outputPath, CancellationToken ct)
    {
        using var image = await SixLabors.ImageSharp.Image.LoadAsync(fanartPath, ct);
        var width = Math.Min(image.Width, Math.Max(1, (int)Math.Round(image.Height * 2.0 / 3)));
        var rect = new Rectangle(image.Width - width, 0, width, image.Height);
        image.Mutate(ctx => ctx.Crop(rect));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await image.SaveAsJpegAsync(outputPath, new JpegEncoder { Quality = 88 }, ct);
        return outputPath;
    }
}
