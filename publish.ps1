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

function Copy-LibVlc {
    param([string]$DestDir)
    $pkgRoot = Join-Path $env:USERPROFILE ".nuget\packages\videolan.libvlc.windows"
    if (-not (Test-Path $pkgRoot)) { Write-Warning "NuGet package cache not found ($pkgRoot); libvlc may be missing."; return }
    $verDir = Get-ChildItem $pkgRoot -Directory | Sort-Object Name | Select-Object -Last 1
    if ($null -eq $verDir) { Write-Warning "VideoLAN.LibVLC.Windows package not found; libvlc may be missing."; return }
    foreach ($arch in @("x64", "x86")) {
        $src = Join-Path $verDir.FullName "build\$arch"
        $dst = Join-Path $DestDir "libvlc\win-$arch"
        if (Test-Path $src) {
            New-Item -ItemType Directory -Force -Path $dst | Out-Null
            Copy-Item "$src\*" $dst -Recurse -Force
            Write-Host "Copied LibVLC native libs ($arch): $dst"
        } else {
            Write-Warning "Missing LibVLC native source: $src"
        }
    }
}

Write-Host "Publishing $Runtime ($Configuration) -> $output"

# NOTE: must use -p:SelfContained=false, NOT the CLI switch --self-contained false.
# The CLI switch combined with -p:PublishSingleFile=true is overridden back to
# self-contained by the .NET 10 SDK (160MB+ output).
# IncludeNativeLibrariesForSelfExtract must be false: the LibVLC native libs are
# deployed as a libvlc\ folder next to the exe and loaded via an explicit path
# (see OnlineVideoPreviewPlayer.FindLibVlcDirectory). Single-file packing strips
# the standalone libvlc folder, so we copy it back below.
dotnet publish (Join-Path $root "src\ResourceGrab.App\ResourceGrab.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    -p:SelfContained=false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -o $output

Write-Host "Publish finished: $output"

Copy-LibVlc $output

# The build output dir collides with the publish dir (both bin\ on Windows), so the
# managed assemblies are also dropped next to the single-file exe. They are redundant
# (the exe is self-contained for managed code); remove them for a clean deliverable.
Remove-Item (Join-Path $output "*.dll") -ErrorAction SilentlyContinue

Write-Host "IMPORTANT: ship the libvlc folder together with the exe, or online playback will fail to initialize."
