using ResourceGrab.Core.Downloading;
using ResourceGrab.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ResourceGrab.Core.Tests.Downloading;

// 分块乱序图还原：禁漫把整图按 blockNum 切块、从底部往上乱序存放。
// 用「每行一个独立颜色」的合成图做像素级断言，验证还原后的行序。

public class ImageReassemblerTests
{
    /// <summary>生成宽 2、每行单色的 PNG；第 r 行红色通道值 = r+1。</summary>
    private static byte[] EncodeRowStripedPng(int rows)
    {
        using var img = new Image<Rgba32>(2, rows);
        for (var r = 0; r < rows; r++)
        {
            for (var x = 0; x < img.Width; x++)
            {
                img[x, r] = new Rgba32((byte)(r + 1), 0, 0, 255);
            }
        }
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    /// <summary>解码 PNG 并返回每行的红色通道值（每行必须同色）。</summary>
    private static byte[] DecodeRowColors(byte[] png)
    {
        using var img = Image.Load<Rgba32>(png);
        var colors = new byte[img.Height];
        for (var r = 0; r < img.Height; r++)
        {
            for (var x = 1; x < img.Width; x++)
            {
                Assert.Equal(img[0, r].R, img[x, r].R);
            }
            colors[r] = img[0, r].R;
        }
        return colors;
    }

    [Fact]
    public void Reassemble_BlockNumZero_ReturnsOriginalBytes()
    {
        var png = EncodeRowStripedPng(8);
        Assert.Same(png, ImageReassembler.Reassemble(png, 0)); // 未分块：引用相等，零拷贝
    }

    [Fact]
    public void Reassemble_EvenSplit_BottomBlockGoesFirst()
    {
        // 8 行切 4 块（每块 2 行）：源块自底向上依次放到目标顶部 → 输出行序 [6,7,4,5,2,3,0,1]
        var png = EncodeRowStripedPng(8);
        var result = DecodeRowColors(ImageReassembler.Reassemble(png, 4));
        Assert.Equal(new byte[] { 7, 8, 5, 6, 3, 4, 1, 2 }, result);
    }

    [Fact]
    public void Reassemble_WithRemainder_FirstBlockTakesExtraRows()
    {
        // 10 行切 4 块（块高 2 余 2）：首块 4 行（源底 4 行），其后每块 2 行 → [6..9, 4,5, 2,3, 0,1]
        var png = EncodeRowStripedPng(10);
        var result = DecodeRowColors(ImageReassembler.Reassemble(png, 4));
        Assert.Equal(new byte[] { 7, 8, 9, 10, 5, 6, 3, 4, 1, 2 }, result);
    }

    [Fact]
    public void Reassemble_OutputDimensions_MatchInput()
    {
        var png = EncodeRowStripedPng(10);
        using var img = Image.Load<Rgba32>(ImageReassembler.Reassemble(png, 4));
        Assert.Equal(2, img.Width);
        Assert.Equal(10, img.Height);
    }

    [Fact]
    public void SaveImage_BlockNumZero_WritesBytesAtomically()
    {
        var path = Path.Combine(Path.GetTempPath(), $"reassembler-{Guid.NewGuid():N}.jpg");
        try
        {
            var payload = new byte[] { 1, 2, 3, 4 };
            ImageReassembler.SaveImage(path, DownloadFormat.Jpeg, 0, payload);
            Assert.Equal(payload, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp")); // 临时文件不留痕迹
        }
        finally
        {
            File.Delete(path);
        }
    }
}
