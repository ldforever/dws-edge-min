<#
    B1 回归测试：两次回调合并 + 重复上报去重 + 幂等下发。

    它做的事（全部离线、不需要相机和加密狗）：
      1) 复制一份 runtime 到临时目录，往 spool 里写一段手工构造的事件：
            P1: detected + enriched + 重复的 detected + 重复的 enriched
            P2: detected + enriched + 重复的 enriched
            P3: detected + enriched（两次回调的 eventId 都是 1，验证不会误判）
      2) 启动平台，检查 /api/stats 与 /api/parcels：
            * 每个包裹只有一条记录
            * 重复事件被丢弃（duplicateEvents），且不重复计数（图片数、包裹数）
            * 只推送有效更新（publishedParcels）
      3) 下发幂等：ack 同一个 traceId 两次，第二次必须返回 alreadySent；失败的下发可重试
      4) 模拟"平台异常退出 + 位点丢失"，重启后 spool 被整段重读：
            包裹数、图片数、每次回调数都不允许变化

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-b1-dedup.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\test-b1-dedup.ps1 -SourceRuntime D:\dws\runtime -Port 8099
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = '',
    [int]$Port = 8099
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-b1-test' }

$platformExe = Join-Path $SourceRuntime 'platform\DwsEdge.Platform.exe'
if (!(Test-Path $platformExe)) {
    throw "找不到平台可执行文件：$platformExe（先跑一次 build.ps1）"
}

$baseUrl = 'http://127.0.0.1:' + $Port
$results = New-Object System.Collections.Generic.List[object]

function Add-Check {
    param([string]$Name, $Expected, $Actual)
    $ok = ("$Expected" -eq "$Actual")
    $results.Add([pscustomobject]@{ 检查项 = $Name; 期望 = "$Expected"; 实际 = "$Actual"; 结果 = $(if ($ok) { 'PASS' } else { 'FAIL' }) })
    return $ok
}

function Stop-Platform {
    Get-Process -Name 'DwsEdge.Platform' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like ($WorkDir + '*') } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

function Start-Platform {
    $exe = Join-Path $WorkDir 'platform\DwsEdge.Platform.exe'
    $log = Join-Path $WorkDir 'logs\b1-test-platform.log'
    # 用命令行 --urls 指定端口：它的优先级高于 appsettings.json 里的 Urls
    Start-Process -FilePath $exe -ArgumentList @('--urls', $baseUrl) `
        -WorkingDirectory (Join-Path $WorkDir 'platform') -WindowStyle Hidden `
        -RedirectStandardOutput $log -RedirectStandardError ($log + '.err') | Out-Null

    # 等就绪（最多 30 秒）
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        try {
            $null = Invoke-RestMethod -Uri ($baseUrl + '/api/health') -TimeoutSec 3
            return
        }
        catch {
        }
    }
    throw "平台在 30 秒内没有就绪：$baseUrl（看日志 " + $log + "）"
}

function Get-Stats {
    return Invoke-RestMethod -Uri ($baseUrl + '/api/stats') -TimeoutSec 10
}

function Get-Parcels {
    return @(Invoke-RestMethod -Uri ($baseUrl + '/api/parcels?limit=50') -TimeoutSec 10)
}

function Ack-Dispatch {
    param([string]$TraceId, [bool]$Success, [string]$Error = '')
    $body = @{ traceId = $TraceId; success = $Success; error = $Error } | ConvertTo-Json -Compress
    return Invoke-RestMethod -Uri ($baseUrl + '/api/dispatch/ack') -Method POST -ContentType 'application/json' -Body $body -TimeoutSec 10
}

function Get-PendingDispatch {
    return @(Invoke-RestMethod -Uri ($baseUrl + '/api/dispatch/pending?limit=50') -TimeoutSec 10)
}

# ---------------------------------------------------------------- 准备测试运行时
Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
Stop-Platform

if (Test-Path $WorkDir) {
    $backup = "$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Move-Item -LiteralPath $WorkDir -Destination $backup -Force
    Write-Host "旧目录已挪走：$backup" -ForegroundColor DarkGray
}

robocopy $SourceRuntime $WorkDir /E /XD spool data images logs /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $WorkDir 'spool'), (Join-Path $WorkDir 'data'), (Join-Path $WorkDir 'images'), (Join-Path $WorkDir 'logs') | Out-Null

# ---------------------------------------------------------------- 构造事件
$day = (Get-Date).ToString('yyyyMMdd')
$spoolFile = Join-Path $WorkDir ("spool\events-" + $day + ".jsonl")

function New-ParcelEvent {
    param(
        [long]$EventId, [string]$TraceId, [string]$Stage, [string]$Device, [long]$CapturedAtMs,
        [string[]]$Codes, [int]$Weight, [int]$Length, [int]$Width, [int]$Height, [string]$ImagePath
    )
    $codeItems = @()
    foreach ($c in $Codes) { $codeItems += @{ value = $c; kind = '1d'; position = 'top' } }
    $images = @()
    if (![string]::IsNullOrEmpty($ImagePath)) {
        $images += @{ kind = 'original'; deviceId = $Device; format = 'bmp'; width = 320; height = 240; bytes = 1000; path = $ImagePath }
    }
    $volume = 0
    if ($Weight -gt 0) { $volume = $Length * $Width * $Height }

    return (@{
        schemaVersion = 1; type = 'parcel'; eventId = $EventId; providerId = 'test'; deviceId = $Device;
        stage = $Stage; capturedAtMs = $CapturedAtMs; receivedAtMs = $CapturedAtMs; traceId = $TraceId;
        stagedResult = $true; weightGrams = $Weight; lengthMm = $Length; widthMm = $Width; heightMm = $Height;
        volumeMm3 = $volume; codes = $codeItems; images = $images
    } | ConvertTo-Json -Compress -Depth 6)
}

$lines = New-Object System.Collections.Generic.List[string]
# P1：正常两次回调 + 两条重复
$lines.Add((New-ParcelEvent -EventId 11 -TraceId 'P1' -Stage 'detected' -Device 'cam-top' -CapturedAtMs 1789666000000 -Codes @('YT1000000001') -Weight -1 -Length 0 -Width 0 -Height 0 -ImagePath 'D:\images\P1-ori.bmp'))
$lines.Add((New-ParcelEvent -EventId 12 -TraceId 'P1' -Stage 'enriched' -Device 'cam-top' -CapturedAtMs 1789666000000 -Codes @('YT1000000001') -Weight 500 -Length 300 -Width 200 -Height 150 -ImagePath ''))
$lines.Add((New-ParcelEvent -EventId 21 -TraceId 'P1' -Stage 'detected' -Device 'cam-top' -CapturedAtMs 1789666000000 -Codes @('YT1000000001') -Weight -1 -Length 0 -Width 0 -Height 0 -ImagePath 'D:\images\P1-ori.bmp'))
$lines.Add((New-ParcelEvent -EventId 22 -TraceId 'P1' -Stage 'enriched' -Device 'cam-top' -CapturedAtMs 1789666000000 -Codes @('YT1000000001') -Weight 500 -Length 300 -Width 200 -Height 150 -ImagePath ''))
# P2：正常两次回调 + 一条重复
$lines.Add((New-ParcelEvent -EventId 31 -TraceId 'P2' -Stage 'detected' -Device 'cam-side' -CapturedAtMs 1789666010000 -Codes @('SF2000000002') -Weight -1 -Length 0 -Width 0 -Height 0 -ImagePath ''))
$lines.Add((New-ParcelEvent -EventId 32 -TraceId 'P2' -Stage 'enriched' -Device 'cam-side' -CapturedAtMs 1789666010000 -Codes @('SF2000000002') -Weight 800 -Length 400 -Width 300 -Height 200 -ImagePath ''))
$lines.Add((New-ParcelEvent -EventId 33 -TraceId 'P2' -Stage 'enriched' -Device 'cam-side' -CapturedAtMs 1789666010000 -Codes @('SF2000000002') -Weight 800 -Length 400 -Width 300 -Height 200 -ImagePath ''))
# P3：两次回调的 eventId 都是 1（宿主重启后会重号）—— 不能被误判成重复
$lines.Add((New-ParcelEvent -EventId 1 -TraceId 'P3' -Stage 'detected' -Device 'cam-top' -CapturedAtMs 1789666020000 -Codes @('JD3000000003') -Weight -1 -Length 0 -Width 0 -Height 0 -ImagePath ''))
$lines.Add((New-ParcelEvent -EventId 1 -TraceId 'P3' -Stage 'enriched' -Device 'cam-top' -CapturedAtMs 1789666020000 -Codes @('JD3000000003') -Weight 300 -Length 200 -Width 150 -Height 100 -ImagePath ''))

[System.IO.File]::WriteAllLines($spoolFile, $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("已写入 spool " + $lines.Count + " 行：" + $spoolFile) -ForegroundColor DarkGray

# ---------------------------------------------------------------- 第 1 轮
Write-Host "`n=== 第 1 轮：正常消费（含 3 条重复事件）===" -ForegroundColor Cyan
Start-Platform
$stats = Get-Stats
Add-Check '包裹数（每个包裹一条记录）' 3 $stats.parcels | Out-Null
Add-Check '合并成功的包裹数（两次回调）' 3 $stats.mergedParcels | Out-Null
Add-Check '丢弃的重复事件数' 3 $stats.duplicateEvents | Out-Null
Add-Check '推送次数（重复不推）' 6 $stats.publishedParcels | Out-Null
Add-Check '图片计数（不重复计数）' 1 $stats.images | Out-Null
Add-Check '待下发包裹数' 3 $stats.dispatchPending | Out-Null

$parcels = Get-Parcels
Add-Check '包裹列表条数' 3 $parcels.Count | Out-Null
foreach ($traceId in @('P1', 'P2', 'P3')) {
    $item = $parcels | Where-Object { $_.traceId -eq $traceId }
    $updates = 'n/a'
    if ($item) { $updates = $item.updates }
    Add-Check ("$traceId 的有效回调次数") 2 $updates | Out-Null
}

# ---------------------------------------------------------------- 下发幂等
Write-Host "`n=== 第 2 轮：下发幂等（同一个 traceId ack 两次）===" -ForegroundColor Cyan
$first = Ack-Dispatch -TraceId 'P1' -Success $true
Add-Check 'P1 第 1 次 ack' 'sent' $first.state | Out-Null
$second = Ack-Dispatch -TraceId 'P1' -Success $true
Add-Check 'P1 第 2 次 ack（幂等）' 'True' $second.alreadySent | Out-Null
$null = Ack-Dispatch -TraceId 'P2' -Success $false -Error 'TCP timeout'
$stats = Get-Stats
Add-Check 'ack 后待下发' 1 $stats.dispatchPending | Out-Null
Add-Check 'ack 后已下发' 1 $stats.dispatchSent | Out-Null
Add-Check 'ack 后失败待重试' 1 $stats.dispatchFailed | Out-Null
Add-Check '待下发列表条数（一个 traceId 只出现一次）' 2 (Get-PendingDispatch).Count | Out-Null

# ---------------------------------------------------------------- 第 3 轮：位点丢失 + 重读
Write-Host "`n=== 第 3 轮：模拟异常退出（位点丢失）后重读整个 spool ===" -ForegroundColor Cyan
Stop-Platform
$offsets = Join-Path $WorkDir 'data\offsets.json'
if (Test-Path $offsets) {
    Move-Item -LiteralPath $offsets -Destination ($offsets + '.lost') -Force
    Write-Host '已把 offsets.json 改名（模拟位点丢失）' -ForegroundColor DarkGray
}
Start-Platform
$stats2 = Get-Stats
$parcels2 = Get-Parcels

Add-Check '重读后包裹数（不允许增加）' 3 $stats2.parcels | Out-Null
Add-Check '重读后图片计数（不允许翻倍）' 1 $stats2.images | Out-Null
Add-Check '重读后合并包裹数' 3 $stats2.mergedParcels | Out-Null
Add-Check '重读后丢弃的重复事件数（9 条全部判重）' 9 $stats2.duplicateEvents | Out-Null
Add-Check '重读后推送次数（没有新内容就不推）' 0 $stats2.publishedParcels | Out-Null
Add-Check '重读后待下发（下发状态保持）' 1 $stats2.dispatchPending | Out-Null
Add-Check '重读后已下发（下发状态保持）' 1 $stats2.dispatchSent | Out-Null
Add-Check '重读后失败待重试（下发状态保持）' 1 $stats2.dispatchFailed | Out-Null
foreach ($traceId in @('P1', 'P2', 'P3')) {
    $item = $parcels2 | Where-Object { $_.traceId -eq $traceId }
    $updates = 'n/a'
    if ($item) { $updates = $item.updates }
    Add-Check ("重读后 " + $traceId + " 的有效回调次数") 2 $updates | Out-Null
}

Stop-Platform

# ---------------------------------------------------------------- 汇总
Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('B1 回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    exit 0
}

Write-Host ('B1 回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
