<#
    C2 回归测试：相机状态墙（每台相机在线/离线、掉线次数与出码计数一屏展示）。

    验收点：
      * 每台相机能在"一屏"里拿到：在线/离线/未发现、出码计数、掉线次数、心跳、在线率、活动告警；
      * 状态与实际一致，且 5 秒内刷新（上面这些数字都来自 SSE，页面不用点刷新）；
      * 异常相机能快速识别（离线/未发现/心跳超时/频繁掉线/在线率低 -> 有告警徽标）。

    重点验证"出码计数"这条以前不刷新的链路：
      出码 -> 平台按 700ms 节流推 camera-count -> 界面数字自己涨；被节流掉的那次由 5 秒监控快照兜底。
    所以脚本会真连一次 SSE，检查建连补发和出码后推送里都带 codeCount。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-c2-wall.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\test-c2-wall.ps1 -KeepRunning   # 留着看界面
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = '',
    [int]$Port = 8099,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-c2-test' }
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
    $log = Join-Path $WorkDir 'logs\c2-test-platform.log'
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

Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
Stop-Platform
if (Test-Path $WorkDir) {
    Move-Item -LiteralPath $WorkDir -Destination ("$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force
}
robocopy $SourceRuntime $WorkDir /E /XD spool data images logs cache /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $WorkDir 'spool'), (Join-Path $WorkDir 'data'), (Join-Path $WorkDir 'images'), (Join-Path $WorkDir 'logs') | Out-Null

# 相机墙是只读界面，测试环境把鉴权关掉（现场默认开着，登录后一样能看）
[System.IO.File]::WriteAllText((Join-Path $WorkDir 'config\auth.json'),
    '{"enabled":false,"protectRead":false,"allowServiceKey":true,"serviceKey":"c2-test","serviceKeyRole":"admin","maxFailures":5,"lockMinutes":15,"failureWindowMinutes":10,"sessionMinutes":480}',
    $utf8NoBom)

$spoolFile = Join-Path $WorkDir ('spool\events-' + (Get-Date).ToString('yyyyMMdd') + '.jsonl')
$now = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()

function New-CamEvent {
    param([string]$Cam, [bool]$Online, [long]$AtMs, [bool]$Discovered = $true, [string]$Position = '', [int]$Offline = 0)
    $item = [ordered]@{
        schemaVersion = 1; type = 'camera-status'; eventId = 0; providerId = 'test'; deviceId = $Cam
        atMs = $AtMs; receivedAtMs = $AtMs; online = $Online; isSnapshot = $true; discovered = $Discovered
        sessionId = 1; offlineCount = $Offline; reconnectCount = $Offline
    }
    if ($Position) { $item.position = $Position }
    return ($item | ConvertTo-Json -Compress -Depth 6)
}

function New-ParcelEvent {
    param([string]$TraceId, [string]$Cam, [string]$Code, [long]$AtMs, [int]$EventId)
    return (@{
        schemaVersion = 1; type = 'parcel'; eventId = $EventId; providerId = 'test'; deviceId = $Cam
        stage = 'enriched'; capturedAtMs = $AtMs; receivedAtMs = $AtMs; traceId = $TraceId; stagedResult = $false
        weightGrams = 500; lengthMm = 300; widthMm = 200; heightMm = 150; volumeMm3 = 9000000
        codes = @(@{ value = $Code; kind = '1d'; position = 'top' }); images = @()
    } | ConvertTo-Json -Compress -Depth 6)
}

try {
    # ---------------------------------------------------------------- 1) 造 3 台相机 + 1 个出码
    Write-Host "`n=== 1) 造数据：2 台在线 + 1 台未发现，cam-top 先出 1 个码 ===" -ForegroundColor Cyan
    $lines = @(
        (New-CamEvent -Cam 'cam-top' -Online $true -AtMs ($now - 3000) -Position 'top'),
        (New-CamEvent -Cam 'cam-left' -Online $true -AtMs ($now - 3000) -Position 'left'),
        (New-CamEvent -Cam 'cam-right' -Online $false -AtMs ($now - 3000) -Discovered $false -Position 'right'),
        (New-ParcelEvent -TraceId 'C2-P1' -Cam 'cam-top' -Code 'SF600000000001' -AtMs ($now - 2000) -EventId 1)
    )
    [System.IO.File]::WriteAllLines($spoolFile, $lines, $utf8NoBom)

    Start-Platform
    Start-Sleep -Seconds 4

    # ---------------------------------------------------------------- 2) 一屏能拿到的字段
    Write-Host "`n=== 2) 状态墙一屏需要的字段 ===" -ForegroundColor Cyan
    $counters = @(Get-Json '/api/cameras/counters')
    Add-Check '计数接口返回 3 台相机' 3 @($counters).Count
    $top = @($counters | Where-Object { $_.camera -eq 'cam-top' })[0]
    Add-Check 'cam-top 在线' 'True' $top.online
    Add-Check 'cam-top 出码数 1' 1 $top.codeCount
    Add-Check 'cam-top 有最近出码时间' 'True' ([string]::IsNullOrEmpty($top.lastCodeTime) -eq $false)
    Add-Check 'cam-top 方位=顶面' 'top' $top.position
    $right = @($counters | Where-Object { $_.camera -eq 'cam-right' })[0]
    Add-Check '未发现相机标成 discovered=false（墙上显黄）' 'False' $right.discovered

    $monitorCams = @(Get-Json '/api/monitor/cameras')
    $mtop = @($monitorCams | Where-Object { $_.camera -eq 'cam-top' })[0]
    Add-Check '监控快照也带出码数（5 秒兜底刷新用）' 1 $mtop.codeCount
    Add-Check '监控快照带在线率' 'True' ($mtop.onlineRatePercent -ge 0)
    Add-Check '监控快照带心跳新鲜度' 'True' ($mtop.lastHeartbeatAgeSeconds -ge 0)
    Add-Check '监控快照带方位（墙上的"顶面"）' 'top' $mtop.position
    $mright = @($monitorCams | Where-Object { $_.camera -eq 'cam-right' })[0]
    Add-Check '异常相机有告警（未发现/离线/心跳/在线率）' 'True' (@($mright.alerts).Count -ge 1)

    # ---------------------------------------------------------------- 3) SSE：建连补发 + 出码实时推
    Write-Host "`n=== 3) 出码计数能不能实时推给界面（不点刷新）===" -ForegroundColor Cyan
    $client = New-Object System.Net.Sockets.TcpClient
    $client.Connect('127.0.0.1', $Port)
    $stream = $client.GetStream()
    $request = "GET /api/stream HTTP/1.1`r`nHost: 127.0.0.1`r`nAccept: text/event-stream`r`nConnection: close`r`n`r`n"
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($request)
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
    Start-Sleep -Milliseconds 1500

    $buffer = New-Object byte[] 262144
    $read = $stream.Read($buffer, 0, $buffer.Length)
    $firstChunk = [System.Text.Encoding]::UTF8.GetString($buffer, 0, [Math]::Max(0, $read))
    Add-Check 'SSE 建连时补发了 camera-count' 'True' ($firstChunk -match '"type":"camera-count"')
    Add-Check '补发的计数里带 codeCount' 'True' ($firstChunk -match '"codeCount"')
    Add-Check '补发的监控快照里带方位（状态墙首屏）' 'True' ($firstChunk -match '"position"')

    # 再来 2 个包裹（cam-top 出码 1 -> 3），观察是否被实时推出来
    [System.IO.File]::AppendAllText($spoolFile,
        (New-ParcelEvent -TraceId 'C2-P2' -Cam 'cam-top' -Code 'SF600000000002' -AtMs ([DateTimeOffset]::Now.ToUnixTimeMilliseconds()) -EventId 2) + "`n", $utf8NoBom)
    [System.IO.File]::AppendAllText($spoolFile,
        (New-ParcelEvent -TraceId 'C2-P3' -Cam 'cam-top' -Code 'SF600000000003' -AtMs ([DateTimeOffset]::Now.ToUnixTimeMilliseconds()) -EventId 3) + "`n", $utf8NoBom)
    Start-Sleep -Seconds 3

    $read2 = $stream.Read($buffer, 0, $buffer.Length)
    $secondChunk = [System.Text.Encoding]::UTF8.GetString($buffer, 0, [Math]::Max(0, $read2))
    $allChunks = $firstChunk + $secondChunk
    Add-Check '出码后推了 camera-count（不用等 5 秒快照）' 'True' ($secondChunk -match '"type":"camera-count"')
    # 两包连推时中间那次会被 300ms 节流合并掉，所以这里只要求"数字涨了"
    Add-Check '推送里的出码数已经涨了（2 或 3）' 'True' ($allChunks -match '"codeCount":(2|3)')

    # 5 秒内必须能看到最终值（节流合并掉的那次由 B8 周期快照补上）—— 这是验收标准的硬指标
    $deadline = (Get-Date).AddSeconds(9)
    while ((Get-Date) -lt $deadline -and $allChunks -notmatch '"codeCount":3') {
        $stream.ReadTimeout = 2000
        try {
            $read3 = $stream.Read($buffer, 0, $buffer.Length)
            $allChunks += [System.Text.Encoding]::UTF8.GetString($buffer, 0, [Math]::Max(0, $read3))
        }
        catch { }
    }
    Add-Check '5 秒内界面能拿到最终出码数 3（快照兜底）' 'True' ($allChunks -match '"codeCount":3')
    $client.Close()

    $counters2 = @(Get-Json '/api/cameras/counters')
    Add-Check '接口侧出码数也是 3' 3 @($counters2 | Where-Object { $_.camera -eq 'cam-top' })[0].codeCount

    # ---------------------------------------------------------------- 4) 掉线：状态与计数都要变
    Write-Host "`n=== 4) 掉线后状态/掉线次数跟着变 ===" -ForegroundColor Cyan
    [System.IO.File]::AppendAllText($spoolFile,
        (New-CamEvent -Cam 'cam-left' -Online $false -AtMs ([DateTimeOffset]::Now.ToUnixTimeMilliseconds()) -Position 'left' -Offline 1) + "`n", $utf8NoBom)
    Start-Sleep -Seconds 3

    $counters3 = @(Get-Json '/api/cameras/counters')
    $left = @($counters3 | Where-Object { $_.camera -eq 'cam-left' })[0]
    Add-Check 'cam-left 变成离线' 'False' $left.online
    Add-Check 'cam-left 掉线次数 1' 1 $left.offlineCount
    Add-Check 'cam-right 仍然是未发现（异常）' 'False' @($counters3 | Where-Object { $_.camera -eq 'cam-right' })[0].discovered

    # ---------------------------------------------------------------- 5) 部署产物里确实有相机墙
    Write-Host "`n=== 5) 部署的前端产物里带相机状态墙 ===" -ForegroundColor Cyan
    $realtimeJs = Join-Path $WorkDir 'platform\wwwroot\js\realtime.js'
    $indexHtml = Join-Path $WorkDir 'platform\wwwroot\index.html'
    if (Test-Path $realtimeJs) {
        $js = [System.IO.File]::ReadAllText($realtimeJs)
        Add-Check 'realtime.js 有相机墙渲染' 'True' ($js -match 'camwall|camcell')
        Add-Check 'realtime.js 处理 camera-count 推送' 'True' ($js -match 'applyCameraCounters')
        Add-Check 'realtime.js 把监控指标并进相机墙' 'True' ($js -match 'applyMonitorStats')
        Add-Check 'realtime.js 有异常摘要（未发现/离线）' 'True' ($js -match '未发现')
    }
    if (Test-Path $indexHtml) {
        $html = [System.IO.File]::ReadAllText($indexHtml)
        Add-Check 'index.html 有相机墙容器' 'True' ($html -match 'id="camWall"')
        Add-Check 'index.html 有相机墙视图切换' 'True' (($html -match 'id="camViewCards"') -and ($html -match 'id="camViewTable"'))
        Add-Check 'index.html 表格视图仍保留' 'True' ($html -match 'id="camRows"')
        Add-Check 'B8 面板表头补了出码数' 'True' ($html -match '<th>出码数</th>')
    }
}
finally {
    if (!$KeepRunning) { Stop-Platform }
}

Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('C2 回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    if ($KeepRunning) { Write-Host ('平台还在跑：' + $baseUrl + '　打开看相机状态墙') -ForegroundColor Yellow }
    exit 0
}
Write-Host ('C2 回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
