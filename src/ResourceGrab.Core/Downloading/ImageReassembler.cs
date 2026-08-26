using ResourceGrab.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ResourceGrab.Core.Downloading;

/// <summary>
/// 图片保存与分块重组（对应原 Rust 实现 save_image）。
/// 禁漫的图片被切成 block_num 块并乱序存放，下载后需要按算法重新拼接。
/// </summary>
public static class ImageReassembler
{
    public static void SaveImage(string savePath, DownloadFormat downloadFormat, uint blockNum, byte[] imageData)
    {
        // 先写临时文件再原子改名：保证目标路径上出现的文件必然是完整的，
        // 断点续传时「文件存在且非空」即可安全跳过，不会把半截文件误判为已完成。
        var tmpPath = savePath + ".tmp";
        try
        {
            // block_num 为 0 表示未分块，直接保存
            if (blockNum == 0)
            {
                File.WriteAllBytes(tmpPath, imageData);
                File.Move(tmpPath, savePath, true);
                return;
            }

            using var srcImg = Image.Load<Rgba32>(imageData);
            using var dstImg = ReassemblePixels(srcImg, blockNum);

            // 按下载格式保存（先写临时文件）
            switch (downloadFormat)
            {
                case DownloadFormat.Jpeg:
                    dstImg.SaveAsJpeg(tmpPath);
                    break;
                case DownloadFormat.Png:
                    // PNG 使用最高压缩质量，否则体积会很大
                    var pngEncoder = new PngEncoder
                    {
                        CompressionLevel = PngCompressionLevel.BestCompression,
                        ColorType = PngColorType.RgbWithAlpha,
                    };
                    dstImg.Save(tmpPath, pngEncoder);
                    break;
                case DownloadFormat.Webp:
                    dstImg.Save(tmpPath, new WebpEncoder());
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(downloadFormat));
            }

            File.Move(tmpPath, savePath, true);
        }
        catch
        {
            TryDelete(tmpPath);
            throw;
        }
    }

    /// <summary>
    /// 内存重组（在线阅读用）：把分块图片重组为完整图片字节，保持原始编码格式；未分块时原样返回。
    /// </summary>
    public static byte[] Reassemble(byte[] imageData, uint blockNum)
    {
        if (blockNum == 0)
        {
            return imageData;
        }

        using var srcImg = Image.Load<Rgba32>(imageData);
        var format = srcImg.Metadata.DecodedImageFormat;
        if (format is null)
        {
            throw new InvalidOperationException("无法识别图片格式");
        }
        using var dstImg = ReassemblePixels(srcImg, blockNum);
        using var ms = new MemoryStream();
        dstImg.Save(ms, format);
        return ms.ToArray();
    }

    /// <summary>把乱序分块的源图重组为完整图像（调用者负责释放返回的 Image）。</summary>
    private static Image<Rgba32> ReassemblePixels(Image<Rgba32> srcImg, uint blockNum)
    {
        var width = srcImg.Width;
        var height = srcImg.Height;

        // 创建目标图，尺寸与原图相同
        var dstImg = new Image<Rgba32>(width, height);

        // 计算原图高度除以 num 的余数
        var remainderHeight = (uint)(height % blockNum);

        // 将图片切分为 blockNum 块并拼接
        for (uint i = 0; i < blockNum; i++)
        {
            // 当前块的标准高度
            var blockHeight = (uint)(height / blockNum);
            // 源图像中当前块的 Y 轴起点位置（从底部往上取块）
            var srcYStart = (uint)(height - blockHeight * (i + 1) - remainderHeight);
            // 目标图像中当前块的 Y 轴起点位置
            var dstYStart = blockHeight * i;

            // 第一块需要加上余数高度，以确保拼接完整
            if (i == 0)
            {
                blockHeight += remainderHeight;
            }
            else
            {
                dstYStart += remainderHeight;
            }

            // 从原图裁剪出当前块
            using var cropped = srcImg.Clone(ctx => ctx.Crop(new Rectangle(0, (int)srcYStart, width, (int)blockHeight)));
            // 将裁剪出的块直接拷贝到目标图的对应位置（等价 Rust copy_from，不做混合）
            cropped.ProcessPixelRows(dstImg, (srcRows, dstRows) =>
            {
                for (var y = 0; y < (int)blockHeight; y++)
                {
                    srcRows.GetRowSpan(y).CopyTo(dstRows.GetRowSpan((int)(dstYStart + (uint)y)));
                }
            });
        }

        return dstImg;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }
}