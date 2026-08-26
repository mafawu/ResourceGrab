using System.Security.Cryptography;
using System.Text;

namespace ResourceGrab.Core.Utils;

public static class Md5Util
{
    /// <summary>
    /// 计算字符串的 MD5 哈希，返回小写十六进制字符串（与原 Rust 实现一致）。
    /// </summary>
    public static string Hex(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }
}
