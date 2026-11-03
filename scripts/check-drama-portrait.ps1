# 验证「竖屏适配」真的生效：直接调详情页里的 ApplyPlayerAspect，检查播放器框宽度
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase | Out-Null

$root = "F:\ProcessProject\C#\ResourceGrab"
$asm = [Reflection.Assembly]::LoadFrom("$root\bin\Debug\ResourceGrab.App.dll")
$appType = $asm.GetType("ResourceGrab.App.App")
$app = [Activator]::CreateInstance($appType)
$appType.GetMethod("InitializeComponent").Invoke($app, @()) | Out-Null
Add-Type -TypeDefinition "public sealed class NullSP11 : System.IServiceProvider { public object GetService(System.Type t) => null; }" | Out-Null
$appType.GetProperty("Services", [Reflection.BindingFlags]"Public,Static").SetValue($null, [NullSP11]::new())

$viewType = $asm.GetType("ResourceGrab.App.Views.ShortDramaDetailView")
$view = [Activator]::CreateInstance($viewType)
$view.Measure([System.Windows.Size]::new(1200, 900))
$view.Arrange([System.Windows.Rect]::new(0, 0, 1200, 900))
$view.UpdateLayout()

$flags = [Reflection.BindingFlags]"NonPublic,Instance"
$frameField = $viewType.GetField("PlayerFrame", $flags)              # XAML 生成的字段
$frame = $frameField.GetValue($view)
if ($null -eq $frame) { throw "找不到 PlayerFrame" }

$apply = $viewType.GetMethod("ApplyPlayerAspect", $flags)
if ($null -eq $apply) { throw "找不到 ApplyPlayerAspect" }

# 真实流程里 DP 先被赋值、再调 ApplyPlayerAspect；测试必须一致，
# 否则 PlayerFrame.SizeChanged 会用仍是 0 的 DP 把宽度改回 NaN。
$player = $viewType.GetField("Player", $flags).GetValue($view)
$playerType = $player.GetType()
$aspectDp = $playerType.GetField("VideoAspectRatioProperty", [Reflection.BindingFlags]"Public,Static").GetValue($null)

function Set-AspectAndApply($ratio) {
    $player.SetValue($aspectDp, [double]$ratio)
    $apply.Invoke($view, [object[]]@([double]$ratio)) | Out-Null
    $view.UpdateLayout()
}

Write-Output ("播放器框初始高度: " + $frame.ActualHeight.ToString("0") + "，宽度: " + $(if ([double]::IsNaN($frame.Width)) { "NaN(铺满)" } else { $frame.Width.ToString("0") }))
$height = if ($frame.ActualHeight -gt 0) { $frame.ActualHeight } else { $frame.MaxHeight }
Write-Output ("用于计算的框高度: " + $height.ToString("0"))

# 1) 竖屏 720x1280 -> 比例 0.5625
Set-AspectAndApply 0.5625
$portrait = $frame.Width
$expected = [Math]::Max(180, $height * 0.5625)
Write-Output ("竖屏: 期望 ≈" + $expected.ToString("0") + "，实际 " + $portrait.ToString("0"))

# 2) 横屏 1920x1080 -> 比例 1.777，应恢复铺满（NaN）
Set-AspectAndApply 1.7778
$landscape = $frame.Width
Write-Output ("横屏: 期望 NaN(铺满)，实际 " + $(if ([double]::IsNaN($landscape)) { "NaN" } else { $landscape.ToString("0") }))

# 3) 未知比例 -> 保持铺满
Set-AspectAndApply 0.0
$unknown = $frame.Width
Write-Output ("未知: 期望 NaN(铺满)，实际 " + $(if ([double]::IsNaN($unknown)) { "NaN" } else { $unknown.ToString("0") }))

$ok = ([Math]::Abs($portrait - $expected) -lt 1) -and [double]::IsNaN($landscape) -and [double]::IsNaN($unknown)
Write-Output ("---- 结果: " + $(if ($ok) { "PASS" } else { "FAIL" }) + " ----")
if (-not $ok) { exit 1 }
