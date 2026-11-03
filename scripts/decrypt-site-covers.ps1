# 把站点前 6 张封面抓下来并解密，落到 %TEMP%\drama-covers-live（纯 HTTP + AES，不碰 WPF）
$ErrorActionPreference = "Stop"
$root = "F:\ProcessProject\C#\ResourceGrab"
$core = [Reflection.Assembly]::LoadFrom("$root\bin\Debug\ResourceGrab.Core.dll")
$cipher = $core.GetType("ResourceGrab.Core.Services.ShortDrama.DramaCoverCipher")
$tryDecrypt = $cipher.GetMethod("TryDecrypt")

$ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36"
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AutomaticDecompression = [System.Net.DecompressionMethods]::All
$handler.Proxy = New-Object System.Net.WebProxy("http://127.0.0.1:10809")
$http = [System.Net.Http.HttpClient]::new($handler)
$http.Timeout = [TimeSpan]::FromSeconds(30)
$http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $ua) | Out-Null

$html = $http.GetStringAsync("https://xiguaai.net/newest").GetAwaiter().GetResult()
Write-Output ("listing: " + $html.Length + " chars")

$matches = [regex]::Matches($html, 'data-track-id="(\d+)" data-track-title="([^"]*)"[\s\S]{0,400}?data-src="(https://pic\.drleao\.cn/[^"]+)"')
$dir = Join-Path $env:TEMP "drama-covers-live"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Get-ChildItem $dir -File -ErrorAction SilentlyContinue | Remove-Item -Force

$n = 0
$meta = @()
foreach ($m in $matches) {
    if ($n -ge 6) { break }
    $id = $m.Groups[1].Value
    $title = $m.Groups[2].Value
    $url = $m.Groups[3].Value -replace '&amp;', '&'
    $raw = $http.GetByteArrayAsync($url).GetAwaiter().GetResult()
    $plain = $tryDecrypt.Invoke($null, [object[]]@(, $raw))
    if ($null -eq $plain) { Write-Output ("  $id 解密失败"); continue }
    # 按真实类型给扩展名：BitmapImage 靠扩展名选解码器，PNG 存成 .jpg 会解不出来
    $ext = if ($plain[0] -eq 0xFF -and $plain[1] -eq 0xD8) { ".jpg" } elseif ($plain[0] -eq 0x89 -and $plain[1] -eq 0x50) { ".png" } else { ".img" }
    $path = Join-Path $dir ("$id$ext")
    [System.IO.File]::WriteAllBytes($path, $plain)
    Write-Output ("  $id : 密文 " + $raw.Length + " → 明文 " + $plain.Length + "  $title")
    $meta += [pscustomobject]@{ id = $id; title = $title; file = "$id$ext" }
    $n++
}
$meta | ConvertTo-Json | Set-Content (Join-Path $dir "titles.json") -Encoding UTF8
Write-Output ("done, files: " + (Get-ChildItem $dir -File).Count + " in " + $dir)
