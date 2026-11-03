# 渲染短剧页设计稿截图（1440×900，1x）
# 用法：pwsh -File render-shots.ps1
# 说明：Chromium 需要多进程 IPC，在受限沙箱下会 FATAL(mojo platform_channel)，
#       因此本脚本需在无沙箱/完全访问下运行。

param(
    [string]$Edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    [string]$ProfileDir = "$env:TEMP\edge-shot-profile"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$shots = Join-Path $root "shots"
New-Item -ItemType Directory -Force -Path $shots | Out-Null

# file:// URL：反斜杠转正斜杠，# 必须编码成 %23（路径里有 "C#"）
$fileBase = "file:///" + ($root -replace '\\', '/' -replace '#', '%23')

$jobs = @(
    @{ page = "list.html";   theme = "dark";  name = "list-dark.png" },
    @{ page = "list.html";   theme = "light"; name = "list-light.png" },
    @{ page = "detail.html"; theme = "dark";  name = "detail-dark.png" },
    @{ page = "detail.html"; theme = "light"; name = "detail-light.png" },
    @{ page = "rank.html";   theme = "dark";  name = "rank-dark.png" },
    @{ page = "local.html";  theme = "dark";  name = "local-dark.png" },
    @{ page = "local.html";  theme = "light"; name = "local-light.png" }
)

foreach ($j in $jobs) {
    $target = Join-Path $shots $j.name
    if (Test-Path $target) { Remove-Item $target -Force }
    $url = "$fileBase/$($j.page)?theme=$($j.theme)"

    $args = @(
        "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
        "--no-default-browser-check", "--disable-crash-reporter", "--disable-breakpad",
        "--force-device-scale-factor=1", "--window-size=1440,900",
        "--virtual-time-budget=1500",
        "--user-data-dir=$ProfileDir",
        "--screenshot=$target",
        $url
    )
    & $Edge @args *> $null

    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $target) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (Test-Path $target) {
        "{0,-18} {1,8:N0} bytes" -f $j.name, (Get-Item $target).Length
    } else {
        "{0,-18} FAILED" -f $j.name
    }
}
