using System.Security.Cryptography;
using System.Text;
using ResourceGrab.Core.Services.ShortDrama;
using Xunit;

namespace ResourceGrab.Core.Tests.Services;

// 站点封面是「加密图管线」：CDN 上存的就是密文（ETag = 密文 md5、长度恒为 16 的倍数），
// 由页面 crypto-worker.js 用写死的 AES-128-CBC 密钥在浏览器本地解密。
// 这几个用例锁住参数与裁剪逻辑——密钥一旦变，这里必须红。
public class DramaCoverCipherTests
{
    // 与 crypto-worker.js 的 i0.media_key / media_iv 一致
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("f5d965df75336270");
    private static readonly byte[] Iv = Encoding.ASCII.GetBytes("97b60394abc2fbe1");

    /// <summary>造一张最小的"JPEG"：真图头 + 填充 + EOI。</summary>
    private static byte[] MakeFakeJpeg(int payloadLength = 3000)
    {
        var bytes = new byte[payloadLength + 4];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xDB;
        for (var i = 4; i < payloadLength; i++) bytes[i] = (byte)(i * 7);
        bytes[^2] = 0xFF; bytes[^1] = 0xD9;
        return bytes;
    }

    private static byte[] Encrypt(byte[] plain)
    {
        // CryptoJS 用的是 NoPadding，明文自己补齐到 16 字节
        var padded = new byte[(plain.Length + 15) / 16 * 16];
        Buffer.BlockCopy(plain, 0, padded, 0, plain.Length);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = Key;
        aes.IV = Iv;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(padded, 0, padded.Length);
    }

    [Fact]
    public void TryDecrypt_RoundTripsAndTrimsPadding()
    {
        var original = MakeFakeJpeg();
        var cipher = Encrypt(original);

        // 密文本身认不出是图片
        Assert.False(DramaCoverCipher.IsPlainImage(cipher));

        var plain = DramaCoverCipher.TryDecrypt(cipher);
        Assert.NotNull(plain);
        Assert.Equal(original.Length, plain!.Length);   // 尾部块对齐垃圾被裁掉
        Assert.Equal(original, plain);
        Assert.Equal(0xD9, plain[^1]);
    }

    [Fact]
    public void TryDecrypt_PassesThroughPlainImage()
    {
        var original = MakeFakeJpeg();
        var result = DramaCoverCipher.TryDecrypt(original);
        Assert.NotNull(result);
        Assert.Equal(original, result);                 // 站点哪天改回直出也能用
    }

    [Fact]
    public void TryDecrypt_RejectsNonImagePayload()
    {
        // 解密出来不是图片（比如被中间层替换成 HTML），必须返回 null 而不是乱码
        var junk = new byte[512];
        for (var i = 0; i < junk.Length; i++) junk[i] = (byte)(i * 13 + 7);
        var cipher = Encrypt(junk);
        Assert.Null(DramaCoverCipher.TryDecrypt(cipher));
    }

    [Fact]
    public void TryDecrypt_RejectsNonBlockAlignedInput()
    {
        var data = new byte[1000];                      // 不是 16 的倍数 → 不是这套加密
        Assert.Null(DramaCoverCipher.TryDecrypt(data));
    }
}
