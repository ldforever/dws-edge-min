<#
    B5 回归测试：下游 TCP **服务端**模式（平台监听，下游接入）。

    验收点：多客户端同时连接稳定；客户端断开不影响采集。

    脚本做的事：
      1) 把平台配成 tcp-server 模式（监听 127.0.0.1:端 口），起两个测试客户端 A / B；
      2) 喂 3 个包裹 → 校验 A、B **都**收到同样 3 条（广播）；
      3) 杀掉客户端 A → 再喂 2 个包裹 → 校验 B 正常收到、平台采集不受影响
         （包裹数继续增长、A 被移除后仍有客户端在线）；
      4) 让客户端 A 重连 → 再喂 1 个包裹 → 校验 A / B 又都能收到；
      5) 没有任何客户端在线时喂 1 个包裹 → 留在待发队列（不丢），接入客户端后自动补发。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-b5-tcp-server.ps1
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = '',
    [int]$Port = 8093,
    [int]$ListenPort = 9200
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-b5-test' }

$platformExe = Join-Path $SourceRuntime 'platform\DwsEdge.Platform.exe'
if (!(Test-Path $platformExe)) { throw "找不到平台可执行文件：$platformExe（先跑一次 build.ps1）" }

$baseUrl = 'http://127.0.0.1:' + $Port
$clientAFile = Join-Path $WorkDir 'logs\client-a.log'
$clientBFile = Join-Path $WorkDir 'logs\client-b.log'
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
    $log = Join-Path $WorkDir 'logs\b5-test-platform.log'
    Start-Process -FilePath $exe -ArgumentList @('--urls', $baseUrl) `
        -WorkingDirectory (Join-Path $WorkDir 'platform') -WindowStyle Hidden `
        -RedirectStandardOutput $log -RedirectStandardError ($log + '.err') | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        try { $null = Invoke-RestMethod -Uri ($baseUrl + '/api/health') -TimeoutSec 3; return } catch { }
    }
    throw "平台在 60 秒内没有就绪：$baseUrl"
}

function Post-Json {
    param([string]$Path, $Body)
    $json = $Body | ConvertTo-Json -Depth 8 -Compress
    return Invoke-RestMethod -Uri ($baseUrl + $Path) -Method POST -ContentType 'application/json; charset=utf-8' -Body $json -TimeoutSec 30
}

function Start-TestClient {
    param([int]$TargetPort, [string]$File, [string]$ServerHost = '127.0.0.1')
    $script = Join-Path $scriptDir 'tcp-test-client.ps1'
    return Start-Process -FilePath 'powershell' `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script,
            '-ServerHost', $ServerHost, '-Port', $TargetPort, '-File', $File) `
        -WindowStyle Hidden -PassThru
}

function Get-Lines {
    param([string]$File)
    if (!(Test-Path $File)) { return @() }
    return @([System.IO.File]::ReadAllLines($File, [System.Text.Encoding]::UTF8) | Where-Object { $_.Trim().Length -gt 0 })
}

function New-ParcelEvents {
    param([string]$TraceId, [string]$Code, [string]$Device, [long]$CapturedAtMs, [int]$Weight)
    $items = New-Object System.Collections.Generic.List[string]
    $items.Add((@{
        schemaVersion = 1; type = 'parcel'; eventId = 1; providerId = 'test'; deviceId = $Device;
        stage = 'detected'; capturedAtMs = $CapturedAtMs; receivedAtMs = $CapturedAtMs; traceId = $TraceId;
        stagedResult = $true; weightGrams = -1; lengthMm = 0; widthMm = 0; heightMm = 0; volumeMm3 = 0;
        codes = @(@{ value = $Code; kind = '1d'; position = 'top' }); images = @()
    } | ConvertTo-Json -Compress -Depth 6))
    $items.Add((@{
        schemaVersion = 1; type = 'parcel'; eventId = 2; providerId = 'test'; deviceId = $Device;
        stage = 'enriched'; capturedAtMs = $CapturedAtMs; receivedAtMs = $CapturedAtMs; traceId = $TraceId;
        stagedResult = $true; weightGrams = $Weight; lengthMm = 300; widthMm = 200; heightMm = 150;
        volumeMm3 = 9000000; codes = @(@{ value = $Code; kind = '1d'; position = 'top' }); images = @()
    } | ConvertTo-Json -Compress -Depth 6))
    return $items
}

function Feed {
    param([string[]]$TraceIds, [long]$BaseMs)
    $batch = New-Object System.Collections.Generic.List[string]
    $index = 0
    foreach ($id in $TraceIds) {
        # 造一条规范运单号：SF + 12 位数字（和真实现场格式一致）
        $script:codeSeq++
        $code = 'SF' + $script:codeSeq.ToString('D12')
        foreach ($item in (New-ParcelEvents -TraceId $id -Code $code -Device 'cam-top' -CapturedAtMs ($BaseMs + $index * 100) -Weight (500 + $index))) {
            $batch.Add($item)
        }
        $index++
    }
    [System.IO.File]::AppendAllLines($script:spoolFile, $batch, (New-Object System.Text.UTF8Encoding($false)))
}

Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
Stop-Platform
Get-Process -Name 'powershell' -ErrorAction SilentlyContinue |
    Where-Object { $_.Id -ne $PID -and $_.StartTime -gt (Get-Date).AddMinutes(-5) } |
    Stop-Process -Force -ErrorAction SilentlyContinue

if (Test-Path $WorkDir) {
    Move-Item -LiteralPath $WorkDir -Destination ("$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force
}
robocopy $SourceRuntime $WorkDir /E /XD spool data images logs /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $WorkDir 'spool'), (Join-Path $WorkDir 'data'), (Join-Path $WorkDir 'images'), (Join-Path $WorkDir 'logs') | Out-Null

$script:spoolFile = Join-Path $WorkDir ('spool\events-' + (Get-Date).ToString('yyyyMMdd') + '.jsonl')
$script:codeSeq = 0
$clientA = $null
$clientB = $null

try {
    Start-Platform
    . (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'b9-auth-helper.ps1')
    $null = Enable-TestAuth -WorkDir $WorkDir   # B9：管理接口要凭据，脚本用服务令牌

    # ---------------------------------------------------------------- 1) 服务端模式配置
    Write-Host "`n=== 1) 配成 TCP 服务端模式（平台监听） ===" -ForegroundColor Cyan
    $config = Post-Json -Path '/api/downstream' -Body @{
        enabled = $true; protocol = 'tcp-server'; host = '127.0.0.1'; port = $ListenPort;
        template = '{traceId}|{code}|{weight}\r\n'; encoding = 'utf-8'; connectTimeoutMs = 3000;
        retryIntervalMs = 1000; maxAttempts = 0; sendOnlyComplete = $true; sendIntervalMs = 0;
        replayRecentCount = 0
    }
    Add-Check '服务端配置已保存' 'True' $config.ok
    Add-Check '模板没有问题' 0 @($config.stats.templateProblems).Count
    Start-Sleep -Seconds 2
    $stats = (Invoke-RestMethod -Uri ($baseUrl + '/api/downstream') -TimeoutSec 10).stats
    Add-Check '正在监听' 'True' $stats.listening
    Add-Check 'listenTarget 正确' ('127.0.0.1:' + $ListenPort) $stats.listenTarget

    # ---------------------------------------------------------------- 2) 两个客户端同时接入
    Write-Host "`n=== 2) 两个下游客户端同时接入，发 3 个包裹 ===" -ForegroundColor Cyan
    $clientA = Start-TestClient -TargetPort $ListenPort -File $clientAFile
    Start-Sleep -Seconds 1
    $clientB = Start-TestClient -TargetPort $ListenPort -File $clientBFile
    Start-Sleep -Seconds 3

    $stats = (Invoke-RestMethod -Uri ($baseUrl + '/api/downstream') -TimeoutSec 10).stats
    Add-Check '在线客户端数' 2 $stats.clientCount

    $base = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
    Feed -TraceIds @('B5-1', 'B5-2', 'B5-3') -BaseMs $base
    Start-Sleep -Seconds 6

    $linesA = Get-Lines -File $clientAFile
    $linesB = Get-Lines -File $clientBFile
    Add-Check '客户端 A 收到 3 条' 3 $linesA.Count
    Add-Check '客户端 B 收到 3 条' 3 $linesB.Count
    Add-Check 'A 收到的是模板格式' 'True' ($linesA[0] -match '^B5-\d\|SF\d+\|\d+$')
    Add-Check 'A/B 收到的内容一致' ($linesA -join ';') ($linesB -join ';')

    # ---------------------------------------------------------------- 3) 客户端断开不影响
    Write-Host "`n=== 3) 杀掉客户端 A，再发 2 个包裹 ===" -ForegroundColor Cyan
    Stop-Process -Id $clientA.Id -Force -ErrorAction SilentlyContinue
    $clientA = $null
    Start-Sleep -Seconds 3

    $stats = (Invoke-RestMethod -Uri ($baseUrl + '/api/downstream') -TimeoutSec 10).stats
    Add-Check '断开后在线客户端数' 1 $stats.clientCount

    Feed -TraceIds @('B5-4', 'B5-5') -BaseMs ($base + 1000)
    Start-Sleep -Seconds 6

    $linesB = Get-Lines -File $clientBFile
    Add-Check '客户端 B 又收到 2 条（不受 A 断开影响）' 5 $linesB.Count
    $stats = (Invoke-RestMethod -Uri ($baseUrl + '/api/downstream') -TimeoutSec 10).stats
    Add-Check '平台已发送计数' 5 $stats.sent

    # 采集是否受影响：喂的包裹都要进库
    $parcels = (Invoke-RestMethod -Uri ($baseUrl + '/api/stats') -TimeoutSec 10)
    Add-Check '采集不受影响（包裹数 ≥ 5）' 'True' ($parcels.parcels -ge 5)

    # ---------------------------------------------------------------- 4) 客户端重连
    Write-Host "`n=== 4) 客户端 A 重连后再发 1 个包裹 ===" -ForegroundColor Cyan
    $clientA = Start-TestClient -TargetPort $ListenPort -File $clientAFile
    Start-Sleep -Seconds 3

    $stats = (Invoke-RestMethod -Uri ($baseUrl + '/api/downstream') -TimeoutSec 10).stats
    Add-Check '重连后在线客户端数' 2 $stats.clientCount

    Feed -TraceIds @('B5-6') -BaseMs ($base + 2000)
    Start-Sleep -Seconds 6

    $linesA = Get-Lines -File $clientAFile
    $linesB = Get-Lines -File $clientBFile
    Add-Check 'A 重连后收到新包裹' 'True' (($linesA -join ';') -match 'B5-6')
    Add-Check 'B 也收到新包裹' 'True' (($linesB -join ';') -match 'B5-6')

    # ---------------------------------------------------------------- 5) 没有客户端时不丢
    Write-Host "`n=== 5) 没有客户端在线时发的包裹不丢 ===" -ForegroundColor Cyan
    Stop-Process -Id $clientA.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $clientB.Id -Force -ErrorAction SilentlyContinue
    $clientA = $null
    $clientB = $null
    Start-Sleep -Seconds 3

    $stats = (Invoke-RestMethod -Uri ($baseUrl + '/api/downstream') -TimeoutSec 10).stats
    Add-Check '客户端都断开后为 0' 0 $stats.clientCount

    Feed -TraceIds @('B5-7') -BaseMs ($base + 3000)
    Start-Sleep -Seconds 5

    $stats = (Invoke-RestMethod -Uri ($baseUrl + '/api/downstream') -TimeoutSec 10).stats
    Add-Check '没有客户端时包裹留在待发队列' 1 $stats.queueDepth

    $clientB = Start-TestClient -TargetPort $ListenPort -File $clientBFile
    Start-Sleep -Seconds 8

    $linesB = Get-Lines -File $clientBFile
    Add-Check '新客户端接入后自动补发' 'True' (($linesB -join ';') -match 'B5-7')
    $stats = (Invoke-RestMethod -Uri ($baseUrl + '/api/downstream') -TimeoutSec 10).stats
    Add-Check '待发队列清空' 0 $stats.queueDepth
}
finally {
    foreach ($p in @($clientA, $clientB)) {
        if ($p) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
    Stop-Platform
}

# ---------------------------------------------------------------- 汇总
Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('B5 回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    exit 0
}

Write-Host ('B5 回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
