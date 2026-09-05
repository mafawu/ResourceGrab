using ResourceGrab.Core.Downloading;
using Xunit;

namespace ResourceGrab.Core.Tests.Downloading;

// 禁漫分块数计算：scrambleId 以下不分块；268850 以下固定 10 块；
// 以上按 md5(id+filename) 尾字符对 x（10 或 8）取模换算，期望值来自独立计算的 MD5 向量。

public class BlockNumCalculatorTests
{
    [Theory]
    [InlineData(100, 99, "1.jpg", 0u)] // id < scrambleId：新图不分块
    [InlineData(0, 0, "1.jpg", 10u)] // id == scrambleId 即进入老逻辑
    [InlineData(100, 268_849, "x.jpg", 10u)] // 268850 以下固定 10
    public void Calculate_LegacyRanges_ReturnsExpected(long scrambleId, long id, string filename, uint expected)
    {
        Assert.Equal(expected, BlockNumCalculator.Calculate(scrambleId, id, filename));
    }

    [Theory]
    // md5("4219261.jpg") 尾字符 '3'(51)：51 % 8 * 2 + 2 = 8（id >= 421926 用 x=8）
    [InlineData(268_850, 421_926, "1.jpg", 8u)]
    // md5("3000001.jpg") 尾字符 '9'(57)：57 % 10 * 2 + 2 = 16（id < 421926 用 x=10）
    [InlineData(268_850, 300_000, "1.jpg", 16u)]
    // md5("10000001.jpg") 尾字符 '8'(56)：56 % 8 * 2 + 2 = 2
    [InlineData(268_850, 1_000_000, "1.jpg", 2u)]
    public void Calculate_Md5Branch_MatchesIndependentVectors(long scrambleId, long id, string filename, uint expected)
    {
        Assert.Equal(expected, BlockNumCalculator.Calculate(scrambleId, id, filename));
    }

    [Theory]
    [InlineData(268_850, 268_850, 10)] // 老段（x=10）：块数 ∈ {2,4,...,20}
    [InlineData(268_850, 421_926, 8)] // 新段（x=8）：块数 ∈ {2,4,...,16}
    public void Calculate_Md5Branch_AlwaysEvenAndInRange(long scrambleId, long id, int x)
    {
        for (var i = 0; i < 64; i++)
        {
            var result = BlockNumCalculator.Calculate(scrambleId, id, $"file{i}.jpg");
            Assert.True(result >= 2 && result <= 2 * x, $"file{i}.jpg -> {result}");
            Assert.True(result % 2 == 0, $"file{i}.jpg -> {result}");
        }
    }
}
