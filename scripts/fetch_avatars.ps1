$ErrorActionPreference = 'Continue'
$proxy      = 'http://127.0.0.1:10809'
$libPath    = 'F:\ProcessProject\C#\ResourceGrab\bin\Release\config\video-library.json'
$indexPath  = 'F:\ProcessProject\C#\ResourceGrab\bin\Release\config\gfriends\Filetree.json'
$avatarDir  = 'F:\ProcessProject\C#\ResourceGrab\bin\Release\config\avatars'
New-Item -ItemType Directory -Force -Path $avatarDir | Out-Null

# ── 1. 读库，收集全部演员名（去重）──
$lib   = Get-Content $libPath -Raw -Encoding UTF8 | ConvertFrom-Json
$actors = @{}
foreach ($item in $lib) {
    if ($null -eq $item.Actors) { continue }
    foreach ($a in $item.Actors) {
        if ($a -and -not $actors.ContainsKey($a)) { $actors[$a] = 1 }
    }
}
Write-Host ("演员总数: " + $actors.Keys.Count)

# ── 2. 读 gfriends 索引：名字 -> 公司|实际文件 ──
$tree = Get-Content $indexPath -Raw -Encoding UTF8 | ConvertFrom-Json
$map = @{}
foreach ($comp in $tree.Content.PSObject.Properties) {
    foreach ($e in $comp.Value.PSObject.Properties) {
        $file = [string]$e.Value
        $q = $file.IndexOf('?'); if ($q -ge 0) { $file = $file.Substring(0, $q) }
        if (-not $file) { continue }
        $name = $e.Name -replace '\.(jpg|jpeg|png|webp)$', ''
        if ($name -and -not $map.ContainsKey($name)) { $map[$name] = @($comp.Name, $file) }
    }
}
Write-Host ("索引名字数: " + $map.Count)

# ── 3. 逐个下载缺失的头像 ──
$sha = [System.Security.Cryptography.SHA256]::Create()
$done = 0; $hit = 0; $skip = 0; $miss = 0; $fail = 0
$total = $actors.Keys.Count
foreach ($name in $actors.Keys) {
    $done++
    $hash = -join ($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($name))[0..15] | ForEach-Object { $_.ToString('x2') })
    $out = Join-Path $avatarDir ($hash + '.jpg')
    if (Test-Path $out) { $skip++; continue }

    $seg = $map[$name]
    if (-not $seg) { $miss++; continue }

    $url = 'https://raw.githubusercontent.com/gfriends/gfriends/master/Content/' +
           [System.Uri]::EscapeDataString($seg[0]) + '/' + [System.Uri]::EscapeDataString($seg[1])
    try {
        Invoke-WebRequest -Proxy $proxy -Uri $url -OutFile $out -TimeoutSec 20 -UseBasicParsing
        $hit++
    } catch { $fail++ }

    if ($done % 25 -eq 0) { Write-Host ("进度 {0}/{1} 命中{2} 缺索引{3} 失败{4} 已有{5}" -f $done, $total, $hit, $miss, $fail, $skip) }
}
Write-Host ("完成: 总{0} 新下载{1} 缺索引{2} 失败{3} 已存在{4}" -f $total, $hit, $miss, $fail, $skip)
