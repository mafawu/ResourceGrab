<#
.SYNOPSIS
发布单文件可执行程序（win-x64，框架依赖，需要目标机安装 .NET 10 Desktop Runtime）。

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
$output = Join-Path $root "publish"

Write-Host "发布 $Runtime ($Configuration) -> $output"

# 注意：必须用 -p:SelfContained=false 而非 CLI 开关 --self-contained false。
# CLI 开关与 -p:PublishSingleFile=true 组合时，会被 .NET 10 SDK 覆盖回自包含（产物 160MB+）。
dotnet publish (Join-Path $root "src\ResourceGrab.App\ResourceGrab.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    -p:SelfContained=false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $output

Write-Host "发布完成: $output"
