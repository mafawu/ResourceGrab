# 发布「自包含单文件」exe（目标机无需安装 .NET 运行时）。
# 产物：bin\release-standalone\ResourceGrab.App.exe（单文件）+ ffmpeg\（Flyleaf 原生库，必须随 exe）
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$EmbedFfmpeg
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$output = Join-Path $root "bin\release-standalone"
$staging = Join-Path $root "bin\.publish-standalone"

$running = Get-Process ResourceGrab.App -ErrorAction SilentlyContinue
if ($running) {
    throw "检测到正在运行的 ResourceGrab.App（PID $($running.Id -join ',')），请先关闭后再执行发布。"
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null

$args = @(
    (Join-Path $root "src\ResourceGrab.App\ResourceGrab.App.csproj"),
    "-c", $Configuration,
    "-r", $Runtime,
    "-p:SelfContained=true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none",
    "-p:GenerateRuntimeConfigurationFiles=true",
    "-o", $staging
)
if ($EmbedFfmpeg) {
    $args += "-p:FfmpegBundleDir=$(Join-Path $root 'bin\Release\ffmpeg')"
}

Write-Host "Publishing self-contained single file -> $staging"
dotnet publish @args
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）" }

# 只把 exe 交到交付目录（其它 pdb/config 不要）
Remove-Item (Join-Path $output "ResourceGrab.App.exe") -Force -ErrorAction SilentlyContinue
Get-ChildItem $staging -File | Where-Object { $_.Name -like "*.exe" -and $_.Name -notlike "createdump*" } |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $output $_.Name) -Force }

# ffmpeg 原生库仍按目录随行（除非已内嵌）
if (-not $EmbedFfmpeg) {
    $src = Join-Path $root "bin\Release\ffmpeg"
    $dst = Join-Path $output "ffmpeg"
    if (Test-Path $src) {
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        Copy-Item (Join-Path $src "*") $dst -Force
        Write-Host "已复制 ffmpeg\（Flyleaf 原生库）"
    } else {
        Write-Warning "未找到 bin\Release\ffmpeg，播放/下载会不可用。先跑一次 .\publish.ps1 可下载。"
    }
}

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

$exe = Join-Path $output "ResourceGrab.App.exe"
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "完成：$exe ($sizeMb MB)"
if (Test-Path (Join-Path $output "ffmpeg")) {
    $ff = Get-ChildItem (Join-Path $output "ffmpeg") | Measure-Object Length -Sum
    Write-Host ("ffmpeg\ : {0} 个文件，{1} MB" -f $ff.Count, [math]::Round($ff.Sum / 1MB))
}
