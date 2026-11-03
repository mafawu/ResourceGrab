param(
    [string]$OutFile = "prototype\_shots\home.png",
    [string]$Arg = "",
    [int]$WaitMs = 1400,
    [int]$CursorX = -1,
    [int]$CursorY = -1
)

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

[Win]::SetProcessDPIAware() | Out-Null
[Win]::SetCursorPos(8, 8) | Out-Null
Start-Sleep -Milliseconds 120

$HWND_TOPMOST = [IntPtr](-1)
$SWP_NOMOVE = 0x0002; $SWP_NOSIZE = 0x0001; $SWP_SHOWWINDOW = 0x0040

$exe = Resolve-Path "prototype\YingkuHome\bin\Debug\net10.0-windows\YingkuHome.exe"
if ($Arg) { $proc = Start-Process -FilePath $exe -ArgumentList $Arg -PassThru } else { $proc = Start-Process -FilePath $exe -PassThru }
try {
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and $proc.MainWindowHandle -eq 0) {
        Start-Sleep -Milliseconds 150
        $proc.Refresh()
    }
    if ($proc.MainWindowHandle -eq 0) { throw "window never appeared" }

    $hwnd = $proc.MainWindowHandle
    [Win]::ShowWindow($hwnd, 5) | Out-Null
    [Win]::SetWindowPos($hwnd, $HWND_TOPMOST, 0, 0, 0, 0, $SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_SHOWWINDOW) | Out-Null
    [Win]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds $WaitMs

    $r = New-Object Win+RECT
    # sweep the mouse so no card keeps a stale hover state
    [Win]::SetCursorPos(300, 300) | Out-Null
    Start-Sleep -Milliseconds 80
    [Win]::SetCursorPos(5, 5) | Out-Null
    Start-Sleep -Milliseconds 260
    if ($CursorX -ge 0 -and $CursorY -ge 0) {
        $r0 = New-Object Win+RECT
        [Win]::GetWindowRect($hwnd, [ref]$r0) | Out-Null
        [Win]::SetCursorPos($r0.Left + $CursorX, $r0.Top + $CursorY) | Out-Null
        Start-Sleep -Milliseconds 450
    }
    [Win]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
    Write-Host "rect: $($r.Left),$($r.Top) ${w}x${h}"

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [Win]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    Write-Host "PrintWindow ok=$ok"

    if (-not $ok) {
        $g2 = [System.Drawing.Graphics]::FromImage($bmp)
        $g2.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
        $g2.Dispose()
    }

    $dir = Split-Path -Parent $OutFile
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $bmp.Save((Join-Path (Get-Location) $OutFile), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "saved $OutFile"
}
finally {
    if ($proc -and -not $proc.HasExited) { $proc.Kill() }
}





