using System.Security.Cryptography;
using System.Text;

namespace ResourceGrab.Core.Services.ShortDrama;

/// <summary>
/// 短剧封面解密器。
/// <para>
/// 站点把封面图**加密后**放在 CDN 上（响应 `Content-Type: binary/octet-stream`，
/// 字节熵 7.997、长度恒为 16 的整数倍、ETag 就是密文 md5）。
/// 浏览器靠 <c>/static/web/js/plugins/crypto-worker.js</c> 在本地解密：
/// AES-128-CBC、NoPadding，密钥与 IV 是写死在 worker 里的常量
/// （<c>media_key: "102_53_100_57_..."</c> 这种下划线分隔的字符码）。
/// </para>
/// <para>
/// 这里把同一套参数用 C# 复刻：解密后按文件尾的 EOI/IEND 裁掉块对齐补的尾部字节。
/// 参数若哪天变了，只改本文件的 <see cref="KeyText"/> / <see cref="IvText"/> 即可。
/// </para>
/// </summary>
public static class DramaCoverCipher
{
    // 来自 crypto-worker.js：const i0 = { mode:"CBC", media_key:"102_53_...", media_iv:"57_55_..." }
    private const string KeyText = "f5d965df75336270";   // 16 字节 → AES-128
    private const string IvText = "97b60394abc2fbe1";    // 16 字节

    /// <summary>响应本身就是明文图（站点以后可能改回直出），直接用。</summary>
    public static bool IsPlainImage(ReadOnlySpan<byte> data) => ImageMagicLength(data) > 0;

    /// <summary>
    /// 把 CDN 返回的原始字节变成可解码的图片字节。
    /// 已经是明文图就原样返回；否则按 AES-128-CBC 解密并裁到图片结束标记。
    /// 解不出来返回 null（调用方继续用渐变兜底）。
    /// </summary>
    public static byte[]? TryDecrypt(byte[] data)
    {
        if (data is null || data.Length < 16) return null;
        if (IsPlainImage(data)) return data;

        // CBC 要求 16 字节对齐；对不上说明不是这套加密
        if (data.Length % 16 != 0) return null;

        byte[] plain;
        try
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;      // worker 用的就是 NoPadding
            aes.Key = Encoding.ASCII.GetBytes(KeyText);
            aes.IV = Encoding.ASCII.GetBytes(IvText);
            using var decryptor = aes.CreateDecryptor();
            plain = decryptor.TransformFinalBlock(data, 0, data.Length);
        }
        catch (CryptographicException)
        {
            return null;
        }

        var magicLength = ImageMagicLength(plain);
        if (magicLength == 0) return null;

        return TrimToEnd(plain, magicLength);
    }

    /// <summary>识别图片头，返回文件头长度（0 = 不认识）。</summary>
    private static int ImageMagicLength(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return 3;          // JPEG
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return 8; // PNG
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
            && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return 12;     // WebP
        if (b.Length >= 3 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return 3;          // GIF
        return 0;
    }

    /// <summary>
    /// NoPadding 解密后尾部会带一段块对齐的填充字节，按格式的结束标记裁掉。
    /// 找不到结束标记就原样返回（解码器大多能容忍尾部垃圾）。
    /// </summary>
    private static byte[] TrimToEnd(byte[] data, int magicLength)
    {
        var end = -1;
        if (magicLength == 3 && data[0] == 0xFF && data[1] == 0xD8)
        {
            // JPEG: FF D9
            for (var i = data.Length - 2; i > 2; i--)
            {
                if (data[i] == 0xFF && data[i + 1] == 0xD9) { end = i + 2; break; }
            }
        }
        else if (magicLength == 8)
        {
            // PNG: IEND 块结尾 49 45 4E 44 AE 42 60 82
            for (var i = data.Length - 8; i > 0; i--)
            {
                if (data[i] == 0x49 && data[i + 1] == 0x45 && data[i + 2] == 0x4E && data[i + 3] == 0x44)
                {
                    end = Math.Min(data.Length, i + 8);
                    break;
                }
            }
        }
        else if (magicLength == 12)
        {
            // WebP: RIFF 头里的 size 字段
            var riffSize = BitConverter.ToInt32(data, 4) + 8;
            if (riffSize > 12 && riffSize <= data.Length) end = riffSize;
        }

        if (end <= 0 || end == data.Length) return data;

        var trimmed = new byte[end];
        Buffer.BlockCopy(data, 0, trimmed, 0, end);
        return trimmed;
    }
}
