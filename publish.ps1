<#
.SYNOPSIS
Publish a framework-dependent single-file executable (win-x64).
Target machine must have .NET 10 Desktop Runtime installed.

.EXAMPLE
.\publish.ps1
.\publish.ps1 -Runtime win-x64
#>
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$output = Join-Path $root "bin\release"

# Flyleaf 播放器用的 FFmpeg 共享库（BtbN win64-gpl-shared，与 Flyleaf.FFmpeg 9.x 绑定配套）。
# 不进 git：发布时下载解包到输出目录的 ffmpeg\ 下，程序按 exe 旁 ffmpeg\ 目录加载。
$FlyleafFfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-08-13-11/ffmpeg-n9.0.1-27-g9b0578816c-win64-gpl-shared-9.0.zip"

function Copy-FlyleafFfmpeg {
    param([string]$DestDir)
    $dst = Join-Path $DestDir "ffmpeg"
    if ((Test-Path (Join-Path $dst "avformat-*.dll"))) {
        # 已有：跳过下载（版本不对时删掉 ffmpeg 目录重跑本脚本）
        Write-Host "FFmpeg shared libs already present: $dst"
        return
    }
    $zip = Join-Path ([IO.Path]::GetTempPath()) "flyleaf-ffmpeg-shared.zip"
    Write-Host "Downloading FFmpeg shared build (approx 85MB)..."
    curl.exe -sL -o $zip $FlyleafFfmpegUrl
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "flyleaf-ffmpeg-shared"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force
    $binDir = Get-ChildItem -Path $tmp -Directory | ForEach-Object {
        Join-Path $_.FullName "bin"
    } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($null -eq $binDir) { Write-Warning "FFmpeg bin dir not found in archive."; return }
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item (Join-Path $binDir "av*.dll") $dst -Force
    Copy-Item (Join-Path $binDir "sw*.dll") $dst -Force
    Write-Host "Copied FFmpeg shared libs: $dst"
}

Write-Host "Publishing $Runtime ($Configuration) -> $output"

# NOTE: must use -p:SelfContained=false, NOT the CLI switch --self-contained false.
# The CLI switch combined with -p:PublishSingleFile=true is overridden back to
# self-contained by the .NET 10 SDK (160MB+ output).
dotnet publish (Join-Path $root "src\ResourceGrab.App\ResourceGrab.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    -p:SelfContained=false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -o $output

Write-Host "Publish finished: $output"

Copy-FlyleafFfmpeg $output

# The build output dir collides with the publish dir (both bin\ on Windows), so the
# managed assemblies are also dropped next to the single-file exe. They are redundant
# (the exe is self-contained for managed code); remove them for a clean deliverable.
# NOTE: keep the ffmpeg\ folder (Flyleaf native libs) — it is NOT redundant.
Get-ChildItem (Join-Path $output "*.dll") -File -ErrorAction SilentlyContinue | Remove-Item -ErrorAction SilentlyContinue

Write-Host "IMPORTANT: ship the ffmpeg folder together with the exe, or online playback will fail to initialize."
