# 用 WPF 真渲染短剧卡片墙，验证"封面兜底"在真实控件里的样子。
# 输出：design\short-drama\shots\cards-wpf.png
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase | Out-Null

$root = "F:\ProcessProject\C#\ResourceGrab"
$asm = [Reflection.Assembly]::LoadFrom("$root\bin\Debug\ResourceGrab.App.dll")
$core = [Reflection.Assembly]::LoadFrom("$root\bin\Debug\ResourceGrab.Core.dll")

$appType = $asm.GetType("ResourceGrab.App.App")
$app = [Activator]::CreateInstance($appType)
$appType.GetMethod("InitializeComponent").Invoke($app, @()) | Out-Null
Add-Type -TypeDefinition "public sealed class NullSP4 : System.IServiceProvider { public object GetService(System.Type t) => null; }" | Out-Null
$appType.GetProperty("Services", [Reflection.BindingFlags]"Public,Static").SetValue($null, [NullSP4]::new())

$bgColor = [System.Windows.Media.Color]::FromRgb(0x0B, 0x0E, 0x14)
$bg = [System.Windows.Media.SolidColorBrush]::new($bgColor)

# 先建根，再挂"宿主修正"资源：App.xaml 默认合的是浅色 Colors.xaml，
# 正式运行由 ThemeManager 换字典；渲染脚本里直接在根元素覆盖两个文字色，保证标题可读。
$canvasRoot = New-Object System.Windows.Controls.Border
$canvasRoot.Background = $bg
$canvasRoot.Resources["TextPrimaryBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0xE9, 0xED, 0xF5))
$canvasRoot.Resources["TextDisabledBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0x77, 0x80, 0x96))
# 其余主题色同样按深色字典取值，否则角标胶囊会是浅色版（浅底浅字，看着像没生效）
$canvasRoot.Resources["PrimarySubtleBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0x1A, 0x23, 0x40))
$canvasRoot.Resources["PrimaryLightBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0x9A, 0xAE, 0xFF))
$canvasRoot.Resources["CardBorderBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0x23, 0x2A, 0x38))
$canvasRoot.Resources["CardScrimBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromArgb(0x8C, 0, 0, 0))
$canvasRoot.Resources["SuccessBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0x3D, 0xD5, 0x98))
$canvasRoot.Resources["WarningBrush"] = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(0xF5, 0xA6, 0x23))

$panel = New-Object System.Windows.Controls.Primitives.UniformGrid
$panel.Columns = 6
$panel.Background = $bg
$panel.Margin = New-Object System.Windows.Thickness(16)
$canvasRoot.Child = $panel

$data = @(
    @{ id = "5033"; title = "妈妈和姐姐用了我的系统都说好"; score = "8.4"; ep = "更新至6集"; badge = "新上" },
    @{ id = "6482"; title = "苗疆少女";                       score = "7.8"; ep = "更新至2集"; badge = "新上" },
    @{ id = "1003"; title = "末日原罪";                       score = "8.9"; ep = "全7集";     badge = "热播" },
    @{ id = "6435"; title = "金牌御医";                       score = "9.0"; ep = "全9集";     badge = "高分" },
    @{ id = "1388"; title = "继母的朋友们";                   score = "8.4"; ep = "全1集";     badge = "热播" },
    @{ id = "5524"; title = "母子奴仆";                       score = "8.7"; ep = "全5集";     badge = "原创" }
)

$st = $core.GetType("ResourceGrab.Core.Sources.ShortDrama.ShortDramaSummary")
$at = $asm.GetType("ResourceGrab.App.ViewModels.ShortDramaCardAdapter")
$cardType = $asm.GetType("ResourceGrab.App.Controls.MediaCard")

foreach ($d in $data) {
    $s = [Activator]::CreateInstance($st)
    $st.GetProperty("Id").SetValue($s, $d.id)
    $st.GetProperty("Title").SetValue($s, $d.title)
    # 留空 = 不联网，纯看兜底层；真实运行时这里是加密的 https URL，加载失败同样落回兜底
    $st.GetProperty("CoverUrl").SetValue($s, "")
    $st.GetProperty("ScoreText").SetValue($s, $d.score)
    $st.GetProperty("EpisodeText").SetValue($s, $d.ep)
    $st.GetProperty("Badge").SetValue($s, $d.badge)

    $adapter = [Activator]::CreateInstance($at, [object[]]@($s, $null, $null))
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
# 第二遍：让"封面高度 = 实际宽度 × 1.25"的绑定吃到真实 ActualWidth
$canvasRoot.UpdateLayout()
$canvasRoot.Measure([System.Windows.Size]::new($w, $h))
$canvasRoot.Arrange([System.Windows.Rect]::new(0, 0, $w, $h))
$canvasRoot.UpdateLayout()

$rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($w, $h, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$rtb.Render($canvasRoot)

$enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
$outDir = Join-Path $root "design\short-drama\shots"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outFile = Join-Path $outDir "cards-wpf.png"
$fs = [System.IO.File]::Create($outFile)
$enc.Save($fs)
$fs.Close()
Write-Output ("saved " + $outFile + " (" + (Get-Item $outFile).Length + " bytes)")
