using System.Runtime.InteropServices;
using System.Text.Json;

namespace ResourceGrab.Core.Utils;

/// <summary>
/// 视频元数据读取：优先 Windows Shell Property System（系统原生，覆盖常见格式），
/// 失败时降级 ffprobe（若已安装）。不依赖任何第三方原生库。
/// </summary>
public static class VideoMetadataReader
{
    /// <summary>
    /// 读取视频时长与分辨率。单项读取失败时对应值为 0；
    /// 完全不可读（非 Windows / 无解码器等）返回 null。
    /// </summary>
    public static (double DurationSeconds, int Width, int Height)? Read(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        var shell = TryReadViaShell(filePath);
        if (shell is not null)
        {
            return shell;
        }

        return TryReadViaFfprobe(filePath);
    }

    // ====================== Shell Property System ======================

    private static (double, int, int)? TryReadViaShell(string filePath)
    {
        // 非显式失败（异常）返回 null 继续降级
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            var iid = new Guid("7e9fb0d3-919f-4307-ab2e-973330a48ae5"); // IShellItem2
            SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref iid, out var obj);
            if (obj is not IShellItem2 item)
            {
                return null;
            }

            double duration = 0;
            if (TryGetString(item, "System.Media.Duration", out var durText)
                && long.TryParse(durText, out var ticks100ns))
            {
                duration = ticks100ns / 10_000_000.0;
            }

            var width = TryGetString(item, "System.Video.FrameWidth", out var wText) && int.TryParse(wText, out var w) ? w : 0;
            var height = TryGetString(item, "System.Video.FrameHeight", out var hText) && int.TryParse(hText, out var h) ? h : 0;

            if (duration <= 0 && width == 0 && height == 0)
            {
                return null;
            }
            return (duration, width, height);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetString(IShellItem2 item, string propertyName, out string value)
    {
        value = "";
        try
        {
            PSGetPropertyKeyFromName(propertyName, out var key);
            item.GetString(ref key, out value);
            return !string.IsNullOrEmpty(value);
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [Guid("7e9fb0d3-919f-4307-ab2e-973330a48ae5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        // ===== IShellItem（vtable 顺序不可改动）=====
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(ref Guid riid, out IntPtr ppv);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void CompareNames(IntPtr psi, uint hint, out int piOrder);

        // ===== IShellItem2 =====
        void GetPropertyStore(uint flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyStoreWithFlags(uint flags, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(ref PROPERTYKEY key, ref Guid riid, out IntPtr ppv);
        void Update(IntPtr pbc);
        void GetProperty(ref PROPERTYKEY key, IntPtr pvar);
        void GetString(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void PSGetPropertyKeyFromName(string pszName, out PROPERTYKEY pkey);

    // ====================== ffprobe 降级 ======================

    private sealed class FfprobeResult
    {
        public FfprobeStream[]? streams { get; set; }
        public FfprobeFormat? format { get; set; }
    }

    private sealed class FfprobeStream
    {
        public int? width { get; set; }
        public int? height { get; set; }
    }

    private sealed class FfprobeFormat
    {
        public string? duration { get; set; }
    }

    private static (double, int, int)? TryReadViaFfprobe(string filePath)
    {
        try
        {
            var exe = FindTool("ffprobe");
            if (exe is null)
            {
                return null;
            }

            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height -show_entries format=duration -of json \"{filePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            proc.Start();
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(true); } catch { }
                return null;
            }

            var result = JsonSerializer.Deserialize<FfprobeResult>(stdout);
            var stream = result?.streams?.FirstOrDefault();
            double.TryParse(result?.format?.duration, out var duration);
            var width = stream?.width ?? 0;
            var height = stream?.height ?? 0;
            if (duration <= 0 && width == 0 && height == 0)
            {
                return null;
            }
            return (duration, width, height);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>在 PATH 与程序目录中查找外部工具的可执行文件路径；找不到返回 null。</summary>
    internal static string? FindTool(string fileName)
    {
        var exeName = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".exe";

        var local = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(local))
        {
            return local;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // PATH 中可能有非法路径项，跳过
            }
        }
        return null;
    }
}
