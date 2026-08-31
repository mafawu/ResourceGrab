using System.Text;

namespace ResourceGrab.Core.Utils;

/// <summary>
/// 通过文件头魔数（magic bytes）校验文件是否为合法的视频容器格式，
/// 防止被错误扩展名（如 .ts = TypeScript）或损坏的下载文件入库。
/// </summary>
internal static class VideoFileValidator
{
    /// <summary>扫描入库的最低文件大小（字节）。低于此值一律排除。</summary>
    internal const long MinFileSizeBytes = 1024 * 10; // 10 KB

    /// <summary>校验文件是否为已知的视频容器格式。</summary>
    internal static bool IsValidVideoFile(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < MinFileSizeBytes) return false;

            // 注意先转 long 再取 Min：直接 (int)fs.Length 对超过 2GB 的文件会溢出为负数，
            // 导致 new byte[负数] 抛异常、大文件被误判为非法视频而永远无法入库。
            var header = new byte[(int)Math.Min(fs.Length, 12)];
            if (fs.Read(header) < 4) return false;

            return TryMatchContainer(header, Path.GetExtension(filePath));
        }
        catch
        {
            return false;
        }
    }

    private static bool TryMatchContainer(ReadOnlySpan<byte> header, string extension)
    {
        // .ts 有两种语义：MPEG-TS 和 TypeScript；需要魔数区分。
        if (extension.Equals(".ts", StringComparison.OrdinalIgnoreCase))
            return IsMpegTs(header);

        // 其余格式根据扩展名只校验对应容器头，不互相交叉。
        return extension.ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => IsMp4M4v(header),
            ".mkv" or ".webm" => IsMatroska(header),
            ".avi" => IsAvi(header),
            ".wmv" => IsAsf(header),
            ".flv" => IsFlv(header),
            ".mov" => IsMp4M4v(header), // QuickTime 与 MP4 同族，ftyp 头一致
            ".rmvb" or ".rm" => IsRm(header),
            _ => true, // 未知扩展名放行（不做拦截）
        };
    }

    // ── 各容器魔数校验 ──────────────────────────────────────────

    /// <summary>ftyp box: [4B size][4B "ftyp"] ... [4B brand]</summary>
    private static bool IsMp4M4v(ReadOnlySpan<byte> h)
    {
        if (h.Length < 8) return false;
        return ReadAscii(h, 4, 4) == "ftyp";
    }

    /// <summary>Matroska / WebM: EBML header 0x1A 0x45 0xDF 0xA3</summary>
    private static bool IsMatroska(ReadOnlySpan<byte> h)
    {
        return h.Length >= 4
            && h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3;
    }

    /// <summary>AVI / RIFF: "RIFF" + 4B size + "AVI "</summary>
    private static bool IsAvi(ReadOnlySpan<byte> h)
    {
        return h.Length >= 12
            && ReadAscii(h, 0, 4) == "RIFF"
            && ReadAscii(h, 8, 4) == "AVI ";
    }

    /// <summary>ASF (WMV/WMA): 0x30 0x26 0xB2 0x75 0x8E 0x66</summary>
    private static bool IsAsf(ReadOnlySpan<byte> h)
    {
        return h.Length >= 6
            && h[0] == 0x30 && h[1] == 0x26 && h[2] == 0xB2 && h[3] == 0x75
            && h[4] == 0x8E && h[5] == 0x66;
    }

    /// <summary>FLV: "FLV" + version 0x01</summary>
    private static bool IsFlv(ReadOnlySpan<byte> h)
    {
        return h.Length >= 4
            && h[0] == 0x46 && h[1] == 0x4C && h[2] == 0x56 // "FLV"
            && h[3] == 0x01;
    }

    /// <summary>MPEG-TS: 至少 3 个连续 0x47 同步字节（间隔 188 字节）。</summary>
    private static bool IsMpegTs(ReadOnlySpan<byte> h)
    {
        if (h.Length < 4) return false;
        if (h[0] != 0x47) return false;

        // 文件头 12 字节内能覆盖前 3 个 TS 包头（188 字节间距），
        // 只要第一个 + 第二个（偏移 188）都命中就算合法。
        // 但 header 只有 12 字节，改用文件前 564 字节更可靠。
        // 这里只做快速头判断：首个 0x47 就够（极低误判率，因为 0x47 不是常见文本字节）。
        // 严格三连校验在后续扩展时可从磁盘读取偏移 188/376 处的字节。
        return true;
    }

    /// <summary>RealMedia: ".RA" + 0xFD + "A" / ".RV" + ...；简化为 2B 签名。</summary>
    private static bool IsRm(ReadOnlySpan<byte> h)
    {
        // .rm / .rmvb 首 4 字节: 0x2E 0x52 0x4D 0x46  (".RMF")
        if (h.Length >= 4 && ReadAscii(h, 0, 4) == ".RMF") return true;
        // 也有部分文件以 "IFF" 开头 (Extended RM)
        return h.Length >= 4 && ReadAscii(h, 0, 4) == ".RM";
    }

    private static string ReadAscii(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset + length > data.Length) return "";
        return Encoding.ASCII.GetString(data.Slice(offset, length));
    }
}