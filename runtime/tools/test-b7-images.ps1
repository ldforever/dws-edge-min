<#
    B7 回归测试：图片按需访问与缩略图。

    验收点：前端按需取缩略图与原图；取图不占用采集进程。

    脚本做的事：
      1) 造一张 800x600 的 BMP 放进 runtime\images\（模拟采集侧落盘的原图）；
      2) /api/images/info 能读出尺寸/字节/是否支持缩略图；
      3) /api/images/thumb?w=200 返回的确实是 200x150、体积远小于原图（BMP 真缩小）；
      4) 第二次请求命中缓存（更快、字节完全一致）；
      5) 越权路径（图片目录之外）被拒绝；不存在的文件被拒绝；
      6) JPEG 原图会回退成原图（离线环境没有 JPEG 解码器，这是已知取舍）；
      7) 并发 8 个缩略图请求全部成功，且期间平台采集照常（再喂一个包裹能入库）。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-b7-images.ps1
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = '',
    [int]$Port = 8095
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-b7-test' }

$platformExe = Join-Path $SourceRuntime 'platform\DwsEdge.Platform.exe'
if (!(Test-Path $platformExe)) { throw "找不到平台可执行文件：$platformExe（先跑一次 build.ps1）" }

$baseUrl = 'http://127.0.0.1:' + $Port
$results = New-Object System.Collections.Generic.List[object]

function Add-Check {
    param([string]$Name, $Expected, $Actual)
    $ok = ("$Expected" -eq "$Actual")
    $results.Add([pscustomobject]@{
        检查项 = $Name; 期望 = "$Expected"; 实际 = "$Actual"; 结果 = $(if ($ok) { 'PASS' } else { 'FAIL' })
    }) | Out-Null
}

function Stop-Platform {
    Get-Process -Name 'DwsEdge.Platform' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like ($WorkDir + '*') } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

function Start-Platform {
    $exe = Join-Path $WorkDir 'platform\DwsEdge.Platform.exe'
    $log = Join-Path $WorkDir 'logs\b7-test-platform.log'
    Start-Process -FilePath $exe -ArgumentList @('--urls', $baseUrl) `
        -WorkingDirectory (Join-Path $WorkDir 'platform') -WindowStyle Hidden `
        -RedirectStandardOutput $log -RedirectStandardError ($log + '.err') | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        try { $null = Invoke-RestMethod -Uri ($baseUrl + '/api/health') -TimeoutSec 3; return } catch { }
    }
    throw "平台在 60 秒内没有就绪：$baseUrl"
}

function Get-Bytes {
    param([string]$Url)
    $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 60
    $content = $response.Content
    if ($content -is [string]) {
        return [System.Text.Encoding]::GetEncoding(28591).GetBytes($content)
    }
    return [byte[]]$content
}

function New-TestBmp {
    param([int]$Width, [int]$Height, [string]$Path, [int]$BaseValue = 40)
    $stride = [int]([Math]::Ceiling($Width * 3 / 4.0) * 4)
    $bytes = New-Object byte[] (54 + $stride * $Height)
    $bytes[0] = 66; $bytes[1] = 77
    [BitConverter]::GetBytes($bytes.Length).CopyTo($bytes, 2)
    [BitConverter]::GetBytes(54).CopyTo($bytes, 10)
    [BitConverter]::GetBytes(40).CopyTo($bytes, 14)
    [BitConverter]::GetBytes($Width).CopyTo($bytes, 18)
    [BitConverter]::GetBytes($Height).CopyTo($bytes, 22)
    [BitConverter]::GetBytes([int16]1).CopyTo($bytes, 26)
    [BitConverter]::GetBytes([int16]24).CopyTo($bytes, 28)
    # 画个渐变，免得全 0（也顺便验证降采样确实在算像素）
    for ($y = 0; $y -lt $Height; $y++) {
        $row = 54 + $y * $stride
        for ($x = 0; $x -lt $Width; $x++) {
            $o = $row + $x * 3
            $bytes[$o] = [byte](($BaseValue + $x) % 256)
            $bytes[$o + 1] = [byte](($BaseValue + $y) % 256)
            $bytes[$o + 2] = 128
        }
    }
    $dir = Split-Path -Parent $Path
    if (!(Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [System.IO.File]::WriteAllBytes($Path, $bytes)
    return $bytes.Length
}

Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
Stop-Platform
if (Test-Path $WorkDir) {
    Move-Item -LiteralPath $WorkDir -Destination ("$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force
}
robocopy $SourceRuntime $WorkDir /E /XD spool data images logs cache /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $WorkDir 'spool'), (Join-Path $WorkDir 'data'), (Join-Path $WorkDir 'images'), (Join-Path $WorkDir 'logs') | Out-Null

$imageDir = Join-Path $WorkDir 'images\20260918\cam-top'
$bmpPath = Join-Path $imageDir 'parcel-800x600.bmp'
$jpgPath = Join-Path $imageDir 'parcel-800x600.jpg'

try {
    $originalBytes = New-TestBmp -Width 800 -Height 600 -Path $bmpPath
    [System.IO.File]::WriteAllBytes($jpgPath, (New-Object byte[] 50000))   # 假 JPEG：只要验证"回退原图"这条逻辑
    Write-Host ("测试图片：BMP " + $originalBytes + " 字节；JPEG 50000 字节") -ForegroundColor DarkGray

    Start-Platform
    . (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'b9-auth-helper.ps1')
    $null = Enable-TestAuth -WorkDir $WorkDir   # B9：管理接口要凭据，脚本用服务令牌

    # ---------------------------------------------------------------- 1) 元信息
    Write-Host "`n=== 1) 图片元信息 ===" -ForegroundColor Cyan
    $info = Invoke-RestMethod -Uri ($baseUrl + '/api/images/info?path=' + [uri]::EscapeDataString($bmpPath)) -TimeoutSec 20
    Add-Check '能读到元信息' 'True' $info.ok
    Add-Check '字节数正确' $originalBytes $info.bytes
    Add-Check '后缀' 'bmp' $info.extension
    Add-Check 'BMP 支持缩略图' 'True' $info.thumbSupported

    # ---------------------------------------------------------------- 2) 缩略图
    Write-Host "`n=== 2) 缩略图（真缩小 + 缓存）===" -ForegroundColor Cyan
    $url200 = $baseUrl + '/api/images/thumb?w=200&path=' + [uri]::EscapeDataString($bmpPath)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $thumb = Get-Bytes -Url $url200
    $sw.Stop()
    $firstMs = $sw.ElapsedMilliseconds

    Add-Check '返回的是 BMP（BM 开头）' 'True' ($thumb[0] -eq 66 -and $thumb[1] -eq 77)
    Add-Check '缩略图宽度' 200 ([BitConverter]::ToInt32($thumb, 18))
    Add-Check '缩略图高度（等比）' 150 ([BitConverter]::ToInt32($thumb, 22))
    Add-Check '体积显著变小（< 原图 1/5）' 'True' ($thumb.Length -lt ($originalBytes / 5))
    Write-Host ("首次生成耗时：" + $firstMs + " ms，缩略图 " + $thumb.Length + " 字节") -ForegroundColor Yellow

    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $thumb2 = Get-Bytes -Url $url200
    $sw2.Stop()
    Add-Check '第二次字节完全一致' 'True' (($thumb -join ',') -eq ($thumb2 -join ','))
    Write-Host ("缓存命中耗时：" + $sw2.ElapsedMilliseconds + " ms（这次请求的开销主要是 HTTP 传输，不是计算）") -ForegroundColor Yellow

    # 判断"命中缓存"不要用耗时（来回传 90KB 的 HTTP 开销占了大头），
    # 直接看缓存文件有没有被重写：命中缓存就不会重新生成。
    $cacheFiles = @(Get-ChildItem (Join-Path $WorkDir 'cache\thumbs') -Filter *.bmp -ErrorAction SilentlyContinue)
    Add-Check '缩略图缓存目录已生成' 'True' ($cacheFiles.Count -ge 1)
    if ($cacheFiles.Count -ge 1) {
        $stamp = $cacheFiles[0].LastWriteTimeUtc
        Start-Sleep -Seconds 1
        $null = Get-Bytes -Url $url200
        Add-Check '命中缓存时不会重新生成（文件未被改写）' 'True' ($stamp -eq (Get-Item $cacheFiles[0].FullName).LastWriteTimeUtc)
    }

    # ---------------------------------------------------------------- 3) 越权与不存在的文件
    Write-Host "`n=== 3) 目录白名单与不存在的文件 ===" -ForegroundColor Cyan
    $outside = 'C:\Windows\win.ini'
    try {
        $null = Invoke-WebRequest -Uri ($baseUrl + '/api/images?path=' + [uri]::EscapeDataString($outside)) -UseBasicParsing -TimeoutSec 20
        Add-Check '图片目录之外被拒绝' 400 '200（不符合预期）'
    }
    catch {
        Add-Check '图片目录之外被拒绝' 400 ([int]$_.Exception.Response.StatusCode)
    }
    try {
        $null = Invoke-WebRequest -Uri ($baseUrl + '/api/images/thumb?w=200&path=' + [uri]::EscapeDataString($outside)) -UseBasicParsing -TimeoutSec 20
        Add-Check '缩略图同样受白名单限制' 400 '200（不符合预期）'
    }
    catch {
        Add-Check '缩略图同样受白名单限制' 400 ([int]$_.Exception.Response.StatusCode)
    }
    $missing = Join-Path $imageDir 'not-exists.bmp'
    try {
        $null = Invoke-WebRequest -Uri ($baseUrl + '/api/images?path=' + [uri]::EscapeDataString($missing)) -UseBasicParsing -TimeoutSec 20
        Add-Check '不存在的文件返回 404' 404 '200（不符合预期）'
    }
    catch {
        Add-Check '不存在的文件返回 404' 404 ([int]$_.Exception.Response.StatusCode)
    }

    # ---------------------------------------------------------------- 4) JPEG 回退
    Write-Host "`n=== 4) JPEG 原图回退（离线无解码库）===" -ForegroundColor Cyan
    $thumbJpg = Get-Bytes -Url ($baseUrl + '/api/images/thumb?w=200&path=' + [uri]::EscapeDataString($jpgPath))
    Add-Check 'JPEG 回退时返回原图字节' 50000 $thumbJpg.Length
    $infoJpg = Invoke-RestMethod -Uri ($baseUrl + '/api/images/info?path=' + [uri]::EscapeDataString($jpgPath)) -TimeoutSec 20
    Add-Check 'JPEG 标注为不支持缩略图' 'False' $infoJpg.thumbSupported

    # ---------------------------------------------------------------- 5) 并发 + 采集不受影响
    Write-Host "`n=== 5) 并发取图 + 采集不受影响 ===" -ForegroundColor Cyan
    $jobs = 1..8 | ForEach-Object {
        Start-Job -ScriptBlock { param($u) (Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 60).RawContentLength } -ArgumentList $url200
    }
    $null = Wait-Job -Job $jobs -Timeout 60
    $okCount = @($jobs | Where-Object { $_.State -eq 'Completed' }).Count
    $jobs | Remove-Job -Force -ErrorAction SilentlyContinue
    Add-Check '并发 8 个缩略图请求全部成功' 8 $okCount

    $spoolFile = Join-Path $WorkDir ('spool\events-' + (Get-Date).ToString('yyyyMMdd') + '.jsonl')
    $now = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
    $event = @{
        schemaVersion = 1; type = 'parcel'; eventId = 1; providerId = 'test'; deviceId = 'cam-top';
        stage = 'enriched'; capturedAtMs = $now; receivedAtMs = $now; traceId = 'B7-1';
        stagedResult = $true; weightGrams = 500; lengthMm = 300; widthMm = 200; heightMm = 150; volumeMm3 = 9000000;
        codes = @(@{ value = 'SF000000000001'; kind = '1d'; position = 'top' });
        images = @(@{ kind = 'original'; deviceId = 'cam-top'; format = 'bmp'; width = 800; height = 600; bytes = $originalBytes; path = $bmpPath })
    } | ConvertTo-Json -Compress -Depth 6
    [System.IO.File]::WriteAllLines($spoolFile, @($event), (New-Object System.Text.UTF8Encoding($false)))
    Start-Sleep -Seconds 5

    $stats = Invoke-RestMethod -Uri ($baseUrl + '/api/stats') -TimeoutSec 20
    Add-Check '取图期间采集照常（包裹已入库）' 'True' ($stats.parcels -ge 1)
    $parcels = (Invoke-WebRequest -Uri ($baseUrl + '/api/parcels?limit=5') -UseBasicParsing -TimeoutSec 20).Content
    Add-Check '包裹上带着缩略图要用的图片路径' 'True' ($parcels -match 'parcel-800x600\.bmp')
}
finally {
    Stop-Platform
}

Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('B7 回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    exit 0
}
Write-Host ('B7 回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
