<#
    B4 回归测试：下游 TCP 输出（模板 + 失败重传 + 不丢不重）。

    脚本自己起一个 TCP 服务端（后台 Job），把收到的报文追加到文件，然后：
      1) 配好下游地址与模板，喂 3 个包裹 → 校验服务端收到 3 条、格式与模板一致；
      2) 停掉服务端（模拟下游断线），再喂 2 个包裹 → 校验"不丢"：包裹留在待发/失败队列里；
      3) 重新起服务端 → 校验队列自动补发：总共 5 条，每个追踪号恰好一条（不重）；
      4) 改模板（换分隔符/字段）→ 再喂 1 个包裹 → 校验服务端收到的是新格式；
      5) 模板校验：写错字段名要被报出来。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-b4-downstream.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\test-b4-downstream.ps1 -Port 8092 -TcpPort 9100
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = '',
    [int]$Port = 8092,
    [int]$TcpPort = 9100
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-b4-test' }

$platformExe = Join-Path $SourceRuntime 'platform\DwsEdge.Platform.exe'
if (!(Test-Path $platformExe)) { throw "找不到平台可执行文件：$platformExe（先跑一次 build.ps1）" }

$baseUrl = 'http://127.0.0.1:' + $Port
$recvFile = Join-Path $WorkDir 'logs\tcp-received.log'
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
    $log = Join-Path $WorkDir 'logs\b4-test-platform.log'
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
    throw "平台在 60 秒内没有就绪：$baseUrl"
}

function Post-Json {
    param([string]$Path, $Body)
    $json = $Body | ConvertTo-Json -Depth 8 -Compress
    return Invoke-RestMethod -Uri ($baseUrl + $Path) -Method POST -ContentType 'application/json; charset=utf-8' -Body $json -TimeoutSec 30
}

function Get-Received {
    if (!(Test-Path $recvFile)) { return @() }
    return @([System.IO.File]::ReadAllLines($recvFile, [System.Text.Encoding]::UTF8) | Where-Object { $_.Trim().Length -gt 0 })
}

function Start-TcpServer {
    param([int]$ListenPort, [string]$File)
    # 用独立进程而不是 Start-Job：Job 会一直占着控制台句柄，把调用方的会话挂住
    $serverScript = Join-Path $scriptDir 'tcp-test-server.ps1'
    $proc = Start-Process -FilePath 'powershell' `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $serverScript,
            '-Port', $ListenPort, '-File', $File) `
        -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 2
    return $proc
}

function New-ParcelEvents {
    param([string]$TraceId, [string]$Code, [string]$Device, [long]$CapturedAtMs, [int]$Weight)
    $items = New-Object System.Collections.Generic.List[string]
    $detected = @{
        schemaVersion = 1; type = 'parcel'; eventId = 1; providerId = 'test'; deviceId = $Device;
        stage = 'detected'; capturedAtMs = $CapturedAtMs; receivedAtMs = $CapturedAtMs; traceId = $TraceId;
        stagedResult = $true; weightGrams = -1; lengthMm = 0; widthMm = 0; heightMm = 0; volumeMm3 = 0;
        codes = @(@{ value = $Code; kind = '1d'; position = 'top' }); images = @()
    } | ConvertTo-Json -Compress -Depth 6
    $enriched = @{
        schemaVersion = 1; type = 'parcel'; eventId = 2; providerId = 'test'; deviceId = $Device;
        stage = 'enriched'; capturedAtMs = $CapturedAtMs; receivedAtMs = $CapturedAtMs; traceId = $TraceId;
        stagedResult = $true; weightGrams = $Weight; lengthMm = 300; widthMm = 200; heightMm = 150;
        volumeMm3 = 9000000; codes = @(@{ value = $Code; kind = '1d'; position = 'top' }); images = @()
    } | ConvertTo-Json -Compress -Depth 6
    $items.Add($detected)
    $items.Add($enriched)
    return $items
}

Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
Stop-Platform

if (Test-Path $WorkDir) {
    $backup = "$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Move-Item -LiteralPath $WorkDir -Destination $backup -Force
}
robocopy $SourceRuntime $WorkDir /E /XD spool data images logs /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $WorkDir 'spool'), (Join-Path $WorkDir 'data'), (Join-Path $WorkDir 'images'), (Join-Path $WorkDir 'logs') | Out-Null

$spoolFile = Join-Path $WorkDir ('spool\events-' + (Get-Date).ToString('yyyyMMdd') + '.jsonl')
$job = $null

try {
    $job = Start-TcpServer -ListenPort $TcpPort -File $recvFile
    Add-Check 'TCP 服务端已启动' 'True' ($job -ne $null -and !$job.HasExited)

    Start-Platform
    . (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'b9-auth-helper.ps1')
    $null = Enable-TestAuth -WorkDir $WorkDir   # B9：管理接口要凭据，脚本用服务令牌

    # ---------------------------------------------------------------- 1) 配置 + 发 3 个包裹
    Write-Host "`n=== 1) 配置下游地址与模板，发 3 个包裹 ===" -ForegroundColor Cyan
    $template1 = 'DWS|{code}|{time}|{camera}|{weight}|{volume}|{traceId}\r\n'
    $config = Post-Json -Path '/api/downstream' -Body @{
        enabled = $true; protocol = 'tcp-client'; host = '127.0.0.1'; port = $TcpPort;
        template = $template1; encoding = 'utf-8'; connectTimeoutMs = 3000;
        retryIntervalMs = 1000; maxAttempts = 0; sendOnlyComplete = $true; sendIntervalMs = 0
    }
    Add-Check '配置已保存' 'True' $config.ok
    Add-Check '模板没有问题' 0 @($config.stats.templateProblems).Count

    $now = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
    $batch1 = New-Object System.Collections.Generic.List[string]
    foreach ($item in (New-ParcelEvents -TraceId 'B4-1' -Code 'SF0000000001' -Device 'cam-top' -CapturedAtMs $now -Weight 500)) { $batch1.Add($item) }
    foreach ($item in (New-ParcelEvents -TraceId 'B4-2' -Code 'SF0000000002' -Device 'cam-top' -CapturedAtMs ($now + 100) -Weight 600)) { $batch1.Add($item) }
    foreach ($item in (New-ParcelEvents -TraceId 'B4-3' -Code 'SF0000000003' -Device 'cam-top' -CapturedAtMs ($now + 200) -Weight 700)) { $batch1.Add($item) }
    [System.IO.File]::WriteAllLines($spoolFile, $batch1, (New-Object System.Text.UTF8Encoding($false)))
    Start-Sleep -Seconds 6

    $received = Get-Received
    Add-Check '服务端收到 3 条报文' 3 $received.Count
    Add-Check '第 1 条以模板前缀开头' 'True' ($received[0].StartsWith('DWS|'))
    Add-Check '报文里带追踪号' 'True' ($received[0].EndsWith('|B4-1'))
    $parts = $received[0] -split '\|'
    Add-Check '字段个数（DWS|条码|时间|相机|重量|体积|追踪号）' 7 $parts.Count
    Add-Check '条码正确' 'SF0000000001' $parts[1]
    Add-Check '相机正确' 'cam-top' $parts[3]
    Add-Check '重量正确' '500' $parts[4]
    Add-Check '体积正确' '9000000' $parts[5]

    $stats = (Invoke-RestMethod -Uri ($baseUrl + '/api/downstream') -TimeoutSec 10).stats
    Add-Check '平台侧已发送计数' 3 $stats.sent
    Add-Check '下游连接状态' 'True' $stats.connected
    Add-Check '待发队列已清空' 0 $stats.queueDepth

    # ---------------------------------------------------------------- 2) 下游断线：不丢
    Write-Host "`n=== 2) 停掉下游服务端，再发 2 个包裹（不丢） ===" -ForegroundColor Cyan
    Stop-Process -Id $job.Id -Force -ErrorAction SilentlyContinue
    $job = $null
    Start-Sleep -Seconds 3

    $batch2 = New-Object System.Collections.Generic.List[string]
    foreach ($item in (New-ParcelEvents -TraceId 'B4-4' -Code 'SF0000000004' -Device 'cam-side' -CapturedAtMs ($now + 300) -Weight 800)) { $batch2.Add($item) }
    foreach ($item in (New-ParcelEvents -TraceId 'B4-5' -Code 'SF0000000005' -Device 'cam-side' -CapturedAtMs ($now + 400) -Weight 900)) { $batch2.Add($item) }
    [System.IO.File]::AppendAllLines($spoolFile, $batch2, (New-Object System.Text.UTF8Encoding($false)))
    Start-Sleep -Seconds 6

    $received = Get-Received
    Add-Check '断线期间服务端没有新报文' 3 $received.Count
    $stats = (Invoke-RestMethod -Uri ($baseUrl + '/api/downstream') -TimeoutSec 10).stats
    Add-Check '两个包裹留在待发队列（没丢）' 2 $stats.queueDepth
    Add-Check '下游连接已断开' 'False' $stats.connected

    # 注意：PowerShell 5.1 的 ConvertFrom-Json 处理"顶层 JSON 数组"时结果不稳定，
    # 所以这里直接数原始报文里的 traceId 个数（断言更可靠）。
    $pendingRaw = (Invoke-WebRequest -Uri ($baseUrl + '/api/dispatch/pending?limit=10') -UseBasicParsing).Content
    $pendingCount = ([regex]::Matches($pendingRaw, '"traceId"')).Count
    Add-Check '待下发列表里有这两个包裹' 2 $pendingCount

    # ---------------------------------------------------------------- 3) 下游恢复：补发且不重
    Write-Host "`n=== 3) 重启下游服务端 → 自动补发（不重） ===" -ForegroundColor Cyan
    $job = Start-TcpServer -ListenPort $TcpPort -File $recvFile
    Start-Sleep -Seconds 10

    $received = Get-Received
    Add-Check '恢复后共收到 5 条' 5 $received.Count
    $traceIds = $received | ForEach-Object { ($_ -split '\|')[-1] }
    $unique = @($traceIds | Sort-Object -Unique)
    Add-Check '5 条各自不同（没有重复下发）' 5 $unique.Count
    Add-Check '包含断线期间的 B4-4' 'True' ($unique -contains 'B4-4')
    Add-Check '包含断线期间的 B4-5' 'True' ($unique -contains 'B4-5')

    $stats = (Invoke-RestMethod -Uri ($baseUrl + '/api/downstream') -TimeoutSec 10).stats
    Add-Check '平台侧累计发送 5 条' 5 $stats.sent
    Add-Check '待发队列清空' 0 $stats.queueDepth

    # ---------------------------------------------------------------- 4) 换模板
    Write-Host "`n=== 4) 换数据格式模板 ===" -ForegroundColor Cyan
    $template2 = '{traceId}#{code}#{weight}#{volume}#{noread}\r\n'
    $config2 = Post-Json -Path '/api/downstream' -Body @{
        enabled = $true; protocol = 'tcp-client'; host = '127.0.0.1'; port = $TcpPort;
        template = $template2; encoding = 'utf-8'; connectTimeoutMs = 3000;
        retryIntervalMs = 1000; maxAttempts = 0; sendOnlyComplete = $true; sendIntervalMs = 0
    }
    Add-Check '新模板已保存' 'True' $config2.ok

    $batch3 = New-Object System.Collections.Generic.List[string]
    foreach ($item in (New-ParcelEvents -TraceId 'B4-6' -Code 'JD0000000006' -Device 'cam-top' -CapturedAtMs ($now + 500) -Weight 1000)) { $batch3.Add($item) }
    [System.IO.File]::AppendAllLines($spoolFile, $batch3, (New-Object System.Text.UTF8Encoding($false)))
    Start-Sleep -Seconds 6

    $received = Get-Received
    Add-Check '收到新格式报文' 6 $received.Count
    $last = $received[5]
    Add-Check '新格式：追踪号开头' 'True' ($last.StartsWith('B4-6#'))
    Add-Check '新格式：条码' 'True' ($last.Contains('#JD0000000006#'))
    Add-Check '新格式：无码标记 0' 'True' ($last.EndsWith('#0'))

    # 模板预览 + 校验
    $preview = Post-Json -Path '/api/downstream/preview' -Body @{ template = 'X|{code}|{weight}|{noSuchField}\r\n' }
    Add-Check '预览报出未知字段' 'True' (@($preview.problems).Count -ge 1)
    Add-Check '预览把未知字段原样保留' 'True' ($preview.rendered -like '*{noSuchField}*')
}
finally {
    if ($job) {
        Stop-Process -Id $job.Id -Force -ErrorAction SilentlyContinue
    }
    Stop-Platform
}

# ---------------------------------------------------------------- 汇总
Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('B4 回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    exit 0
}

Write-Host ('B4 回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
