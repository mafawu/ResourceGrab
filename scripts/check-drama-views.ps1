# 冒烟测试：在 STA 线程里实例化短剧相关视图与卡片模板，
# 目的是让 XAML 解析器真正跑一遍（编译期不校验 StaticResource 是否存在）。
param(
    [string]$AssemblyPath = "F:\ProcessProject\C#\ResourceGrab\bin\Debug\ResourceGrab.App.dll"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationFramework | Out-Null

$asm = [Reflection.Assembly]::LoadFrom($AssemblyPath)
Write-Output ("assembly: " + $asm.GetName().Name + " " + $asm.GetName().Version)

# 先加载 App.xaml 的资源字典（Icons / Colors / Tokens / Controls 都靠它）
$appType = $asm.GetType("ResourceGrab.App.App")
$app = [Activator]::CreateInstance($appType)
$appType.GetMethod("InitializeComponent").Invoke($app, @()) | Out-Null
Write-Output "App resources: OK"

# 视图里的控件（OnlineVideoPreviewPlayer）会在字段初始化时用 App.Services，
# 正常启动时它总是已就绪；测试里给一个空实现顶上，避免把"未初始化"误判成 XAML 错误。
Add-Type -TypeDefinition @"
public sealed class NullServiceProvider : System.IServiceProvider
{
    public object GetService(System.Type serviceType) => null;
}
"@ | Out-Null
$servicesProp = $appType.GetProperty("Services", [Reflection.BindingFlags]"Public,Static")
$servicesProp.SetValue($null, [NullServiceProvider]::new())
Write-Output "App.Services stub: OK"

$failed = 0
foreach ($name in @("ShortDramaBrowseView", "ShortDramaDetailView", "ShortDramaLocalView")) {
    $type = $asm.GetType("ResourceGrab.App.Views.$name")
    if ($null -eq $type) { Write-Output "MISS $name"; $failed++; continue }
    try {
        $view = [Activator]::CreateInstance($type)
        # 强制走一遍布局，触发模板/资源解析
        $view.Measure([System.Windows.Size]::new(1200, 800))
        $view.Arrange([System.Windows.Rect]::new(0, 0, 1200, 800))
        Write-Output "OK   $name"
    }
    catch {
        $failed++
        $inner = $_.Exception
        while ($inner.InnerException) { $inner = $inner.InnerException }
        Write-Output ("FAIL $name : " + $inner.GetType().Name + " " + $inner.Message)
    }
}

# 短剧卡片模板：构造一个 adapter 放进 MediaCard，强制选出 DramaCover 模板
try {
    $coreAsm = [Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $AssemblyPath) "ResourceGrab.Core.dll"))
    $summaryType = $coreAsm.GetType("ResourceGrab.Core.Sources.ShortDrama.ShortDramaSummary")
    $summary = [Activator]::CreateInstance($summaryType)
    $summaryType.GetProperty("Id").SetValue($summary, "5033")
    $summaryType.GetProperty("Title").SetValue($summary, "妈妈和姐姐用了我的系统都说好")
    $summaryType.GetProperty("CoverUrl").SetValue($summary, "")
    $summaryType.GetProperty("ScoreText").SetValue($summary, "8.4")
    $summaryType.GetProperty("EpisodeText").SetValue($summary, "更新至6集")
    $summaryType.GetProperty("Badge").SetValue($summary, "新上")

    $adapterType = $asm.GetType("ResourceGrab.App.ViewModels.ShortDramaCardAdapter")
    $adapter = [Activator]::CreateInstance($adapterType, @($summary, $null, $null))

    $cardType = $asm.GetType("ResourceGrab.App.Controls.MediaCard")
    $card = [Activator]::CreateInstance($cardType)
    $card.DataContext = $adapter
    $card.Width = 174
    # 脱离可视化树时 {Binding} 不会自动评估，手动喂一次内容；
    # 封面高度 = 卡片宽度 ×1.25 的绑定要等 Arrange 后才有 ActualWidth，属于既有卡片的通用模式，
    # 这里只验证「模板能被选中且能渲染出内容」，不断言具体高度。
    $card.Content.Content = $adapter
    $card.Measure([System.Windows.Size]::new(174, 400))
    $card.Arrange([System.Windows.Rect]::new(0, 0, 174, 400))
    $h = $card.DesiredSize.Height
    if ($h -lt 60) { throw "DramaCover 模板没有渲染出内容（高度 $h）" }
    Write-Output ("OK   MediaCard/DramaCover (desired=" + $card.DesiredSize.Width.ToString("0") + "x" + $h.ToString("0") + ")")
}
catch {
    $failed++
    $inner = $_.Exception
    while ($inner.InnerException) { $inner = $inner.InnerException }
    Write-Output ("FAIL MediaCard/DramaCover : " + $inner.GetType().Name + " " + $inner.Message)
}

Write-Output ("---- failed=$failed ----")
exit $failed
