<#
    B8 回归测试：相机状态监控与告警。

    验收点：
      * 在线率、掉线记录、心跳 —— 能查、数字对得上；
      * 四类告警：离线超时 / 心跳超时 / 频繁掉线 / 在线率过低，外加"清单里声明了但没发现"；
      * 告警恢复后自动消除，本次离线时长记在事件里；
      * 事件落盘，平台重启后掉线记录还在（且不会一启动就误报离线）；
      * 阈值配置可读 / 可改 / 非法值被拒 / 改完立即生效，旧配置自动备份；
      * 监控不影响原有链路：包裹照常入库、设备页照常显示。

    脚本做的事：往 spool 里写"相机状态事件 + 包裹事件"，模拟 5 台相机的正常/掉线/沉默/频繁闪断/在线率过低，
    然后只通过 HTTP 接口验收（也就是界面能看到的那几个接口）。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-b8-monitor.ps1
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = '',
    [int]$Port = 8096
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-b8-test' }

# 强制转成绝对路径：Stop-Platform 是按"可执行文件路径以运行目录开头"来认进程的。
# 传相对路径（比如 -WorkDir .\work\xxx）会匹配不上 —— 旧实例关不掉，新实例又抢不到端口，
# 最后脚本会卡在一个"以为平台起来了、其实连的是上一个实例"的状态里。
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
    $log = Join-Path $WorkDir 'logs\b8-test-platform.log'
    Start-Process -FilePath $exe -ArgumentList @('--urls', $baseUrl) `
        -WorkingDirectory (Join-Path $WorkDir 'platform') -WindowStyle Hidden `
        -RedirectStandardOutput $log -RedirectStandardError ($log + '.err') | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        try { $null = Invoke-RestMethod -Uri ($baseUrl + '/api/health') -TimeoutSec 3; return } catch { }
    }
    throw "平台在 60 秒内没有就绪：$baseUrl"
}

# 顶层数组用 Invoke-RestMethod 解析不可靠，统一走 Invoke-WebRequest + ConvertFrom-Json
function Get-Json {
    param([string]$Path)
    $raw = (Invoke-WebRequest -Uri ($baseUrl + $Path) -UseBasicParsing -TimeoutSec 60).Content
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    return ($raw | ConvertFrom-Json)
}

function Post-Json {
    param([string]$Path, $Body, [switch]$ExpectError)
    $payload = $Body | ConvertTo-Json -Depth 6
    try {
        $response = Invoke-WebRequest -Uri ($baseUrl + $Path) -Method Post -Body $payload `
            -ContentType 'application/json' -UseBasicParsing -TimeoutSec 60
        if ($ExpectError) { return @{ ok = $false; status = [int]$response.StatusCode; body = $response.Content } }
        return @{ ok = $true; status = [int]$response.StatusCode; body = ($response.Content | ConvertFrom-Json) }
    }
    catch {
        $status = 0
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        return @{ ok = $false; status = $status; body = $null }
    }
}

function New-CamEvent {
    param(
        [string]$Camera, [bool]$Online, [long]$AtMs,
        [bool]$Snapshot = $false, [bool]$Discovered = $true,
        [string]$Position = '', [string]$DeclaredKind = '', [string]$DeclaredValue = ''
    )
    $e = [ordered]@{
        schemaVersion = 1; type = 'camera-status'; eventId = 0; providerId = 'test'; deviceId = $Camera
        atMs = $AtMs; receivedAtMs = $AtMs; online = $Online; isSnapshot = $Snapshot
        discovered = $Discovered; sessionId = 1; offlineCount = 0; reconnectCount = 0
    }
    if ($Position) { $e.position = $Position }
    if ($DeclaredKind) { $e.declaredKind = $DeclaredKind; $e.declaredValue = $DeclaredValue }
    return ($e | ConvertTo-Json -Compress -Depth 6)
}

function New-ParcelEvent {
    param([string]$TraceId, [string]$Camera, [string]$Code, [long]$AtMs, [int]$EventId = 1)
    $e = [ordered]@{
        schemaVersion = 1; type = 'parcel'; eventId = $EventId; providerId = 'test'; deviceId = $Camera
        stage = 'enriched'; capturedAtMs = $AtMs; receivedAtMs = $AtMs; traceId = $TraceId
        stagedResult = $false; weightGrams = 520
        lengthMm = 300; widthMm = 200; heightMm = 150; volumeMm3 = 9000000
        codes = @(@{ value = $Code; kind = '1d'; position = 'top' })
    }
    return ($e | ConvertTo-Json -Compress -Depth 6)
}

function Append-Lines {
    param([string]$Path, [string[]]$Lines)
    $text = ''
    foreach ($line in $Lines) { $text += $line + "`n" }
    [System.IO.File]::AppendAllText($Path, $text, $utf8NoBom)
}

Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
Stop-Platform
if (Test-Path $WorkDir) {
    Move-Item -LiteralPath $WorkDir -Destination ("$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force
}
robocopy $SourceRuntime $WorkDir /E /XD spool data images logs cache /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $WorkDir 'spool'), (Join-Path $WorkDir 'data'), (Join-Path $WorkDir 'images'), (Join-Path $WorkDir 'logs') | Out-Null

# 阈值调小，让"心跳超时 / 离线告警"能在几十秒内验证出来（现场用默认值：60s / 10s / 5s 一次检查）
$monitorConfigPath = Join-Path $WorkDir 'config\monitor.json'
$monitorConfig = [ordered]@{
    enabled = $true
    heartbeatTimeoutSeconds = 5
    offlineAlertSeconds = 2
    frequentOfflineCount = 3
    frequentOfflineWindowMinutes = 30
    onlineRateAlertPercent = 95
    onlineRateWindowMinutes = 60
    checkIntervalSeconds = 1
    eventRetentionDays = 30
} | ConvertTo-Json
[System.IO.File]::WriteAllText($monitorConfigPath, $monitorConfig, $utf8NoBom)

$spoolFile = Join-Path $WorkDir ('spool\events-' + (Get-Date).ToString('yyyyMMdd') + '.jsonl')

try {
    Start-Platform
    . (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'b9-auth-helper.ps1')
    $null = Enable-TestAuth -WorkDir $WorkDir   # B9：管理接口要凭据，脚本用服务令牌

    # ---------------------------------------------------------------- 1) 造场景
    Write-Host "`n=== 1) 造场景：5 台相机（正常 / 未发现 / 沉默 / 频繁闪断 / 在线率过低）===" -ForegroundColor Cyan
    $now = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()

    $lines = @()
    # cam-top：在线，随后有包裹出码（心跳最强）
    $lines += New-CamEvent -Camera 'cam-top' -Online $true -AtMs ($now - 3000) -Snapshot $true -Position 'top' -DeclaredKind 'ip' -DeclaredValue '172.20.10.11'
    # cam-missing：清单里声明了，但 SDK 没发现（没上电 / 没接网 / 被占用）
    $lines += New-CamEvent -Camera 'cam-missing' -Online $false -AtMs ($now - 3000) -Snapshot $true -Discovered $false -Position 'bottom' -DeclaredKind 'ip' -DeclaredValue '172.20.10.12'
    # cam-silent：只报过一次在线，之后再没有任何数据 -> 靠"心跳超时"兜住
    $lines += New-CamEvent -Camera 'cam-silent' -Online $true -AtMs ($now - 1000) -Snapshot $true -Position 'left' -DeclaredKind 'ip' -DeclaredValue '172.20.10.13'
    # cam-flap：30 分钟内掉线 3 次 -> 频繁掉线
    $lines += New-CamEvent -Camera 'cam-flap' -Online $true  -AtMs ($now - 600000) -Position 'right' -DeclaredKind 'ip' -DeclaredValue '172.20.10.14'
    $lines += New-CamEvent -Camera 'cam-flap' -Online $false -AtMs ($now - 500000) -Position 'right'
    $lines += New-CamEvent -Camera 'cam-flap' -Online $true  -AtMs ($now - 400000) -Position 'right'
    $lines += New-CamEvent -Camera 'cam-flap' -Online $false -AtMs ($now - 300000) -Position 'right'
    $lines += New-CamEvent -Camera 'cam-flap' -Online $true  -AtMs ($now - 200000) -Position 'right'
    $lines += New-CamEvent -Camera 'cam-flap' -Online $false -AtMs ($now - 100000) -Position 'right'
    # cam-rate：过去 10 分钟里只在线 1 秒 -> 在线率约 0.2%
    $lines += New-CamEvent -Camera 'cam-rate' -Online $true  -AtMs ($now - 600000) -Position 'front' -DeclaredKind 'ip' -DeclaredValue '172.20.10.15'
    $lines += New-CamEvent -Camera 'cam-rate' -Online $false -AtMs ($now - 599000) -Position 'front'
    # 两个包裹（cam-top 出码 = 心跳）
    $lines += New-ParcelEvent -TraceId 'B8-P1' -Camera 'cam-top' -Code 'SF800000000001' -AtMs ($now - 1500) -EventId 1
    $lines += New-ParcelEvent -TraceId 'B8-P2' -Camera 'cam-top' -Code 'SF800000000002' -AtMs ($now - 1000) -EventId 2

    Append-Lines -Path $spoolFile -Lines $lines
    Write-Host ("已写入 " + $lines.Count + " 条事件，等平台消费 + 检查器判定（7 秒）") -ForegroundColor DarkGray
    Start-Sleep -Seconds 7

    # ---------------------------------------------------------------- 2) 汇总与在线率
    Write-Host "`n=== 2) 监控汇总与在线率 ===" -ForegroundColor Cyan
    $summary = Get-Json '/api/monitor/summary'
    Add-Check '监控到 5 台相机' 5 $summary.cameras
    Add-Check '在线 2 台' 2 $summary.online
    Add-Check '离线 3 台' 3 $summary.offline
    Add-Check '监控已启用' 'True' $summary.enabled
    Add-Check '平均在线率有值（不被平均成 100）' 'True' ($summary.averageOnlineRatePercent -ge 0 -and $summary.averageOnlineRatePercent -lt 95)
    Add-Check '有活动告警' 'True' ($summary.activeAlerts -ge 4)
    Add-Check '配置文件是 monitor.json' 'True' ($summary.configFile -like '*monitor.json')

    $cameras = Get-Json '/api/monitor/cameras'
    $top = @($cameras | Where-Object { $_.camera -eq 'cam-top' })[0]
    Add-Check '每台相机有在线率' 'True' ($top.onlineRatePercent -ge 99)
    Add-Check '每台相机有在线时长' 'True' ($top.onlineSeconds -ge 1)
    Add-Check '每台相机有心跳时间' 'True' ([string]::IsNullOrEmpty($top.lastHeartbeatAt) -eq $false)
    Add-Check '心跳是新鲜的（<=15 秒）' 'True' ($top.lastHeartbeatAgeSeconds -ge 0 -and $top.lastHeartbeatAgeSeconds -le 15)
    Add-Check '出码被记成心跳（最近出码有值）' 'True' ([string]::IsNullOrEmpty($top.lastCodeAt) -eq $false)
    Add-Check '正常相机没有掉线记录' 0 $top.offlineCount

    $flap = @($cameras | Where-Object { $_.camera -eq 'cam-flap' })[0]
    Add-Check '频繁闪断相机掉线次数 3' 3 $flap.offlineCount
    # 3 次掉线各 100 秒、在线也是 300 秒 -> 在线率约 50%
    Add-Check '频繁闪断相机在线率约 50%' 'True' ($flap.onlineRatePercent -ge 40 -and $flap.onlineRatePercent -le 60)

    $rate = @($cameras | Where-Object { $_.camera -eq 'cam-rate' })[0]
    Add-Check '低在线率相机在线率约 0.2%' 'True' ($rate.onlineRatePercent -le 1)

    # ---------------------------------------------------------------- 3) 四类告警
    Write-Host "`n=== 3) 告警判定（离线 / 心跳 / 频繁掉线 / 在线率 / 未发现）===" -ForegroundColor Cyan
    $active = Get-Json '/api/monitor/alerts?limit=200&activeOnly=true'
    $items = @($active.items)
    Add-Check '活动告警条数 >= 5' 'True' ($items.Count -ge 5)
    Add-Check '相机离线告警' 'True' (@($items | Where-Object { $_.code -eq 'camera-offline' }).Count -ge 1)
    Add-Check '心跳超时告警' 'True' (@($items | Where-Object { $_.code -eq 'heartbeat-timeout' -and $_.camera -eq 'cam-silent' }).Count -ge 1)
    Add-Check '频繁掉线告警' 'True' (@($items | Where-Object { $_.code -eq 'frequent-offline' -and $_.camera -eq 'cam-flap' }).Count -ge 1)
    Add-Check '在线率过低告警' 'True' (@($items | Where-Object { $_.code -eq 'low-online-rate' -and $_.camera -eq 'cam-rate' }).Count -ge 1)
    Add-Check '清单里声明但未发现（critical）' 'True' (@($items | Where-Object { $_.code -eq 'declared-missing' -and $_.camera -eq 'cam-missing' -and $_.severity -eq 'critical' }).Count -ge 1)
    Add-Check '告警带回阈值说明与时间' 'True' ([string]::IsNullOrEmpty($items[0].message) -eq $false -and [string]::IsNullOrEmpty($items[0].raisedAt) -eq $false)

    # ---------------------------------------------------------------- 4) 掉线记录
    Write-Host "`n=== 4) 掉线记录（可查 + 按相机过滤 + 落盘）===" -ForegroundColor Cyan
    $events = Get-Json '/api/monitor/events?limit=300'
    Add-Check '有掉线记录（>=5 条）' 'True' (@($events | Where-Object { $_.event -eq 'offline' }).Count -ge 5)
    Add-Check '有上线记录' 'True' (@($events | Where-Object { $_.event -eq 'online' }).Count -ge 1)
    Add-Check '告警产生也记进事件流' 'True' (@($events | Where-Object { $_.event -eq 'alert-raised' }).Count -ge 5)

    $flapEvents = Get-Json '/api/monitor/events?limit=100&camera=cam-flap'
    Add-Check '按相机过滤只返回该相机' 'True' (@($flapEvents | Where-Object { $_.camera -ne 'cam-flap' }).Count -eq 0)
    Add-Check 'cam-flap 记录 >= 6 条' 'True' (@($flapEvents).Count -ge 6)

    $today = (Get-Date).ToString('yyyyMMdd')
    $eventFile = Join-Path $WorkDir ('data\camera-events-' + $today + '.jsonl')
    Add-Check '事件落盘文件已生成' 'True' (Test-Path $eventFile)
    if (Test-Path $eventFile) {
        $diskText = [System.IO.File]::ReadAllText($eventFile)
        Add-Check '落盘内容含掉线记录' 'True' ($diskText -match '"event":\s*"offline"')
        Add-Check '落盘内容含告警记录' 'True' ($diskText -match '"event":\s*"alert-raised"')
    }

    # ---------------------------------------------------------------- 5) 掉线 -> 恢复
    Write-Host "`n=== 5) 相机掉线再恢复：告警消除 + 离线时长 ===" -ForegroundColor Cyan
    Append-Lines -Path $spoolFile -Lines @(
        (New-CamEvent -Camera 'cam-top' -Online $false -AtMs ([DateTimeOffset]::Now.ToUnixTimeMilliseconds()) -Position 'top')
    )
    Start-Sleep -Seconds 4

    $afterOffline = Get-Json '/api/monitor/alerts?limit=200&activeOnly=true'
    Add-Check 'cam-top 掉线后产生离线告警' 'True' (@($afterOffline.items | Where-Object { $_.camera -eq 'cam-top' -and $_.code -eq 'camera-offline' }).Count -ge 1)

    Append-Lines -Path $spoolFile -Lines @(
        (New-CamEvent -Camera 'cam-top' -Online $true -AtMs ([DateTimeOffset]::Now.ToUnixTimeMilliseconds()) -Position 'top')
    )
    Start-Sleep -Seconds 3

    $allAlerts = Get-Json '/api/monitor/alerts?limit=200&activeOnly=false'
    $topAlert = @($allAlerts.items | Where-Object { $_.camera -eq 'cam-top' -and $_.code -eq 'camera-offline' })[0]
    Add-Check 'cam-top 恢复后离线告警自动消除' 'False' $topAlert.active
    Add-Check '告警记录了恢复时间' 'True' ([string]::IsNullOrEmpty($topAlert.clearedAt) -eq $false)

    $topEvents = Get-Json '/api/monitor/events?limit=100&camera=cam-top'
    Add-Check '有恢复事件' 'True' (@($topEvents | Where-Object { $_.event -eq 'recovered' }).Count -ge 1)
    Add-Check '恢复事件带本次离线时长' 'True' (@($topEvents | Where-Object { $_.offlineDurationMs -gt 0 }).Count -ge 1)

    # ---------------------------------------------------------------- 6) 出码 = 心跳，心跳恢复后告警消除
    Write-Host "`n=== 6) 出码当作心跳：心跳超时告警自动消除 ===" -ForegroundColor Cyan
    Append-Lines -Path $spoolFile -Lines @(
        (New-ParcelEvent -TraceId 'B8-P3' -Camera 'cam-silent' -Code 'SF800000000003' -AtMs ([DateTimeOffset]::Now.ToUnixTimeMilliseconds()) -EventId 3)
    )
    Start-Sleep -Seconds 4

    $silentActive = Get-Json '/api/monitor/alerts?limit=200&activeOnly=true'
    Add-Check 'cam-silent 心跳恢复后不再报超时' 0 (@($silentActive.items | Where-Object { $_.camera -eq 'cam-silent' -and $_.code -eq 'heartbeat-timeout' }).Count)
    $silentCam = @((Get-Json '/api/monitor/cameras') | Where-Object { $_.camera -eq 'cam-silent' })[0]
    Add-Check '出码记进了最近出码时间' 'True' ([string]::IsNullOrEmpty($silentCam.lastCodeAt) -eq $false)

    # ---------------------------------------------------------------- 7) 重启后事件还在
    Write-Host "`n=== 7) 平台重启：掉线记录与告警状态恢复 ===" -ForegroundColor Cyan
    Stop-Platform
    Start-Platform
    Start-Sleep -Seconds 3

    $eventsAfter = Get-Json '/api/monitor/events?limit=300'
    Add-Check '重启后掉线记录还在' 'True' (@($eventsAfter | Where-Object { $_.event -eq 'offline' }).Count -ge 5)
    Add-Check '重启后恢复事件还在' 'True' (@($eventsAfter | Where-Object { $_.event -eq 'recovered' }).Count -ge 1)

    $summaryAfter = Get-Json '/api/monitor/summary'
    Add-Check '重启后相机数恢复' 'True' ($summaryAfter.cameras -ge 5)

    $alertsAfter = Get-Json '/api/monitor/alerts?limit=200&activeOnly=true'
    Add-Check '重启后不会把在线相机误报成离线' 0 (@($alertsAfter.items | Where-Object { $_.camera -eq 'cam-top' -and $_.code -eq 'camera-offline' }).Count)

    # ---------------------------------------------------------------- 8) 阈值配置
    Write-Host "`n=== 8) 监控阈值配置（读取 / 修改 / 非法值 / 备份 / 热生效）===" -ForegroundColor Cyan
    $cfg = Get-Json '/api/monitor/config'
    Add-Check '读到的就是脚本写进去的阈值' 5 $cfg.options.heartbeatTimeoutSeconds
    Add-Check '配置里带事件目录' 'True' ([string]::IsNullOrEmpty($cfg.dataDirectory) -eq $false)

    $body = $cfg.options
    $body.heartbeatTimeoutSeconds = 7
    $save = Post-Json -Path '/api/monitor/config' -Body $body
    Add-Check '保存阈值返回 200' 200 $save.status
    Add-Check '保存后立即生效（返回里就是新值）' 7 $save.body.options.heartbeatTimeoutSeconds
    Add-Check '旧配置自动备份' 'True' ([string]::IsNullOrEmpty($save.body.backup) -eq $false -and (Test-Path $save.body.backup))

    $cfgAfter = Get-Json '/api/monitor/config'
    Add-Check '重新读取是新值' 7 $cfgAfter.options.heartbeatTimeoutSeconds

    $bad = $cfgAfter.options
    $bad.onlineRateAlertPercent = 150
    $badResult = Post-Json -Path '/api/monitor/config' -Body $bad -ExpectError
    Add-Check '非法阈值被拒绝（400）' 400 $badResult.status
    Add-Check '拒绝后文件里的值没被改坏' 7 (Get-Json '/api/monitor/config').options.heartbeatTimeoutSeconds

    # ---------------------------------------------------------------- 9) 原有链路不受影响
    Write-Host "`n=== 9) 监控不影响原有链路 ===" -ForegroundColor Cyan
    $stats = Get-Json '/api/stats'
    Add-Check '包裹照常入库' 'True' ($stats.parcels -ge 3)
    Add-Check '读码率正常' 'True' ($stats.readRate -ge 1)
    $devices = Get-Json '/api/devices'
    Add-Check '设备页照常返回相机清单' 'True' (($devices.cameras).Count -ge 5)

    # SSE 里应该能拿到监控快照
    $streamOk = $false
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $client.Connect('127.0.0.1', $Port)
        $stream = $client.GetStream()
        $request = "GET /api/stream HTTP/1.1`r`nHost: 127.0.0.1`r`nAccept: text/event-stream`r`nConnection: close`r`n`r`n"
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($request)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()
        Start-Sleep -Milliseconds 1500
        $buffer = New-Object byte[] 65536
        $read = $stream.Read($buffer, 0, $buffer.Length)
        $text = [System.Text.Encoding]::UTF8.GetString($buffer, 0, [Math]::Max(0, $read))
        $streamOk = ($text -match '"type":"monitor"')
        $client.Close()
    }
    catch {
        Write-Host ("SSE 读取失败：" + $_.Exception.Message) -ForegroundColor Yellow
    }
    Add-Check 'SSE 补发里带监控快照' 'True' $streamOk
}
finally {
    Stop-Platform
}

Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('B8 回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    exit 0
}
Write-Host ('B8 回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
