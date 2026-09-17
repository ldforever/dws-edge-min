<#
    B6 回归测试：HTTP 推送（带重试与幂等键）。

    验收点：下游返回错误时自动重试并最终成功；重复请求不产生重复业务（靠幂等键）。

    脚本做的事：
      1) 起一个测试 HTTP 接收端，platform 配成 http 模式（模板 + 幂等键头 Idempotency-Key）；
      2) 喂 3 个包裹 → 校验收到 3 个 POST、请求体符合模板、每个请求都带 Idempotency-Key=<traceId>；
      3) 故障注入：对 B6-2 的前 2 次请求返回 500 → 校验平台**重试**并最终成功
         （日志里能看到 FAIL(1)、FAIL(2)、OK，说明重试而非丢弃）；
      4) 停掉接收端再喂 2 个包裹 → 包裹留在待发队列（不丢）；重启接收端 → 自动补发且每个追踪号只成功一次；
      5) 校验统计与最近下发记录里的状态码。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-b6-http.ps1
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = '',
    [int]$Port = 8094,
    [int]$HttpPort = 9300
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-b6-test' }

$platformExe = Join-Path $SourceRuntime 'platform\DwsEdge.Platform.exe'
if (!(Test-Path $platformExe)) { throw "找不到平台可执行文件：$platformExe（先跑一次 build.ps1）" }

$baseUrl = 'http://127.0.0.1:' + $Port
$recvFile = Join-Path $WorkDir 'logs\http-received.log'
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
    $log = Join-Path $WorkDir 'logs\b6-test-platform.log'
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

function Start-HttpServer {
    param([int]$ListenPort, [string]$File, [string]$FailKey = '', [int]$FailTimes = 0)
    $script = Join-Path $scriptDir 'http-test-server.ps1'
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script,
        '-Port', $ListenPort, '-File', $File)
    if ($FailKey.Length -gt 0) { $args += @('-FailKey', $FailKey, '-FailTimes', $FailTimes) }
    return Start-Process -FilePath 'powershell' -ArgumentList $args -WindowStyle Hidden -PassThru
}

function Get-Received {
    if (!(Test-Path $recvFile)) { return @() }
    return @([System.IO.File]::ReadAllLines($recvFile, [System.Text.Encoding]::UTF8) | Where-Object { $_.Trim().Length -gt 0 })
}

function Feed {
    param([string[]]$TraceIds, [long]$BaseMs)
    $batch = New-Object System.Collections.Generic.List[string]
    $index = 0
    foreach ($id in $TraceIds) {
        $script:codeSeq++
        $code = 'SF' + $script:codeSeq.ToString('D12')
        $detected = @{
            schemaVersion = 1; type = 'parcel'; eventId = 1; providerId = 'test'; deviceId = 'cam-top';
            stage = 'detected'; capturedAtMs = ($BaseMs + $index * 100); receivedAtMs = ($BaseMs + $index * 100); traceId = $id;
            stagedResult = $true; weightGrams = -1; lengthMm = 0; widthMm = 0; heightMm = 0; volumeMm3 = 0;
            codes = @(@{ value = $code; kind = '1d'; position = 'top' }); images = @()
        } | ConvertTo-Json -Compress -Depth 6
        $enriched = @{
            schemaVersion = 1; type = 'parcel'; eventId = 2; providerId = 'test'; deviceId = 'cam-top';
            stage = 'enriched'; capturedAtMs = ($BaseMs + $index * 100); receivedAtMs = ($BaseMs + $index * 100); traceId = $id;
            stagedResult = $true; weightGrams = (500 + $index); lengthMm = 300; widthMm = 200; heightMm = 150; volumeMm3 = 9000000;
            codes = @(@{ value = $code; kind = '1d'; position = 'top' }); images = @()
        } | ConvertTo-Json -Compress -Depth 6
        $batch.Add($detected)
        $batch.Add($enriched)
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
$httpServer = $null

try {
    # 先起接收端，但故意对 B6-2 的前 2 次请求返回 500
    $httpServer = Start-HttpServer -ListenPort $HttpPort -File $recvFile -FailKey 'B6-2' -FailTimes 2
    Start-Sleep -Seconds 3

    Start-Platform

    # ---------------------------------------------------------------- 1) 配置 HTTP 推送
    Write-Host "`n=== 1) 配成 HTTP 推送模式 ===" -ForegroundColor Cyan
    $config = Post-Json -Path '/api/downstream' -Body @{
        enabled = $true; protocol = 'http';
        url = ('http://127.0.0.1:' + $HttpPort + '/dws');
        host = '127.0.0.1'; port = $HttpPort;
        template = '{traceId}|{code}|{weight}';
        encoding = 'utf-8'; contentType = 'text/plain; charset=utf-8';
        idempotencyHeader = 'Idempotency-Key';
        headers = @('X-Dws-Source: lightcookr');
        httpTimeoutMs = 3000; retryIntervalMs = 1000; maxAttempts = 0;
        sendOnlyComplete = $true; sendIntervalMs = 0
    }
    Add-Check 'HTTP 配置已保存' 'True' $config.ok
    Add-Check '模板没有问题' 0 @($config.stats.templateProblems).Count
    $stats = (Invoke-RestMethod -Uri ($baseUrl + '/api/downstream') -TimeoutSec 10).stats
    Add-Check '状态里是 HTTP 模式' 'True' $stats.httpMode
    Add-Check '目标地址正确' ('http://127.0.0.1:' + $HttpPort + '/dws') $stats.httpTarget

    # ---------------------------------------------------------------- 2) 推送 3 个包裹 + 故障重试
    Write-Host "`n=== 2) 推送 3 个包裹（其中 B6-2 前两次会被拒绝）===" -ForegroundColor Cyan
    $base = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
    Feed -TraceIds @('B6-1', 'B6-2', 'B6-3') -BaseMs $base
    Start-Sleep -Seconds 10

    $received = Get-Received
    $okLines = @($received | Where-Object { $_.StartsWith('OK|') })
    $failLines = @($received | Where-Object { $_.StartsWith('FAIL(') })

    Add-Check '成功接收 3 条（每个包裹一条）' 3 $okLines.Count
    Add-Check 'B6-2 被拒绝 2 次（故障注入生效）' 2 $failLines.Count
    Add-Check '重试后 B6-2 最终成功' 'True' (($okLines -join ';') -match 'B6-2')

    $first = $okLines[0] -split '\|', 3
    Add-Check '请求带幂等键（=追踪号）' 'True' ($first[1] -match '^B6-\d$')
    Add-Check '请求体符合模板' 'True' ($first[2] -match '^B6-\d\|SF\d{12}\|\d+$')

    # 幂等键与追踪号一一对应：B6-2 的 2 次失败 + 1 次成功都应带同一个键
    $b62Keys = @($received | Where-Object { $_ -match 'B6-2' } | ForEach-Object { ($_ -split '\|')[1] })
    $b62Unique = @($b62Keys | Sort-Object -Unique)
    Add-Check '同一包裹的重试都带同一个幂等键' 1 $b62Unique.Count
    Add-Check '  该键就是 traceId' 'B6-2' $b62Unique[0]

    $stats = (Invoke-RestMethod -Uri ($baseUrl + '/api/downstream') -TimeoutSec 10).stats
    Add-Check '平台侧已发送计数' 3 $stats.sent
    Add-Check '待发队列清空' 0 $stats.queueDepth

    # ---------------------------------------------------------------- 3) 接收端不可用时：不丢
    Write-Host "`n=== 3) 停掉接收端，再发 2 个包裹（不丢）===" -ForegroundColor Cyan
    Stop-Process -Id $httpServer.Id -Force -ErrorAction SilentlyContinue
    $httpServer = $null
    Start-Sleep -Seconds 3

    Feed -TraceIds @('B6-4', 'B6-5') -BaseMs ($base + 1000)
    Start-Sleep -Seconds 8

    $stats = (Invoke-RestMethod -Uri ($baseUrl + '/api/downstream') -TimeoutSec 10).stats
    Add-Check '接收端不可用时包裹留在待发队列' 2 $stats.queueDepth
    Add-Check '  并且记录了失败原因' 'True' (-not [string]::IsNullOrEmpty($stats.lastError))

    # ---------------------------------------------------------------- 4) 接收端恢复：自动补发
    Write-Host "`n=== 4) 接收端恢复 → 自动补发 ===" -ForegroundColor Cyan
    $httpServer = Start-HttpServer -ListenPort $HttpPort -File $recvFile
    Start-Sleep -Seconds 10

    $received = Get-Received
    $okLines = @($received | Where-Object { $_.StartsWith('OK|') })
    $traceIds = @($okLines | ForEach-Object { ($_ -split '\|')[1] } | Sort-Object -Unique)
    Add-Check '恢复后累计成功 5 条' 5 $okLines.Count
    Add-Check '5 个追踪号各自成功一次' 5 $traceIds.Count
    Add-Check '包含断线期间的 B6-4' 'True' ($traceIds -contains 'B6-4')
    Add-Check '包含断线期间的 B6-5' 'True' ($traceIds -contains 'B6-5')

    $stats = (Invoke-RestMethod -Uri ($baseUrl + '/api/downstream') -TimeoutSec 10).stats
    Add-Check '待发队列清空' 0 $stats.queueDepth
    Add-Check '平台侧累计成功 5 条' 5 $stats.sent

    # ---------------------------------------------------------------- 5) 最近的失败记录
    $log = (Invoke-WebRequest -Uri ($baseUrl + '/api/downstream/log?limit=50') -UseBasicParsing).Content
    Add-Check '下发日志里能看到失败记录' 'True' ($log -match 'HTTP 500')
}
finally {
    if ($httpServer) { Stop-Process -Id $httpServer.Id -Force -ErrorAction SilentlyContinue }
    Stop-Platform
}

# ---------------------------------------------------------------- 汇总
Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('B6 回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    exit 0
}

Write-Host ('B6 回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
