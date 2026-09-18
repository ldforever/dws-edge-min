<#
    C4 回归测试：统计看板（简版）—— 总包数 / 读码率 / 无码率，按相机与班次维度查看。

    验收点（需求原文）：统计值与数据库一致；维度切换后数据正确。

    脚本造 300 个包裹（3 台相机、含 60 个无码、跨白班/夜班、跨昨天/今天），然后：
      1) 核对总览：总数 300 / 有码 240 / 无码 60 / 读码率 80% / 无码率 20%；
      2) 按相机：cam-a 120、cam-b 100、cam-c 80，且每组读码率与"数出来的"一致；
      3) 按班次：白班/夜班分组，**各组之和必须等于总数**（不重不漏），跨天夜班的凌晨算前一天；
      4) 按小时 / 按日期维度也能出数据，且日期维度昨天 180、今天 120；
      5) 与库一致：直接数历史快照文件的行数，和看板的总数对上；
      6) 班次配置可读可改、非法值被拒；未登录改班次被 401 拦（配置类接口要鉴权）。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-c4-stats.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\test-c4-stats.ps1 -KeepRunning
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = '',
    [int]$Port = 8102,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-c4-test' }
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
    $log = Join-Path $WorkDir 'logs\c4-test-platform.log'
    Start-Process -FilePath $exe -ArgumentList @('--urls', $baseUrl) `
        -WorkingDirectory (Join-Path $WorkDir 'platform') -WindowStyle Hidden `
        -RedirectStandardOutput $log -RedirectStandardError ($log + '.err') | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        try { $null = Invoke-RestMethod -Uri ($baseUrl + '/api/health') -TimeoutSec 3; return } catch { }
    }
    throw "平台在 60 秒内没有就绪：$baseUrl"
}

function Call {
    param([string]$Path, [string]$Method = 'GET', $Body = $null, [string]$ApiKey = '', [switch]$Anonymous)
    $headers = @{}
    if ($ApiKey) { $headers['X-Api-Key'] = $ApiKey }
    $params = @{ Uri = $baseUrl + $Path; Method = $Method; UseBasicParsing = $true; TimeoutSec = 60 }
    # -Anonymous：显式传空 Headers，盖掉 b9-auth-helper 设的全局默认令牌（用来验证"未登录"）
    if ($headers.Count -gt 0) { $params.Headers = $headers }
    elseif ($Anonymous) { $params.Headers = @{} }
    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 6 -Compress)
        $params.ContentType = 'application/json; charset=utf-8'
    }
    try {
        $response = Invoke-WebRequest @params
        $parsed = $null
        try { if ($response.Content) { $parsed = $response.Content | ConvertFrom-Json } } catch { }
        return @{ status = [int]$response.StatusCode; body = $parsed; raw = $response.Content }
    }
    catch {
        $status = 0; $content = ''
        if ($_.Exception.Response) {
            $resp = $_.Exception.Response
            $status = [int]$resp.StatusCode
            try {
                if ($null -ne $resp.Content -and $resp.Content.ReadAsStringAsync) {
                    $content = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                }
            }
            catch { }
            if ([string]::IsNullOrEmpty($content)) {
                try { $content = (New-Object System.IO.StreamReader($resp.GetResponseStream())).ReadToEnd() } catch { }
            }
        }
        $parsed = $null
        try { if ($content) { $parsed = $content | ConvertFrom-Json } } catch { }
        return @{ status = $status; body = $parsed; raw = $content }
    }
}

function Get-Board {
    param([string]$Dimension, [string]$From = '', [string]$To = '', [string]$DeviceId = '')
    if ([string]::IsNullOrEmpty($From)) { $From = (Get-Date).AddDays(-1).ToString('yyyyMMdd') }
    if ([string]::IsNullOrEmpty($To)) { $To = (Get-Date).ToString('yyyyMMdd') }
    $path = '/api/stats/board?from=' + $From + '&to=' + $To + '&dimension=' + $Dimension
    if ($DeviceId) { $path += '&deviceId=' + $DeviceId }
    return (Call -Path $path).body
}

<# 平台正在往"今天"的快照文件里追加写，必须用 FileShare.ReadWrite 才读得到（默认独占会 IOException）#>
function Get-LineCount {
    param([string]$Path)
    if (!(Test-Path $Path)) { return 0 }
    $count = 0
    $stream = New-Object System.IO.FileStream($Path, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $reader = New-Object System.IO.StreamReader($stream)
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            if ($line.Trim().Length -gt 0) { $count++ }
        }
    }
    finally {
        $reader.Close()
        $stream.Close()
    }
    return $count
}

Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
Stop-Platform
if (Test-Path $WorkDir) {
    Move-Item -LiteralPath $WorkDir -Destination ("$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force
}
robocopy $SourceRuntime $WorkDir /E /XD spool data images logs cache /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $WorkDir 'spool'), (Join-Path $WorkDir 'data'), (Join-Path $WorkDir 'images'), (Join-Path $WorkDir 'logs') | Out-Null
[System.IO.File]::WriteAllText((Join-Path $WorkDir 'config\auth.json'),
    '{"enabled":true,"protectRead":false,"allowServiceKey":true,"serviceKey":"c4-test","serviceKeyRole":"admin","maxFailures":5,"lockMinutes":15,"failureWindowMinutes":10,"sessionMinutes":480}',
    $utf8NoBom)

# ---------------------------------------------------------------- 造 300 个包裹
# cam-a 120（白班 09-17 10:00 60 个 + 夜班 09-17 22:00 60 个）
# cam-b 100（白班 09-18 09:00 50 个 + 夜班 09-17 次日 02:00 50 个）
# cam-c  80（白班 09-17 10:30 40 个 + 夜班 09-17 23:30 40 个）
# 每台相机 20 个无码，共 60 无码 -> 读码率 80%
$today = (Get-Date).Date
$yesterday = $today.AddDays(-1)

function New-Plan {
    param([string]$Camera, [int]$Count, [DateTime]$At, [int]$Noread)
    return @{ camera = $Camera; count = $Count; at = $At; noread = $Noread }
}

$plans = @(
    (New-Plan -Camera 'cam-a' -Count 60 -At $yesterday.AddHours(10) -Noread 10),
    (New-Plan -Camera 'cam-a' -Count 60 -At $yesterday.AddHours(22) -Noread 10),
    (New-Plan -Camera 'cam-b' -Count 50 -At $today.AddHours(9) -Noread 10),
    (New-Plan -Camera 'cam-b' -Count 50 -At $today.AddHours(2) -Noread 10),
    (New-Plan -Camera 'cam-c' -Count 40 -At $yesterday.AddHours(10.5) -Noread 10),
    (New-Plan -Camera 'cam-c' -Count 40 -At $yesterday.AddHours(23.5) -Noread 10)
)

$lines = New-Object System.Collections.Generic.List[string]
$seq = 0
foreach ($plan in $plans) {
    for ($i = 0; $i -lt $plan.count; $i++) {
        $seq++
        $noread = $i -lt $plan.noread
        $atMs = [DateTimeOffset]::new($plan.at.AddSeconds($i * 7)).ToUnixTimeMilliseconds()
        # 注意：codes 必须在哈希表字面量里直接写数组。
        # 用 "$codes = if (...) { @() } else { @(...) }" 会被 PowerShell 展开
        # （空数组变 null、单元素变标量），写进 spool 就成了 {} 而不是 []，平台按数组反序列化会整条失败。
        if ($noread) {
            $event = [ordered]@{
                schemaVersion = 1; type = 'parcel'; eventId = $seq; providerId = 'test'; deviceId = $plan.camera
                stage = 'enriched'; capturedAtMs = $atMs; receivedAtMs = $atMs; traceId = ('C4-' + $seq.ToString('D5'))
                stagedResult = $false; weightGrams = 500; lengthMm = 300; widthMm = 200; heightMm = 150; volumeMm3 = 9000000
                codes = @(); images = @()
            }
        }
        else {
            $event = [ordered]@{
                schemaVersion = 1; type = 'parcel'; eventId = $seq; providerId = 'test'; deviceId = $plan.camera
                stage = 'enriched'; capturedAtMs = $atMs; receivedAtMs = $atMs; traceId = ('C4-' + $seq.ToString('D5'))
                stagedResult = $false; weightGrams = 500; lengthMm = 300; widthMm = 200; heightMm = 150; volumeMm3 = 9000000
                codes = @(@{ value = ('SF' + $seq.ToString('D12')); kind = '1d'; position = 'top' }); images = @()
            }
        }
        $lines.Add(($event | ConvertTo-Json -Compress -Depth 6))
    }
}

$spoolFile = Join-Path $WorkDir ('spool\events-' + (Get-Date).ToString('yyyyMMdd') + '.jsonl')
[System.IO.File]::WriteAllLines($spoolFile, $lines, $utf8NoBom)
Write-Host ("已写入 " + $lines.Count + " 个包裹（3 台相机 / 4 个时段 / 60 个无码）") -ForegroundColor DarkGray

try {
    Start-Platform
    . (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'b9-auth-helper.ps1')
    $null = Enable-TestAuth -WorkDir $WorkDir
    $key = (Get-Content -LiteralPath (Join-Path $WorkDir 'config\auth.json') -Raw -Encoding UTF8 | ConvertFrom-Json).serviceKey

    Start-Sleep -Seconds 8   # 等平台消费完 300 条（写历史快照）

    $from = $yesterday.ToString('yyyyMMdd')
    $to = $today.ToString('yyyyMMdd')

    # ---------------------------------------------------------------- 1) 总览
    Write-Host "`n=== 1) 总览：总包数 / 读码率 / 无码率 ===" -ForegroundColor Cyan
    $board = Get-Board -Dimension 'camera'
    $parseErrors = (Call -Path '/api/stats').body.parseErrors
    Add-Check '平台解析失败 0 条（spool 事件格式正确）' 0 $parseErrors
    Add-Check '总包数 300' 300 $board.totals.total
    Add-Check '有码 240' 240 $board.totals.read
    Add-Check '无码 60' 60 $board.totals.noread
    Add-Check '读码率 80%' 80 $board.totals.readRatePercent
    Add-Check '无码率 20%' 20 $board.totals.noreadRatePercent
    Add-Check '查询耗时 < 2 秒' 'True' ($board.elapsedMs -lt 2000)

    # ---------------------------------------------------------------- 2) 与"库"一致
    Write-Host "`n=== 2) 与历史库对账 ===" -ForegroundColor Cyan
    $history = (Call -Path ('/api/history?from=' + $from + '&to=' + $to + '&limit=1')).body
    Add-Check '历史查询总数与看板一致' 300 $history.total
    $snapshotCount = 0
    foreach ($day in @($yesterday.ToString('yyyyMMdd'), $today.ToString('yyyyMMdd'))) {
        $snapshotCount += (Get-LineCount -Path (Join-Path $WorkDir ('data\parcels-' + $day + '.jsonl')))
    }
    Add-Check '历史快照文件行数也是 300' 300 $snapshotCount
    # 事件时间跨天时，必须落到对应日期的快照文件里（否则按日期查询/统计会失真）
    Add-Check '昨天的事件写进昨天的快照文件' 200 (Get-LineCount -Path (Join-Path $WorkDir ('data\parcels-' + $yesterday.ToString('yyyyMMdd') + '.jsonl')))
    Add-Check '今天的事件写进今天的快照文件' 100 (Get-LineCount -Path (Join-Path $WorkDir ('data\parcels-' + $today.ToString('yyyyMMdd') + '.jsonl')))

    # ---------------------------------------------------------------- 3) 按相机
    Write-Host "`n=== 3) 按相机维度 ===" -ForegroundColor Cyan
    Add-Check '相机数 3' 3 $board.groupCount
    $byCam = @{}
    foreach ($g in $board.groups) { $byCam[$g.key] = $g }
    Add-Check 'cam-a 120 包' 120 $byCam['cam-a'].total
    Add-Check 'cam-b 100 包' 100 $byCam['cam-b'].total
    Add-Check 'cam-c 80 包' 80 $byCam['cam-c'].total
    Add-Check 'cam-a 无码 20' 20 $byCam['cam-a'].noread
    Add-Check 'cam-a 读码率 83.3%' 83.3 $byCam['cam-a'].readRatePercent
    Add-Check 'cam-b 读码率 80%' 80 $byCam['cam-b'].readRatePercent
    Add-Check 'cam-c 读码率 75%' 75 $byCam['cam-c'].readRatePercent
    Add-Check '相机按包数从多到少' 'cam-a' $board.groups[0].key
    Add-Check '三台相机之和 = 总数' 300 ($byCam['cam-a'].total + $byCam['cam-b'].total + $byCam['cam-c'].total)

    $single = Get-Board -Dimension 'camera' -DeviceId 'cam-b'
    Add-Check '相机过滤生效（只看 cam-b）' 100 $single.totals.total
    Add-Check '过滤后只有 1 组' 1 $single.groupCount

    # ---------------------------------------------------------------- 4) 按班次
    Write-Host "`n=== 4) 按班次维度（默认白班 08:00-20:00 / 夜班 20:00-08:00）===" -ForegroundColor Cyan
    $shiftBoard = Get-Board -Dimension 'shift'
    $shiftSum = 0
    $shiftNames = New-Object System.Collections.Generic.List[string]
    foreach ($g in $shiftBoard.groups) { $shiftSum += $g.total; $shiftNames.Add($g.key) }
    Add-Check '各班组之和 = 总数（不重不漏）' 300 $shiftSum
    Add-Check '没有包裹落在班次之外' 0 $shiftBoard.unmatchedShifts
    Add-Check '白班@昨天 100 包（cam-a 60 + cam-c 40）' 100 ($shiftBoard.groups | Where-Object { $_.key -eq ('白班@' + $yesterday.ToString('yyyy-MM-dd')) }).total
    Add-Check '夜班@昨天 150 包（含今天凌晨 02:00 的 50 包）' 150 ($shiftBoard.groups | Where-Object { $_.key -eq ('夜班@' + $yesterday.ToString('yyyy-MM-dd')) }).total
    Add-Check '白班@今天 50 包（cam-b 09:00）' 50 ($shiftBoard.groups | Where-Object { $_.key -eq ('白班@' + $today.ToString('yyyy-MM-dd')) }).total
    Add-Check '班次组数 3（今天夜班还没开始）' 3 $shiftBoard.groupCount

    # ---------------------------------------------------------------- 5) 其它维度
    Write-Host "`n=== 5) 按小时 / 按日期维度 ===" -ForegroundColor Cyan
    $hourBoard = Get-Board -Dimension 'hour'
    # 昨天 10:00 与 10:30 属同一个小时 -> 合并成一组，所以是 5 组（10/22/23 点 + 今天 02/09 点）
    Add-Check '按小时出 5 组' 5 $hourBoard.groupCount
    Add-Check '按小时之和 = 总数' 300 (($hourBoard.groups | Measure-Object -Property total -Sum).Sum)
    $hour10 = $hourBoard.groups | Where-Object { $_.key -eq ($yesterday.ToString('yyyy-MM-dd') + ' 10') }
    Add-Check '昨天 10 点那组合并了 100 包（cam-a 60 + cam-c 40）' 100 $hour10.total

    $dayBoard = Get-Board -Dimension 'day'
    Add-Check '按日期 2 组' 2 $dayBoard.groupCount
    # 今天凌晨 02:00 那批：日期维度算"今天"，班次维度算"昨天夜班" —— 两个维度不一样，正是跨天班次的口径
    Add-Check '昨天 200 包（含 22:00/23:30）' 200 ($dayBoard.groups | Where-Object { $_.key -eq $yesterday.ToString('yyyy-MM-dd') }).total
    Add-Check '今天 100 包（含凌晨 02:00 + 上午 09:00）' 100 ($dayBoard.groups | Where-Object { $_.key -eq $today.ToString('yyyy-MM-dd') }).total

    $badDim = Call -Path ('/api/stats/board?from=' + $from + '&to=' + $to + '&dimension=whatever')
    Add-Check '未知维度不报错（落到未知维度组）' 200 $badDim.status

    # ---------------------------------------------------------------- 6) 班次配置
    Write-Host "`n=== 6) 班次配置（可读可改 / 非法被拒 / 写要鉴权）===" -ForegroundColor Cyan
    $shiftsNow = Call -Path '/api/stats/shifts'
    Add-Check '读班次 200' 200 $shiftsNow.status
    Add-Check '默认两个班次' 2 @($shiftsNow.body.shifts).Count
    Add-Check '默认白班 08:00-20:00' 'True' ((@($shiftsNow.body.shifts)[0].name -eq '白班') -and (@($shiftsNow.body.shifts)[0].start -eq '08:00'))
    Add-Check '带时长说明' 'True' (@($shiftsNow.body.shifts)[1].span -match '跨天')
    Add-Check '班次配置文件路径' 'True' ($shiftsNow.body.file -like '*shifts.json')

    $noAuth = Call -Path '/api/stats/shifts' -Method POST -Anonymous -Body @{ shifts = @(@{ name = '偷改'; start = '00:00'; end = '08:00' }) }
    Add-Check '未登录改班次被 401 拦' 401 $noAuth.status

    $badShift = Call -Path '/api/stats/shifts' -Method POST -ApiKey $key -Body @{ shifts = @(@{ name = '早班'; start = '8点'; end = '14:00' }) }
    Add-Check '时间格式不对被拒' 400 $badShift.status
    Add-Check '提示要写 HH:mm' 'True' ($badShift.body.error -match 'HH:mm')

    $dupShift = Call -Path '/api/stats/shifts' -Method POST -ApiKey $key -Body @{ shifts = @(
        @{ name = '早班'; start = '06:00'; end = '14:00' },
        @{ name = '早班'; start = '14:00'; end = '22:00' }) }
    Add-Check '班次重名被拒' 400 $dupShift.status

    $good = Call -Path '/api/stats/shifts' -Method POST -ApiKey $key -Body @{ shifts = @(
        @{ name = '早班'; start = '06:00'; end = '14:00' },
        @{ name = '中班'; start = '14:00'; end = '22:00' },
        @{ name = '夜班'; start = '22:00'; end = '06:00' }) }
    Add-Check '保存自定义班次 200' 200 $good.status
    Add-Check '自动备份' 'True' (($good.body.backup -ne $null) -and (Test-Path $good.body.backup))

    $shiftBoard2 = Get-Board -Dimension 'shift'
    $sum2 = ($shiftBoard2.groups | Measure-Object -Property total -Sum).Sum
    Add-Check '改完班次后总数不变（还是 300）' 300 $sum2
    Add-Check '新的班次分组生效（出现了夜班/早班，白班没了）' 'True' (
        (@($shiftBoard2.groups | Where-Object { $_.key -like '夜班@*' }).Count -ge 1) -and
        (@($shiftBoard2.groups | Where-Object { $_.key -like '白班@*' }).Count -eq 0))
    Add-Check '新班次下凌晨那批仍归前一天夜班' 150 ($shiftBoard2.groups | Where-Object { $_.key -eq ('夜班@' + $yesterday.ToString('yyyy-MM-dd')) }).total

    # ---------------------------------------------------------------- 7) 前端产物
    Write-Host "`n=== 7) 部署产物里带统计看板 ===" -ForegroundColor Cyan
    $html = [System.IO.File]::ReadAllText((Join-Path $WorkDir 'platform\wwwroot\index.html'))
    Add-Check '有统计页签' 'True' ($html -match 'id="tab-stats"')
    Add-Check '有统计页容器' 'True' ($html -match 'id="page-stats"')
    Add-Check '有 KPI 与表格' 'True' (($html -match 'id="kpiReadRate"') -and ($html -match 'id="statsRows"'))
    Add-Check '有班次设置表' 'True' ($html -match 'id="shiftRows"')

    $statsJs = [System.IO.File]::ReadAllText((Join-Path $WorkDir 'platform\wwwroot\js\stats.js'))
    $apiJs = [System.IO.File]::ReadAllText((Join-Path $WorkDir 'platform\wwwroot\js\api.js'))
    Add-Check 'api.js 有看板接口' 'True' ($apiJs -match 'api/stats/board')
    Add-Check 'api.js 有班次接口' 'True' ($apiJs -match 'api/stats/shifts')
    Add-Check 'stats.js 调看板' 'True' ($statsJs -match 'statsBoard')
    Add-Check 'stats.js 支持导出统计 CSV' 'True' ($statsJs -match 'downloadText')
    Add-Check 'stats.js 有四个维度' 'True' (($statsJs -match 'camera') -and ($statsJs -match 'shift') -and ($statsJs -match 'hour') -and ($statsJs -match 'day'))
}
finally {
    if (!$KeepRunning) { Stop-Platform }
}

Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('C4 回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    if ($KeepRunning) { Write-Host ('平台还在跑：' + $baseUrl + '　打开"统计"页看 C4') -ForegroundColor Yellow }
    exit 0
}
Write-Host ('C4 回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
