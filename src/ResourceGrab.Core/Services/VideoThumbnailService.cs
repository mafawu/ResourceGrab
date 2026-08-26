using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceGrab.Core.Models;
using ResourceGrab.Core.Utils;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ResourceGrab.Core.Services;

/// <summary>
/// 视频缩略图服务：优先 FFmpeg 命令行截帧（可控截帧位置，避开黑屏开头），
/// 无 FFmpeg 时降级 Windows Shell 缩略图 API。产物存放在 {AppDataDir}/thumbnails/videos/。
/// </summary>
public static class VideoThumbnailService
{
    private const int MaxWidth = 800;
    private const int JpegQuality = 80;

    /// <summary>缩略图生成成功后触发（参数为对应视频文件路径），UI 卡片据此刷新封面。</summary>
    public static event Action<string>? ThumbnailSaved;

    public static string ThumbsDir => Path.Combine(AppPaths.AppDataDir, "thumbnails", "videos");

    /// <summary>
    /// 为视频生成缩略图。
    /// 优先 FFmpeg（取 5%-80% 随机位置截帧）；降级 Shell 系统缩略图（不支持时长的格式也可获得）。
    /// 已存在同版本缩略图时直接返回缓存路径（force = true 时强制重新生成）。
    /// </summary>
    /// <returns>缩略图文件路径；失败返回 null。</returns>
    public static async Task<string?> GenerateAsync(string videoPath, double? seekPercent = null, CancellationToken ct = default, bool force = false)
    {
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
        {
            return null;
        }

        var outputPath = GetThumbPath(videoPath);
        if (string.IsNullOrEmpty(outputPath))
        {
            return null;
        }
        Directory.CreateDirectory(ThumbsDir);

        string? result = null;
        try
        {
            if (!force && File.Exists(outputPath))
            {
                return outputPath;
            }
            if (force)
            {
                try { File.Delete(outputPath); } catch { }
            }

            var ffmpeg = await Task.Run(() => VideoMetadataReader.FindTool("ffmpeg"), ct);
            result = ffmpeg is not null
                ? await GenerateViaFfmpegAsync(ffmpeg, videoPath, outputPath, seekPercent, ct)
                : null;
            result ??= await Task.Run(() => GenerateViaShell(videoPath, outputPath), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            result = null;
        }

        if (result is not null)
        {
            ThumbnailSaved?.Invoke(videoPath);
        }
        return result;
    }

    /// <summary>读取视频时长（秒）。Shell Property System 优先，ffprobe 降级。</summary>
    public static async Task<double> GetDurationAsync(string videoPath)
        => (await Task.Run(() => VideoMetadataReader.Read(videoPath)))?.DurationSeconds ?? 0;

    /// <summary>获取视频分辨率字符串（"1920x1080"），不可读返回空串。</summary>
    public static async Task<string> GetResolutionAsync(string videoPath)
    {
        var meta = await Task.Run(() => VideoMetadataReader.Read(videoPath));
        return meta is { Width: > 0, Height: > 0 } ? $"{meta.Value.Width}x{meta.Value.Height}" : "";
    }

    // ====================== 路径 ======================

    /// <summary>缩略图路径由 视频路径+大小+mtime 决定：文件变化自动失效，重扫可复用。</summary>
    internal static string? GetThumbPath(string videoPath)
    {
        try
        {
            var fi = new FileInfo(videoPath);
            var key = $"{videoPath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
            return Path.Combine(ThumbsDir, hash + ".jpg");
        }
        catch
        {
            return null;
        }
    }

    // ====================== FFmpeg 截帧 ======================

    private static async Task<string?> GenerateViaFfmpegAsync(
        string ffmpegExe, string videoPath, string outputPath, double? seekPercent, CancellationToken ct)
    {
        var tmpPath = outputPath + ".tmp.jpg";
        try
        {
            // 取 5%-80% 随机位置，避免黑屏开头和片尾；无时长数据时退化为取首帧
            var percent = Math.Clamp(seekPercent ?? Random.Shared.Next(5, 81), 1, 95);
            var meta = await Task.Run(() => VideoMetadataReader.Read(videoPath), ct);
            var duration = meta?.DurationSeconds ?? 0;
            var seekArgs = duration > 0 ? $"-ss {duration * percent / 100.0:0.###} " : "";
            var args = $"-y {seekArgs}-i \"{videoPath}\" -frames:v 1 -vf \"scale='min({MaxWidth},iw)':-2\" -q:v {(100 - JpegQuality) / 10} \"{tmpPath}\"";

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                },
            };
            proc.Start();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                throw;
            }

            if (proc.ExitCode != 0 || !File.Exists(tmpPath))
            {
                return null;
            }

            File.Move(tmpPath, outputPath, true);
            return outputPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { File.Delete(tmpPath); } catch { }
        }
    }

    // ====================== Shell 缩略图降级 ======================

    private static string? GenerateViaShell(string videoPath, string outputPath)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"); // IShellItemImageFactory
            SHCreateItemFromParsingName(videoPath, IntPtr.Zero, ref iid, out var obj);
            if (obj is not IShellItemImageFactory factory)
            {
                return null;
            }

            // SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK
            var hr = factory.GetImage(new SIZE { X = MaxWidth, Y = MaxWidth }, 0x1, out var hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                using var image = CopyToImageSharp(hBitmap);
                if (image is null)
                {
                    return null;
                }
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(MaxWidth, MaxWidth),
                    Mode = ResizeMode.Max,
                }));
                image.Save(outputPath, new JpegEncoder { Quality = JpegQuality });
                return outputPath;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>HBITMAP(DDB) → 32bpp DIB → ImageSharp Image。</summary>
    private static Image? CopyToImageSharp(IntPtr hBitmap)
    {
        var bm = new BITMAP();
        if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
        {
            return null;
        }

        var width = bm.bmWidth;
        var height = Math.Abs(bm.bmHeight);
        var stride = width * 4;
        var pixels = new byte[stride * height];

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = 40,
                biWidth = width,
                biHeight = -height, // 负值 → 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };

        var hdc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hBitmap, 0, (uint)height, pixels, ref bmi, 0 /* DIB_RGB_COLORS */) == 0)
            {
                return null;
            }
        }
        finally
        {
            DeleteDC(hdc);
        }

        return Image.LoadPixelData<Bgra32>(pixels, width, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors; // 不使用调色板
    }

    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBytes, ref BITMAP bm);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uVStartScan, uint uVScanLines,
        byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
