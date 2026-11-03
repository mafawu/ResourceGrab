# 用「从站点解密出来的真封面」渲染卡片墙（不联网、不走异步，纯排版验证）
# 输出：design\short-drama\shots\cards-live-covers.png
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase | Out-Null

$root = "F:\ProcessProject\C#\ResourceGrab"
$asm = [Reflection.Assembly]::LoadFrom("$root\bin\Debug\ResourceGrab.App.dll")
$core = [Reflection.Assembly]::LoadFrom("$root\bin\Debug\ResourceGrab.Core.dll")

$coversDir = Join-Path $env:TEMP "drama-covers-live"
$files = @(Get-ChildItem -Path (Join-Path $coversDir "*") -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @(".jpg", ".png", ".webp", ".img") } | Sort-Object Name)
if ($files.Count -eq 0) { throw "没有找到解密后的封面，请先跑 scripts\decrypt-site-covers.ps1" }
Write-Output ("covers: " + $files.Count + " 张")

$appType = $asm.GetType("ResourceGrab.App.App")
$app = [Activator]::CreateInstance($appType)
$appType.GetMethod("InitializeComponent").Invoke($app, @()) | Out-Null
Add-Type -TypeDefinition "public sealed class NullSP10 : System.IServiceProvider { public object GetService(System.Type t) => null; }" | Out-Null
$appType.GetProperty("Services", [Reflection.BindingFlags]"Public,Static").SetValue($null, [NullSP10]::new())

$bg = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0x0B, 0x0E, 0x14))
$canvasRoot = New-Object System.Windows.Controls.Border
$canvasRoot.Background = $bg
$canvasRoot.Resources["TextPrimaryBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0xE9, 0xED, 0xF5))
$canvasRoot.Resources["TextDisabledBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0x77, 0x80, 0x96))
$canvasRoot.Resources["PrimarySubtleBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0x1A, 0x23, 0x40))
$canvasRoot.Resources["PrimaryLightBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0x9A, 0xAE, 0xFF))
$canvasRoot.Resources["CardBorderBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0x23, 0x2A, 0x38))
$canvasRoot.Resources["CardScrimBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromArgb(0x8C, 0, 0, 0))
$canvasRoot.Resources["SuccessBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0x3D, 0xD5, 0x98))
$canvasRoot.Resources["WarningBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0xF5, 0xA6, 0x23))

$panel = New-Object System.Windows.Controls.Primitives.UniformGrid
$panel.Columns = $files.Count
$panel.Background = $bg
$panel.Margin = New-Object System.Windows.Thickness(16)
$canvasRoot.Child = $panel

$st = $core.GetType("ResourceGrab.Core.Sources.ShortDrama.ShortDramaSummary")
$at = $asm.GetType("ResourceGrab.App.ViewModels.ShortDramaCardAdapter")
$cardType = $asm.GetType("ResourceGrab.App.Controls.MediaCard")

# 用站点真实标题（由 decrypt-site-covers.ps1 导出）
$metaPath = Join-Path $coversDir "titles.json"
$meta = @()
if (Test-Path $metaPath) { $meta = Get-Content $metaPath -Raw -Encoding UTF8 | ConvertFrom-Json }
function Meta-Of($file) {
    $id = [System.IO.Path]::GetFileNameWithoutExtension($file)
    return ($meta | Where-Object { $_.id -eq $id } | Select-Object -First 1)
}
$scores = @("7.8", "8.9", "8.4", "9.0", "8.4", "8.7")
$eps    = @("更新至2集", "全7集", "更新至6集", "全9集", "全1集", "全5集")
$badges = @("新上", "热播", "新上", "高分", "热播", "原创")

for ($i = 0; $i -lt $files.Count; $i++) {
    $s = [Activator]::CreateInstance($st)
    $st.GetProperty("Id").SetValue($s, [System.IO.Path]::GetFileNameWithoutExtension($files[$i].Name))
    $m2 = Meta-Of $files[$i].Name
    $title = if ($m2) { $m2.title } else { "剧集 $i" }
    $st.GetProperty("Title").SetValue($s, $title)
    $st.GetProperty("CoverUrl").SetValue($s, "")
    $st.GetProperty("ScoreText").SetValue($s, $scores[$i % $scores.Count])
    $st.GetProperty("EpisodeText").SetValue($s, $eps[$i % $eps.Count])
    $st.GetProperty("Badge").SetValue($s, $badges[$i % $badges.Count])

    $adapter = [Activator]::CreateInstance($at, [object[]]@($s, $null, $null))
    $adapter.SetCoverUrl($files[$i].FullName)
    $card = [Activator]::CreateInstance($cardType)
    $card.DataContext = $adapter
    $card.Content.Content = $adapter
    $card.Margin = New-Object System.Windows.Thickness(4)
    $panel.Children.Add($card) | Out-Null
}

$w = 1440; $h = 420
$canvasRoot.Width = $w
$canvasRoot.Height = $h
$canvasRoot.Measure([System.Windows.Size]::new($w, $h))
$canvasRoot.Arrange([System.Windows.Rect]::new(0, 0, $w, $h))
$canvasRoot.UpdateLayout()
$canvasRoot.Measure([System.Windows.Size]::new($w, $h))
$canvasRoot.Arrange([System.Windows.Rect]::new(0, 0, $w, $h))
$canvasRoot.UpdateLayout()

# ImageLoader 是异步的，脚本宿主里不方便泵消息循环；这里直接按 DataContext 里的本地路径同步解码，
# 目的只是验证"真封面在卡片里的排版效果"，解码能力本身由应用内的 ImageLoader 负责（已在别处验证）。
function Set-Images([System.Windows.DependencyObject]$parent) {
    $count = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($parent)
    for ($k = 0; $k -lt $count; $k++) {
        $child = [System.Windows.Media.VisualTreeHelper]::GetChild($parent, $k)
        if ($child -is [System.Windows.Controls.Image]) {
            $dc = $child.DataContext
            $path = $null
            if ($dc -and $dc.PSObject.Properties['CoverUrl']) { $path = $dc.CoverUrl }
            if ($path -and (Test-Path $path) -and $path -notmatch '\.json$') {
                $bmp = New-Object System.Windows.Media.Imaging.BitmapImage
                $bmp.BeginInit()
                $bmp.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
                $bmp.DecodePixelWidth = 374
                $bmp.UriSource = [Uri]::new($path)
                $bmp.EndInit()
                $bmp.Freeze()
                $child.Source = $bmp
            }
        }
        Set-Images $child
    }
}
Set-Images $canvasRoot
$canvasRoot.UpdateLayout()

$rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($w, $h, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$rtb.Render($canvasRoot)
$enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))

$outDir = Join-Path $root "design\short-drama\shots"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outFile = Join-Path $outDir "cards-live-covers.png"
$fs = [System.IO.File]::Create($outFile)
$enc.Save($fs)
$fs.Close()
Write-Output ("saved " + $outFile + " (" + (Get-Item $outFile).Length + " bytes)")
