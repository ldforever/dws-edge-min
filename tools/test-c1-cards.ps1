<#
    C1 自检：实时过包卡片墙（最新包裹卡片：条码 / 时间 / 相机 / 状态 / 缩略图）。

    这个脚本不测"像素"，测的是卡片墙依赖的那几条链路，并留下一个可以直接用浏览器看的运行时：
      1) 造 4 个包裹：普通有码、两次回调合并（卡片上会显示"更新 ×2"）、无码 NOREAD、无图；
      2) 校验 /api/parcels 返回的字段正好够卡片渲染（条码+方位+类型、时间、相机、图片路径、更新次数）；
      3) 校验卡片用的缩略图接口：/api/images/thumb?w=320 返回 BMP、尺寸 320x240、体积远小于原图；
      4) 再追加一个包裹（不重启平台）-> 立刻能查到，对应界面上的"实时刷新"；
      5) 静态检查前端产物里确实有卡片墙（cardWall / pcard / thumb?w=320），避免"代码没编进去"。

    跑完会在浏览器里打开 http://127.0.0.1:<端口> 看效果；不加 -KeepRunning 时脚本结束会停掉平台。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-c1-cards.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\test-c1-cards.ps1 -KeepRunning
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = '',
    [int]$Port = 8098,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-c1-test' }
if (![System.IO.Path]::IsPathRooted($SourceRuntime)) { $SourceRuntime = [System.IO.Path]::GetFullPath($SourceRuntime) }
if (![System.IO.Path]::IsPathRooted($WorkDir)) { $WorkDir = [System.IO.Path]::GetFullPath($WorkDir) }

$platformExe = Join-Path $SourceRuntime 'platform\DwsEdge.Platform.exe'
if (!(Test-Path $platformExe)) { throw "找不到平台可执行文件：$platformExe（先跑一次 build.ps1）" }

$baseUrl = 'http://127.0.0.1:' + $Port
$results = New-Object System.Collections.Generic.List[object]
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

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
    $log = Join-Path $WorkDir 'logs\c1-test-platform.log'
    Start-Process -FilePath $exe -ArgumentList @('--urls', $baseUrl) `
        -WorkingDirectory (Join-Path $WorkDir 'platform') -WindowStyle Hidden `
        -RedirectStandardOutput $log -RedirectStandardError ($log + '.err') | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        try { $null = Invoke-RestMethod -Uri ($baseUrl + '/api/health') -TimeoutSec 3; return } catch { }
    }
    throw "平台在 60 秒内没有就绪：$baseUrl"
}

function Get-Json {
    param([string]$Path)
    $raw = (Invoke-WebRequest -Uri ($baseUrl + $Path) -UseBasicParsing -TimeoutSec 60).Content
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    return ($raw | ConvertFrom-Json)
}

function New-TestBmp {
    param([int]$Width, [int]$Height, [string]$Path, [int]$Base = 40, [int]$Hue = 128)
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
    for ($y = 0; $y -lt $Height; $y++) {
        $row = 54 + $y * $stride
        for ($x = 0; $x -lt $Width; $x++) {
            $o = $row + $x * 3
            $bytes[$o] = [byte](($Base + $x) % 256)
            $bytes[$o + 1] = [byte](($Base + $y) % 256)
            $bytes[$o + 2] = [byte]$Hue
        }
    }
    [System.IO.File]::WriteAllBytes($Path, $bytes)
    return $bytes.Length
}

Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
Stop-Platform
if (Test-Path $WorkDir) {
    Move-Item -LiteralPath $WorkDir -Destination ("$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force
}
robocopy $SourceRuntime $WorkDir /E /XD spool data images logs cache /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $WorkDir 'spool'), (Join-Path $WorkDir 'data'), `
    (Join-Path $WorkDir 'images\20260918\cam-top'), (Join-Path $WorkDir 'images\20260918\cam-left'), `
    (Join-Path $WorkDir 'images\20260918\cam-bottom'), (Join-Path $WorkDir 'logs') | Out-Null

# 卡片墙是只读界面，演示环境把鉴权关掉，省得登录遮罩挡住（现场默认是开的）
[System.IO.File]::WriteAllText((Join-Path $WorkDir 'config\auth.json'),
    '{"enabled":false,"protectRead":false,"allowServiceKey":true,"serviceKey":"c1-demo","serviceKeyRole":"admin","maxFailures":5,"lockMinutes":15,"failureWindowMinutes":10,"sessionMinutes":480}',
    $utf8NoBom)

$spoolFile = Join-Path $WorkDir ('spool\events-' + (Get-Date).ToString('yyyyMMdd') + '.jsonl')

try {
    # ---------------------------------------------------------------- 1) 造 4 个包裹 + 4 张图
    Write-Host "`n=== 1) 造数据：有码 / 两次回调合并 / 无码 / 无图 ===" -ForegroundColor Cyan
    $img1 = Join-Path $WorkDir 'images\20260918\cam-top\parcel-1.bmp'
    $img2 = Join-Path $WorkDir 'images\20260918\cam-left\parcel-2.bmp'
    $img3 = Join-Path $WorkDir 'images\20260918\cam-bottom\parcel-3.bmp'
    $size1 = New-TestBmp -Width 800 -Height 600 -Path $img1 -Base 40 -Hue 90
    $null = New-TestBmp -Width 800 -Height 600 -Path $img2 -Base 90 -Hue 150
    $null = New-TestBmp -Width 800 -Height 600 -Path $img3 -Base 140 -Hue 40
    Write-Host ("原图：" + $size1 + " 字节（800x600 BMP）") -ForegroundColor DarkGray

    $now = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
    $image = {
        param($path, $cam)
        @{ kind = 'original'; deviceId = $cam; format = 'bmp'; width = 800; height = 600; bytes = 1440054; path = $path }
    }

    $events = @(
        @{ schemaVersion = 1; type = 'parcel'; eventId = 1; providerId = 'demo'; deviceId = 'cam-top'; stage = 'detected'
           capturedAtMs = ($now - 9000); receivedAtMs = ($now - 9000); traceId = 'C1-1'; stagedResult = $true; weightGrams = -1
           codes = @(@{ value = 'SF700000000001'; kind = '1d'; position = 'top' }); images = @(& $image $img1 'cam-top') },
        @{ schemaVersion = 1; type = 'parcel'; eventId = 2; providerId = 'demo'; deviceId = 'cam-top'; stage = 'enriched'
           capturedAtMs = ($now - 9000); receivedAtMs = ($now - 8500); traceId = 'C1-1'; stagedResult = $true; weightGrams = 1240
           lengthMm = 320; widthMm = 210; heightMm = 160; volumeMm3 = 10752000
           codes = @(@{ value = 'SF700000000001'; kind = '1d'; position = 'top' }); images = @() },
        @{ schemaVersion = 1; type = 'parcel'; eventId = 3; providerId = 'demo'; deviceId = 'cam-left'; stage = 'enriched'
           capturedAtMs = ($now - 6000); receivedAtMs = ($now - 6000); traceId = 'C1-2'; stagedResult = $false; weightGrams = 860
           lengthMm = 280; widthMm = 190; heightMm = 120; volumeMm3 = 6384000
           codes = @(); images = @(& $image $img2 'cam-left') },
        @{ schemaVersion = 1; type = 'parcel'; eventId = 4; providerId = 'demo'; deviceId = 'cam-bottom'; stage = 'enriched'
           capturedAtMs = ($now - 4000); receivedAtMs = ($now - 4000); traceId = 'C1-3'; stagedResult = $false; weightGrams = 2100
           lengthMm = 420; widthMm = 300; heightMm = 250; volumeMm3 = 31500000
           codes = @(@{ value = 'JD0000000000003'; kind = '2d'; position = 'bottom' }); images = @(& $image $img3 'cam-bottom') },
        @{ schemaVersion = 1; type = 'parcel'; eventId = 5; providerId = 'demo'; deviceId = 'cam-top'; stage = 'enriched'
           capturedAtMs = ($now - 1000); receivedAtMs = ($now - 1000); traceId = 'C1-4'; stagedResult = $false; weightGrams = -1
           codes = @(@{ value = 'YT0000000000004'; kind = '1d'; position = 'top' }); images = @() }
    )

    $lines = @()
    foreach ($item in $events) { $lines += ($item | ConvertTo-Json -Compress -Depth 6) }
    [System.IO.File]::WriteAllLines($spoolFile, $lines, $utf8NoBom)

    Start-Platform
    Start-Sleep -Seconds 5

    # ---------------------------------------------------------------- 2) 卡片渲染需要的字段
    Write-Host "`n=== 2) /api/parcels 返回的字段够卡片用 ===" -ForegroundColor Cyan
    $parcels = @(Get-Json '/api/parcels?limit=10')
    Add-Check '四个包裹都入库（合并成 4 条）' 4 @($parcels).Count

    $newest = $parcels[0]
    Add-Check '最新一条在最前（卡片墙第一张）' 'C1-4' $newest.traceId
    Add-Check '带条码值（卡片上的条码）' 'YT0000000000004' $newest.codes[0]
    Add-Check '带相机（卡片上的相机）' 'cam-top' $newest.deviceId
    Add-Check '带时间（卡片上的时间）' 'True' ([string]::IsNullOrEmpty($newest.time) -eq $false)

    $merged = @($parcels | Where-Object { $_.traceId -eq 'C1-1' })[0]
    Add-Check '两次回调合并成一条（卡片显示 更新 ×2）' 2 $merged.updates
    Add-Check '合并后补齐重量' 1240 $merged.weightGrams
    Add-Check '合并后保留图片' 1 $merged.imageCount

    $noread = @($parcels | Where-Object { $_.traceId -eq 'C1-2' })[0]
    Add-Check '无码包裹（卡片显示 NOREAD）' 0 $noread.codeCount
    Add-Check '无码包裹也有图（卡片照常显示缩略图）' 1 $noread.imageCount

    $detail = @($parcels | Where-Object { $_.traceId -eq 'C1-3' })[0]
    Add-Check '条码带方位（卡片显示 底面）' 'bottom' $detail.codeDetails[0].position
    Add-Check '条码带类型（卡片显示 2D）' '2d' $detail.codeDetails[0].kind

    $noImage = @($parcels | Where-Object { $_.traceId -eq 'C1-4' })[0]
    Add-Check '无图包裹（卡片显示"无图"占位）' 0 $noImage.imageCount
    Add-Check '无图包裹没有图片路径' 'True' ([string]::IsNullOrEmpty($noImage.firstImagePath))

    # ---------------------------------------------------------------- 3) 卡片用的缩略图接口
    Write-Host "`n=== 3) 缩略图（卡片直接显示的那张图）===" -ForegroundColor Cyan
    $thumbUrl = $baseUrl + '/api/images/thumb?w=320&path=' + [uri]::EscapeDataString($img1)
    $thumb = (Invoke-WebRequest -Uri $thumbUrl -UseBasicParsing -TimeoutSec 60).Content
    if ($thumb -is [string]) { $thumbBytes = [System.Text.Encoding]::GetEncoding(28591).GetBytes($thumb) } else { $thumbBytes = [byte[]]$thumb }
    Add-Check '返回的是 BMP（BM 开头）' 'True' ($thumbBytes[0] -eq 66 -and $thumbBytes[1] -eq 77)
    Add-Check '缩略图宽度 320' 320 ([BitConverter]::ToInt32($thumbBytes, 18))
    Add-Check '缩略图高度 240（等比）' 240 ([BitConverter]::ToInt32($thumbBytes, 22))
    Add-Check '体积远小于原图（< 1/5）' 'True' ($thumbBytes.Length -lt ($size1 / 5))
    Write-Host ("缩略图 " + $thumbBytes.Length + " 字节（原图 " + $size1 + " 字节）") -ForegroundColor DarkGray

    # ---------------------------------------------------------------- 4) 实时刷新（不重启平台）
    Write-Host "`n=== 4) 再推一个包裹：对应界面上的实时刷新 ===" -ForegroundColor Cyan
    $live = @{ schemaVersion = 1; type = 'parcel'; eventId = 99; providerId = 'demo'; deviceId = 'cam-right'; stage = 'enriched'
        capturedAtMs = [DateTimeOffset]::Now.ToUnixTimeMilliseconds(); receivedAtMs = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
        traceId = 'C1-5-LIVE'; stagedResult = $false; weightGrams = 640
        lengthMm = 250; widthMm = 180; heightMm = 90; volumeMm3 = 4050000
        codes = @(@{ value = 'ZTO00000000005'; kind = '1d'; position = 'right' })
        images = @(& $image $img2 'cam-right') } | ConvertTo-Json -Compress -Depth 6
    [System.IO.File]::AppendAllText($spoolFile, $live + "`n", $utf8NoBom)
    Start-Sleep -Seconds 3

    $after = @(Get-Json '/api/parcels?limit=10')
    Add-Check '追加后立刻能查到（SSE 会把它推到卡片墙最前面）' 5 @($after).Count
    Add-Check '新包裹在最前' 'C1-5-LIVE' $after[0].traceId

    # ---------------------------------------------------------------- 5) 前端产物：大图 + 列表 + 绿框
    #
    # 2026-09-23 改版：过包区从"卡片墙 / 表格 二选一切换"改成"上面固定大图 + 下面限高滚动列表"，
    # 大图上叠相机给的解码绿框。所以这一段断言跟着换 —— 断的还是"链路在不在"，
    # 不是"界面长什么样"：视图框、列表、绿框层、跟随开关、上限。
    Write-Host "`n=== 5) 部署的前端产物里带大图 + 列表 + 绿框 ===" -ForegroundColor Cyan
    $parcelJs = Join-Path $WorkDir 'platform\wwwroot\js\parcelview.js'
    $indexHtml = Join-Path $WorkDir 'platform\wwwroot\index.html'
    Add-Check 'parcelview.js 存在（大图 + 列表 + 绿框都在这里）' 'True' (Test-Path $parcelJs)
    if (Test-Path $parcelJs) {
        $js = [System.IO.File]::ReadAllText($parcelJs)
        # 大图地址由 api.imageUrl() 拼（/api/images?path=... 在 api.js 里），这里断它走的是那个接口
        Add-Check '大图走图片读取接口（api.imageUrl）' 'True' ($js -match 'imageUrl')
        Add-Check '把 SDK 的点坐标画成折线（polyline）' 'True' ($js -match 'polyline')
        Add-Check '归一化坐标（viewBox 0 0 1 1）' 'True' ($js -match '0 0 1 1')
        Add-Check '线宽不随缩放变形（non-scaling-stroke）' 'True' ($js -match 'non-scaling-stroke')
        Add-Check '框上标单号' 'True' ($js -match 'boxlabel')
        Add-Check '列表有上限（不会无限增长）' 'True' ($js -match 'MAX_ROWS')
        Add-Check '点行暂停跟随 + 回到最新' 'True' (($js -match 'following') -and ($js -match 'pvFollow'))
    }
    if (Test-Path $indexHtml) {
        $html = [System.IO.File]::ReadAllText($indexHtml)
        Add-Check 'index.html 有大图视图框' 'True' (($html -match 'id="pvViewer"') -and ($html -match 'id="pvImage"'))
        Add-Check 'index.html 有绿框叠层' 'True' ($html -match 'id="pvBoxLayer"')
        Add-Check 'index.html 有过包列表（限高滚动）' 'True' ($html -match 'id="pvList"')
        Add-Check '不再有卡片/表格切换' 'False' (($html -match 'id="cardWall"') -or ($html -match 'id="viewCards"'))
    }
}
finally {
    if (!$KeepRunning) { Stop-Platform }
}

Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('C1 自检通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    if ($KeepRunning) {
        Write-Host ('平台还在跑，浏览器打开：' + $baseUrl + '　看实时过包卡片墙') -ForegroundColor Yellow
    }
    exit 0
}
Write-Host ('C1 自检失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
