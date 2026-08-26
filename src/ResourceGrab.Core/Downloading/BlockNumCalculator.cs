using ResourceGrab.Core.Utils;

namespace ResourceGrab.Core.Downloading;

/// <summary>
/// 计算图片分块数（对应原 Rust 实现 calculate_block_num）。
/// </summary>
public static class BlockNumCalculator
{
    public static uint Calculate(long scrambleId, long id, string filename)
    {
        if (id < scrambleId)
        {
            return 0;
        }
        if (id < 268_850)
        {
            return 10;
        }
        var x = id < 421_926 ? 10 : 8;
        var s = Md5Util.Hex($"{id}{filename}");
        var blockNum = s[^1];
        return (uint)(blockNum % x) * 2 + 2;
    }
}
