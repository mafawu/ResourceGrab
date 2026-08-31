using ResourceGrab.Core.Services;
using Xunit;

namespace ResourceGrab.Core.Tests.Services;

/// <summary>
/// 严格模式：只认文件名本身的番号，不做父目录兜底——
/// 文件夹内的广告/预告/任意命名杂片不应借用文件夹番号入库被误刮削。
/// </summary>
public class VideoNumberParserStrictnessTests
{
    [Theory]
    [InlineData(@"E:\下载\odnbt.com@MVSD-669\MVSD-669.mp4", "MVSD-669")]
    [InlineData(@"E:\下载\MVSD_669 1080p.mp4", "MVSD-669")]
    [InlineData(@"E:\下载\fc2-1234567.mp4", "FC2-1234567")]
    [InlineData(@"E:\下载\987654-321.mp4", "987654-321")]
    public void Parse_FilenameWithNumber_Succeeds(string filePath, string expected)
    {
        Assert.Equal(expected, VideoNumberParser.Parse(filePath).Number);
    }

    [Theory]
    [InlineData(@"E:\下载\odnbt.com@MVSD-669\AI生成視頻.mp4")]
    [InlineData(@"E:\下载\odnbt.com@MVSD-669\吃瓜爆料免费观看.mp4")]
    [InlineData(@"E:\下载\odnbt.com@MVSD-669\CD1.mp4")]
    [InlineData(@"E:\下载\sample.mp4")]
    [InlineData(@"E:\下载\trailer.mp4")]
    public void Parse_FilenameWithoutNumber_ReturnsEmpty_EvenInsideNumberedFolder(string filePath)
    {
        Assert.Equal("", VideoNumberParser.Parse(filePath).Number);
    }
}
