<#
    B3 回归测试：历史库记录 + 查询 + 导出。

    覆盖的验收点：
      1) 记录字段：条码、时间、相机、图片路径、无码标记、下发状态；
      2) 查询：按时间 / 条码 / 相机 / 无码 / 下发状态过滤，分页正确；
      3) 性能：10 万条历史数据，查询（含过滤）在 2 秒内返回；
      4) 导出：CSV 行数与命中数一致、表头字段齐全；
      5) 断电不丢：写入事件后硬杀进程（不优雅退出），重启后仍能查到。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-b3-history.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\test-b3-history.ps1 -Records 100000 -SourceRuntime D:\dws\runtime -Port 8097
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = '',
    [int]$Port = 8097,
    [int]$Records = 100000
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-b3-test' }

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
    $log = Join-Path $WorkDir 'logs\b3-test-platform.log'
    Start-Process -FilePath $exe -ArgumentList @('--urls', $baseUrl) `
        -WorkingDirectory (Join-Path $WorkDir 'platform') -WindowStyle Hidden `
        -RedirectStandardOutput $log -RedirectStandardError ($log + '.err') | Out-Null

    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        try {
            $null = Invoke-RestMethod -Uri ($baseUrl + '/api/health') -TimeoutSec 3
            return
        }
        catch {
        }
    }
    throw "平台在 60 秒内没有就绪：$baseUrl（看日志 " + $log + "）"
}

function Get-Json {
    param([string]$Path)
    $text = (Invoke-WebRequest -Uri ($baseUrl + $Path) -UseBasicParsing -TimeoutSec 60).Content
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return $text | ConvertFrom-Json
}

function New-ParcelEvent {
    param([long]$EventId, [string]$TraceId, [string]$Stage, [string]$Device, [long]$CapturedAtMs,
          [string[]]$Codes, [int]$Weight, [string]$ImagePath)
    $codeItems = @()
    foreach ($c in $Codes) { $codeItems += @{ value = $c; kind = '1d'; position = 'top' } }
    $images = @()
    if (![string]::IsNullOrEmpty($ImagePath)) {
        $images += @{ kind = 'original'; deviceId = $Device; format = 'bmp'; width = 320; height = 240; bytes = 1000; path = $ImagePath }
    }
    return (@{
        schemaVersion = 1; type = 'parcel'; eventId = $EventId; providerId = 'test'; deviceId = $Device;
        stage = $Stage; capturedAtMs = $CapturedAtMs; receivedAtMs = $CapturedAtMs; traceId = $TraceId;
        stagedResult = $true; weightGrams = $Weight; lengthMm = 0; widthMm = 0; heightMm = 0; volumeMm3 = 0;
        codes = $codeItems; images = $images
    } | ConvertTo-Json -Compress -Depth 6)
}

Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
Stop-Platform

if (Test-Path $WorkDir) {
    $backup = "$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Move-Item -LiteralPath $WorkDir -Destination $backup -Force
    Write-Host "旧目录已挪走：$backup" -ForegroundColor DarkGray
}

robocopy $SourceRuntime $WorkDir /E /XD spool data images logs /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $WorkDir 'spool'), (Join-Path $WorkDir 'data'), (Join-Path $WorkDir 'images'), (Join-Path $WorkDir 'logs') | Out-Null

$dataDir = Join-Path $WorkDir 'data'
$yesterday = (Get-Date).AddDays(-1).ToString('yyyyMMdd')

# ---------------------------------------------------------------- 造 10 万条历史（昨天的数据 → 走索引）
Write-Host ("`n=== 造 " + $Records + " 条历史数据（" + $yesterday + "）===") -ForegroundColor Cyan
$historyFile = Join-Path $dataDir ("parcels-" + $yesterday + ".jsonl")
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$writer = New-Object System.IO.StreamWriter($historyFile, $false, (New-Object System.Text.UTF8Encoding($false)))
try {
    $baseTime = [DateTimeOffset]::Now.ToUnixTimeMilliseconds() - 86400000
    for ($i = 0; $i -lt $Records; $i++) {
        $traceId = 'T' + $i.ToString('D7')
        $device = 'cam-' + (($i % 6) + 1)
        $noread = ($i % 25) -eq 0
        $code = ''
        if (!$noread) { $code = 'SF' + $i.ToString('D12') }
        $weight = 100 + ($i % 5000)
        $dispatch = 'pending'
        if (($i % 10) -eq 1) { $dispatch = 'sent' }
        elseif (($i % 10) -eq 2) { $dispatch = 'failed' }
        $image = ''
        if (($i % 3) -eq 0) { $image = 'D:\images\' + $traceId + '.bmp' }
        # JSON 里反斜杠必须转义（"D:\images\x.bmp" 里的 \i 是非法转义，整行会解析失败）
        $imageJson = $image.Replace('\', '\\')
        $captured = $baseTime + ($i * 10)
        # 注意：历史文件里的 codes 是"条码值数组"（字符串），类型/方位在 codeDetails 里。
        # 这里必须按真实格式造数据，否则平台解析不了（条码写成对象数组就是无效格式）。
        $codesJson = if ($noread) { '[]' } else { '["' + $code + '"]' }
        $detailsJson = if ($noread) { '[]' } else { '[{"value":"' + $code + '","kind":"1d","position":"top"}]' }
        $codeCount = if ($noread) { 0 } else { 1 }
        $imageCount = if ($image) { 1 } else { 0 }
        $line = '{"type":"parcel","time":"2026-09-16 10:00:00.000","data":{"traceId":"' + $traceId +
            '","deviceId":"' + $device + '","stage":"enriched","capturedAtMs":' + $captured +
            ',"time":"2026-09-16 10:00:00.000","codes":' + $codesJson +
            ',"codeDetails":' + $detailsJson + ',"filteredCodes":[],"codeCount":' + $codeCount +
            ',"weightGrams":' + $weight + ',"volumeMm3":0,"lengthMm":0,"widthMm":0,"heightMm":0' +
            ',"imageCount":' + $imageCount + ',"firstImagePath":' + $(if ($image) { '"' + $imageJson + '"' } else { 'null' }) +
            ',"updates":2,"staged":true,"complete":true,"dispatchState":"' + $dispatch +
            '","dispatchAttempts":0,"dispatchedAt":null,"dispatchError":null}}'
        $writer.WriteLine($line)
        # 每件再补一行"第一次回调"的快照，模拟真实的两次回调（索引应该把它收敛掉）
        $writer.WriteLine($line.Replace('"updates":2', '"updates":1').Replace('"stage":"enriched"', '"stage":"detected"'))
    }
}
finally {
    $writer.Dispose()
}
$sw.Stop()
$fileMb = [Math]::Round((Get-Item $historyFile).Length / 1MB, 1)
Write-Host ("快照文件：" + $fileMb + " MB（" + ($Records * 2) + " 行，模拟两次回调），写盘耗时 " + [Math]::Round($sw.Elapsed.TotalSeconds, 1) + " 秒") -ForegroundColor DarkGray

try {
    Start-Platform

    # ---------------------------------------------------------------- 1) 索引与查询
    Write-Host "`n=== 1) 索引收敛 + 全量查询 ===" -ForegroundColor Cyan
    $all = Get-Json -Path ('/api/history?from=' + $yesterday + '&to=' + $yesterday + '&limit=10')
    Add-Check '历史总数（每个包裹一条）' $Records $all.total
    Add-Check '查询走了索引（不是逐条快照）' 'True' $all.fromIndex
    Add-Check '返回条数（第一页）' 10 $all.returned
    Write-Host ("查询耗时：" + $all.elapsedMs + " ms（" + $Records + " 条）") -ForegroundColor Yellow
    Add-Check '10 万条查询在 2 秒内' 'True' ($all.elapsedMs -lt 2000)
    Add-Check '索引文件已生成' 'True' (Test-Path (Join-Path $dataDir ("parcels-" + $yesterday + ".index.jsonl")))

    # ---------------------------------------------------------------- 2) 各种过滤
    Write-Host "`n=== 2) 过滤条件 ===" -ForegroundColor Cyan
    $noreadExpected = [Math]::Ceiling($Records / 25)
    $noread = Get-Json -Path ('/api/history?from=' + $yesterday + '&to=' + $yesterday + '&noread=true&limit=1')
    Add-Check '无码包裹数' $noreadExpected $noread.total

    # 造数据时：i % 10 == 1 → sent，i % 10 == 2 → failed
    $sentExpected = [Math]::Floor($Records / 10)
    $sent = Get-Json -Path ('/api/history?from=' + $yesterday + '&to=' + $yesterday + '&dispatchState=sent&limit=1')
    Add-Check '已下发（sent）条数' $sentExpected $sent.total

    $dev = Get-Json -Path ('/api/history?from=' + $yesterday + '&to=' + $yesterday + '&deviceId=cam-3&limit=1')
    $devExpected = [Math]::Ceiling($Records / 6)
    Add-Check '按相机过滤（cam-3）' $devExpected $dev.total

    $code = Get-Json -Path ('/api/history?from=' + $yesterday + '&to=' + $yesterday + '&code=SF000000012345&limit=5')
    Add-Check '按条码精确过滤' 1 $code.total

    $img = Get-Json -Path ('/api/history?from=' + $yesterday + '&to=' + $yesterday + '&hasImage=true&limit=1')
    Add-Check '只看有图的' ([Math]::Ceiling($Records / 3)) $img.total

    # ---------------------------------------------------------------- 3) 分页
    Write-Host "`n=== 3) 分页 ===" -ForegroundColor Cyan
    $page1 = Get-Json -Path ('/api/history?from=' + $yesterday + '&to=' + $yesterday + '&limit=20&offset=0')
    $page2 = Get-Json -Path ('/api/history?from=' + $yesterday + '&to=' + $yesterday + '&limit=20&offset=20')
    Add-Check '第 1 页条数' 20 $page1.returned
    Add-Check '第 2 页条数' 20 $page2.returned
    Add-Check '两页不重复' 'True' ($page1.items[0].traceId -ne $page2.items[0].traceId)
    Add-Check '按时间倒序（第 1 页第一条是最新的）' 'True' ($page1.items[0].capturedAtMs -gt $page2.items[0].capturedAtMs)

    # ---------------------------------------------------------------- 4) 导出
    Write-Host "`n=== 4) 导出 CSV ===" -ForegroundColor Cyan
    $csv = (Invoke-WebRequest -Uri ($baseUrl + '/api/history/export?from=' + $yesterday + '&to=' + $yesterday + '&noread=true') -UseBasicParsing -TimeoutSec 120).Content
    $csvLines = @($csv -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
    Add-Check 'CSV 行数（表头 + 无码条数）' ($noreadExpected + 1) $csvLines.Count
    $header = $csvLines[0]
    foreach ($col in @('条码', '时间', '相机', '图片路径', '无码', '下发状态')) {
        Add-Check ("CSV 表头包含「" + $col + "」") 'True' ($header.Contains($col))
    }

    # ---------------------------------------------------------------- 5) 断电不丢
    Write-Host "`n=== 5) 断电不丢（硬杀进程后重启） ===" -ForegroundColor Cyan
    $spoolFile = Join-Path $WorkDir ("spool\events-" + (Get-Date).ToString('yyyyMMdd') + ".jsonl")
    $now = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add((New-ParcelEvent -EventId 501 -TraceId 'POWER-A' -Stage 'detected' -Device 'cam-9' -CapturedAtMs $now -Codes @('SF999000000001') -Weight -1 -ImagePath 'D:\images\POWER-A.bmp'))
    $lines.Add((New-ParcelEvent -EventId 502 -TraceId 'POWER-A' -Stage 'enriched' -Device 'cam-9' -CapturedAtMs $now -Codes @('SF999000000001') -Weight 1234 -ImagePath ''))
    [System.IO.File]::WriteAllLines($spoolFile, $lines, (New-Object System.Text.UTF8Encoding($false)))

    Start-Sleep -Seconds 3
    Stop-Process -Name 'DwsEdge.Platform' -Force -ErrorAction SilentlyContinue   # 硬杀：不走优雅退出
    Start-Sleep -Seconds 2
    Start-Platform

    $today = (Get-Date).ToString('yyyyMMdd')
    $power = Get-Json -Path ('/api/history?from=' + $today + '&to=' + $today + '&code=SF999000000001&limit=10')
    Add-Check '断电重启后仍能查到该包裹' 1 $power.total
    if ($power.total -ge 1) {
        Add-Check '  条码正确' 'SF999000000001' ($power.items[0].codes -join ',')
        Add-Check '  相机正确' 'cam-9' $power.items[0].deviceId
        Add-Check '  重量已合并（第二次回调）' 1234 $power.items[0].weightGrams
        Add-Check '  图片路径已记录' 'D:\images\POWER-A.bmp' $power.items[0].firstImagePath
        Add-Check '  下发状态默认 pending' 'pending' $power.items[0].dispatchState
    }
}
finally {
    Stop-Platform
}

# ---------------------------------------------------------------- 汇总
Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('B3 回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    exit 0
}

Write-Host ('B3 回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
