# 渲染播放器的"海报兜底层"，确认它不与中央播放键打架。
# 输出：design\short-drama\shots\player-poster-fallback.png
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase | Out-Null

$root = "F:\ProcessProject\C#\ResourceGrab"
$asm = [Reflection.Assembly]::LoadFrom("$root\bin\Debug\ResourceGrab.App.dll")

$appType = $asm.GetType("ResourceGrab.App.App")
$app = [Activator]::CreateInstance($appType)
$appType.GetMethod("InitializeComponent").Invoke($app, @()) | Out-Null
Add-Type -TypeDefinition "public sealed class NullSP5 : System.IServiceProvider { public object GetService(System.Type t) => null; }" | Out-Null
$appType.GetProperty("Services", [Reflection.BindingFlags]"Public,Static").SetValue($null, [NullSP5]::new())

$playerType = $asm.GetType("ResourceGrab.App.Controls.OnlineVideoPreviewPlayer")
$placeholderType = $asm.GetType("ResourceGrab.App.Common.DramaCoverPlaceholder")

$player = [Activator]::CreateInstance($playerType)
$brush = $placeholderType.GetMethod("Build").Invoke($null, [object[]]@("1003末日原罪"))
$player.Width = 960
$player.Height = 540
$player.PosterFallbackBrush = $brush
$player.PosterFallbackTextValue = "末"
$player.PosterUrl = ""              # 无海报 -> 走兜底层
$player.ShowPlayButton = $true

$player.Measure([System.Windows.Size]::new(960, 540))
$player.Arrange([System.Windows.Rect]::new(0, 0, 960, 540))
$player.UpdateLayout()

# 让 UpdatePosterFallback 的 BeginInvoke 跑完
$frame = New-Object System.Windows.Threading.DispatcherFrame
$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(1)
$timer.Add_Tick({ $frame.Continue = $false; $timer.Stop() })
$timer.Start()
[System.Windows.Threading.Dispatcher]::PushFrame($frame)
$player.UpdateLayout()

$rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(960, 540, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$rtb.Render($player)

$enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
$outDir = Join-Path $root "design\short-drama\shots"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outFile = Join-Path $outDir "player-poster-fallback.png"
$fs = [System.IO.File]::Create($outFile)
$enc.Save($fs)
$fs.Close()
Write-Output ("saved " + $outFile + " (" + (Get-Item $outFile).Length + " bytes)")
