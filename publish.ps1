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
    [string]$Configuration = "Release",
    # 下载 FFmpeg 用的 HTTP 代理（默认取 HTTPS_PROXY 环境变量）；例如 http://127.0.0.1:10809
    [string]$Proxy = $env:HTTPS_PROXY
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$output = Join-Path $root "bin\release"

# Flyleaf 播放器用的 FFmpeg 共享库（BtbN win64-gpl-shared，与 Flyleaf.FFmpeg.Bindings 9.0 绑定配套）。
# 用 latest 标签的 n9.0 资产：稳定不随日期失效（旧的按日期 autobuild 会被 BtbN 清理变成 404）。
# 不进 git：发布时下载解包到输出目录的 ffmpeg\ 下，程序按 exe 旁 ffmpeg\ 目录加载。
$FlyleafFfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip"

function Copy-FlyleafFfmpeg {
    param([string]$DestDir)
    $dst = Join-Path $DestDir "ffmpeg"
    $hasLibs = Test-Path (Join-Path $dst "avformat-*.dll")
    $hasExe = Test-Path (Join-Path $dst "ffmpeg.exe")
    if ($hasLibs -and $hasExe) {
        # 已有：跳过下载（版本不对时删掉 ffmpeg 目录重跑本脚本）
        Write-Host "FFmpeg libs + ffmpeg.exe already present: $dst"
        return
    }
    $zip = Join-Path ([IO.Path]::GetTempPath()) "flyleaf-ffmpeg-shared.zip"
    if (-not (Test-Path $zip) -or (Get-Item $zip).Length -lt 20MB) {
        Write-Host "Downloading FFmpeg shared build (approx 85MB)..."
        $curlArgs = @("-sL", "-o", $zip)
        if (-not [string]::IsNullOrWhiteSpace($Proxy)) {
            Write-Host "Using proxy: $Proxy"
            $curlArgs += @("-x", $Proxy)
        }
        $curlArgs += $FlyleafFfmpegUrl
        curl.exe @curlArgs
    }
    if (-not (Test-Path $zip) -or (Get-Item $zip).Length -lt 20MB) {
        throw "FFmpeg 下载失败或文件不完整（$(if (Test-Path $zip) { (Get-Item $zip).Length } else { 0 }) bytes）。可设置代理后重试：.\publish.ps1 -Proxy http://127.0.0.1:10809"
    }
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
    # 全片下载/缩略图需要命令行工具；共享库 dll 与 exe 必须同目录
    Copy-Item (Join-Path $binDir "ffmpeg.exe") $dst -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $binDir "ffprobe.exe") $dst -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path (Join-Path $dst "ffmpeg.exe"))) {
        Write-Warning "ffmpeg.exe not found in archive bin dir: $binDir"
    }
    Write-Host "Copied FFmpeg libs + tools: $dst"
}

Write-Host "Publishing $Runtime ($Configuration) -> staging"

# 运行中的实例会锁住 exe/dll，导致清理与拷贝失败
$running = Get-Process ResourceGrab.App -ErrorAction SilentlyContinue
if ($running) {
    throw "检测到正在运行的 ResourceGrab.App（PID $($running.Id -join ',')），请先关闭后再执行发布。"
}

# 关键：必须发布到「独立干净目录」。若发布到 build 输出目录（bin\Release），
# MSBuild 的 build 阶段会把普通 apphost（283KB，依赖旁边的 dll）写进同一目录，
# 与单文件 bundle 冲突，最终可能留下 apphost + 被删的 dll → 启动报
# "The application to execute does not exist: ResourceGrab.App.dll"。
$staging = Join-Path $root "bin\.publish-staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

# NOTE: must use -p:SelfContained=false, NOT the CLI switch --self-contained false.
# The CLI switch combined with -p:PublishSingleFile=true is overridden back to
# self-contained by the .NET 10 SDK (160MB+ output).
dotnet publish (Join-Path $root "src\ResourceGrab.App\ResourceGrab.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    -p:SelfContained=false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -o $staging

# 把真正的单文件产物同步到交付目录（保留 config\ 用户数据与 ffmpeg\）：
# 先清掉 build 阶段遗留在交付目录里的托管 dll / pdb / deps / runtimeconfig / 旧 apphost，
# 再拷贝发布产物（单文件 exe）。不删 config\。
Remove-Item (Join-Path $output "*.dll"), (Join-Path $output "*.pdb"),
            (Join-Path $output "*.deps.json"), (Join-Path $output "*.runtimeconfig.json"),
            (Join-Path $output "ResourceGrab.App.exe") -Force -ErrorAction SilentlyContinue
Get-ChildItem $staging -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -ne ".pdb" } |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $output $_.Name) -Force }

Copy-FlyleafFfmpeg $output

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Publish finished: $output"

Write-Host "IMPORTANT: ship the ffmpeg folder together with the exe, or online playback will fail to initialize."
