<#
    C6 回归测试：日志查看与导出。

    验收点（需求原文）：按时间范围导出成功；压缩包可离线分析。

    脚本造出四类日志（采集宿主 / 平台 / 大华 SDK / 事件缓冲 + 审计），然后：
      1) 来源概览：文件数、大小、最新文件；
      2) 按时间范围列文件：今天/昨天各归各的，SDK 那种没日期的按修改时间落范围；
      3) 看尾部：能读到内容，越权路径与不存在的文件被拒；
      4) 单文件下载：内容正确，越权被拒；
      5) **一键打包**：真下载 zip → 真解压 → 校验目录结构、README、内容、以及"范围里没有的文件不能进包"；
      6) 只勾一个来源时，包里就只有那一个来源；未登录访问诊断接口被 401 拦。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-c6-diag.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\test-c6-diag.ps1 -KeepRunning
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = '',
    [int]$Port = 8103,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-c6-test' }
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
    $log = Join-Path $WorkDir 'logs\c6-test-platform.log'
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
    $params = @{ Uri = $baseUrl + $Path; Method = $Method; UseBasicParsing = $true; TimeoutSec = 120 }
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

Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
Stop-Platform
if (Test-Path $WorkDir) {
    Move-Item -LiteralPath $WorkDir -Destination ("$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force
}
robocopy $SourceRuntime $WorkDir /E /XD spool data images logs cache /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $WorkDir 'spool'), (Join-Path $WorkDir 'data'), `
    (Join-Path $WorkDir 'images'), (Join-Path $WorkDir 'logs'), (Join-Path $WorkDir 'Log') | Out-Null
[System.IO.File]::WriteAllText((Join-Path $WorkDir 'config\auth.json'),
    '{"enabled":true,"protectRead":false,"allowServiceKey":true,"serviceKey":"c6-test","serviceKeyRole":"admin","maxFailures":5,"lockMinutes":15,"failureWindowMinutes":10,"sessionMinutes":480}',
    $utf8NoBom)

# ---------------------------------------------------------------- 造日志
$today = (Get-Date).Date
$yesterday = $today.AddDays(-1)
$todayKey = $today.ToString('yyyyMMdd')
$yesterdayKey = $yesterday.ToString('yyyyMMdd')

$hostToday = Join-Path $WorkDir ('logs\host-' + $todayKey + '.log')
$hostYesterday = Join-Path $WorkDir ('logs\host-' + $yesterdayKey + '.log')
$platformLog = Join-Path $WorkDir ('logs\platform-' + $todayKey + '.log')
$sdkAlg = Join-Path $WorkDir 'Log\Alg.log'
$spoolFile = Join-Path $WorkDir ('spool\events-' + $todayKey + '.jsonl')
$authFile = Join-Path $WorkDir ('data\auth-events-' + $todayKey + '.jsonl')

[System.IO.File]::WriteAllText($hostToday, @"
[08:00:01][info] 已加载 provider 插件：dahua-dws
[08:00:02][info] 相机上线 cam-top(172.20.10.11)
[09:12:33][parcel] 条码 SF000000000001 相机=cam-top 条码数=1
[10:20:41][error] 相机 cam-left 回调超时，准备重连（第 2 次）
[10:20:42][warn] 采集队列使用率 82%
"@, $utf8NoBom)

[System.IO.File]::WriteAllText($hostYesterday, @"
[21:00:00][info] 采集宿主启动
[21:05:00][error] 加密狗未就绪（返回码 2200）
"@, $utf8NoBom)

[System.IO.File]::WriteAllText($platformLog, @"
2026-09-18 10:00:00 info: 平台启动完成
2026-09-18 10:00:01 info: 已从 spool 回放相机状态 6 条
"@, $utf8NoBom)

[System.IO.File]::WriteAllText($sdkAlg, "SDK 算法日志：读码耗时 12ms`r`n", $utf8NoBom)
[System.IO.File]::WriteAllText($authFile, '{"time":"2026-09-18 10:00:00","kind":"login-ok","username":"admin"}' + "`n", $utf8NoBom)

$parcel = @{
    schemaVersion = 1; type = 'parcel'; eventId = 1; providerId = 'test'; deviceId = 'cam-top'
    stage = 'enriched'; capturedAtMs = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
    receivedAtMs = [DateTimeOffset]::Now.ToUnixTimeMilliseconds(); traceId = 'C6-P1'
    stagedResult = $false; weightGrams = 500; codes = @(@{ value = 'SF000000000001'; kind = '1d'; position = 'top' })
    images = @()
}
[System.IO.File]::WriteAllText($spoolFile, ($parcel | ConvertTo-Json -Compress -Depth 6) + "`n", $utf8NoBom)

# 让修改时间贴近真实：今天的日志是今天写的、昨天的是昨天写的。
# 这样"最新文件"和"没有日期的文件按修改时间过滤"两条才验证得准。
(Get-Item $hostYesterday).LastWriteTime = $yesterday.AddHours(21)
(Get-Item $hostToday).LastWriteTime = $today.AddHours(10)
(Get-Item $platformLog).LastWriteTime = $today.AddHours(10)
(Get-Item $spoolFile).LastWriteTime = $today.AddHours(10)
(Get-Item $authFile).LastWriteTime = $today.AddHours(10)
(Get-Item $sdkAlg).LastWriteTime = $yesterday.AddHours(15)

try {
    Start-Platform
    . (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'b9-auth-helper.ps1')
    $null = Enable-TestAuth -WorkDir $WorkDir
    $key = (Get-Content -LiteralPath (Join-Path $WorkDir 'config\auth.json') -Raw -Encoding UTF8 | ConvertFrom-Json).serviceKey

    $from = $yesterday.ToString('yyyyMMdd')
    $to = $today.ToString('yyyyMMdd')

    # ---------------------------------------------------------------- 1) 来源概览
    Write-Host "`n=== 1) 日志来源概览 ===" -ForegroundColor Cyan
    $src = Call -Path '/api/diag/sources'
    Add-Check '来源概览 200' 200 $src.status
    Add-Check '六个来源' 6 @($src.body.sources).Count
    $hostSrc = @($src.body.sources | Where-Object { $_.id -eq 'host' })[0]
    Add-Check '采集宿主日志有 2 个文件（今天+昨天）' 2 $hostSrc.fileCount
    Add-Check '带大小文本' 'True' ($hostSrc.sizeText -match 'B|KB|MB')
    Add-Check '带最新文件名' 'True' ($hostSrc.newestFile -like ('host-' + $todayKey + '*'))
    $sdkSrc = @($src.body.sources | Where-Object { $_.id -eq 'sdk' })[0]
    Add-Check 'SDK 来源至少 1 个文件' 'True' ($sdkSrc.fileCount -ge 1)
    Add-Check '总文件数大于等于 6' 'True' ($src.body.totalFiles -ge 6)

    # ---------------------------------------------------------------- 2) 按时间范围
    Write-Host "`n=== 2) 按时间范围列文件 ===" -ForegroundColor Cyan
    $hostToday2 = Call -Path ('/api/diag/files?source=host&from=' + $todayKey + '&to=' + $todayKey)
    Add-Check '只看今天：host 只有 1 个文件' 1 $hostToday2.body.fileCount
    Add-Check '今天那份就是今天的' 'True' (@($hostToday2.body.files)[0].name -like ('host-' + $todayKey + '*'))

    $hostYest2 = Call -Path ('/api/diag/files?source=host&from=' + $yesterdayKey + '&to=' + $yesterdayKey)
    Add-Check '只看昨天：host 只有 1 个文件' 1 $hostYest2.body.fileCount
    Add-Check '昨天那份是昨天的' 'True' (@($hostYest2.body.files)[0].name -like ('host-' + $yesterdayKey + '*'))

    $sdk2 = Call -Path ('/api/diag/files?source=sdk&from=' + $yesterdayKey + '&to=' + $yesterdayKey)
    Add-Check 'SDK 无日期文件按修改时间落范围（昨天）' 'True' ($sdk2.body.fileCount -ge 1)
    $sdk3 = Call -Path ('/api/diag/files?source=sdk&from=' + $todayKey + '&to=' + $todayKey)
    Add-Check 'SDK 那份不属于今天' 0 $sdk3.body.fileCount

    $badSrc = Call -Path ('/api/diag/files?source=nope&from=' + $from + '&to=' + $to)
    Add-Check '未知来源被拒' 400 $badSrc.status

    # ---------------------------------------------------------------- 3) 看尾部
    Write-Host "`n=== 3) 查看尾部 ===" -ForegroundColor Cyan
    $tail = Call -Path ('/api/diag/tail?source=host&file=host-' + $todayKey + '.log&lines=50')
    Add-Check '看尾部 200' 200 $tail.status
    Add-Check '读到 5 行' 5 $tail.body.lines
    Add-Check '内容里有 ERROR 行' 'True' (($tail.body.content -join "`n") -match 'error')
    Add-Check '带文件大小与修改时间' 'True' (([string]::IsNullOrEmpty($tail.body.sizeText) -eq $false) -and ([string]::IsNullOrEmpty($tail.body.modified) -eq $false))

    $escape = Call -Path ('/api/diag/tail?source=host&file=' + [uri]::EscapeDataString('..\..\config\gateway.ini'))
    Add-Check '越权路径被拒' 400 $escape.status
    $missing = Call -Path ('/api/diag/tail?source=host&file=not-exist.log')
    Add-Check '不存在的文件被拒' 400 $missing.status

    # ---------------------------------------------------------------- 4) 单文件下载
    Write-Host "`n=== 4) 单文件下载 ===" -ForegroundColor Cyan
    $dl = Call -Path ('/api/diag/download?source=host&file=host-' + $todayKey + '.log')
    Add-Check '下载 200' 200 $dl.status
    # 二进制流（octet-stream）在 PowerShell 7 里拿到的是字节数组，先转成文本再断言
    $dlText = if ($dl.raw -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($dl.raw) } else { [string]$dl.raw }
    Add-Check '下载内容含 ERROR' 'True' ($dlText -match 'error')
    $dlEscape = Call -Path ('/api/diag/download?source=host&file=' + [uri]::EscapeDataString('..\gateway.ini'))
    Add-Check '下载越权被拒' 400 $dlEscape.status

    # ---------------------------------------------------------------- 5) 一键打包（真下载 + 真解压）
    Write-Host "`n=== 5) 一键打包 zip（下载并解压校验）===" -ForegroundColor Cyan
    $zipPath = Join-Path $WorkDir 'logs\bundle-all.zip'
    $zipUrl = $baseUrl + '/api/diag/bundle?from=' + $from + '&to=' + $to + '&sources=host,platform,sdk,spool,camera,auth'
    Invoke-WebRequest -Uri $zipUrl -UseBasicParsing -TimeoutSec 180 -OutFile $zipPath -Headers @{ 'X-Api-Key' = $key }
    Add-Check '打包文件已下载' 'True' (Test-Path $zipPath)
    $zipBytes = [System.IO.File]::ReadAllBytes($zipPath)
    Add-Check '是 zip（PK 头）' 'True' ($zipBytes[0] -eq 80 -and $zipBytes[1] -eq 75)
    Add-Check '包不是空的（> 1KB）' 'True' ($zipBytes.Length -gt 1024)

    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $names = @($zip.Entries | ForEach-Object { $_.FullName })
        Add-Check '包里有 README.txt' 'True' ($names -contains 'README.txt')
        Add-Check '包里有今天的采集日志' 'True' ($names -contains ('host/host-' + $todayKey + '.log'))
        Add-Check '包里有昨天的采集日志' 'True' ($names -contains ('host/host-' + $yesterdayKey + '.log'))
        Add-Check '包里有 spool 事件' 'True' (@($names | Where-Object { $_ -like 'spool/events-*' }).Count -ge 1)
        Add-Check '包里有 SDK 日志' 'True' (@($names | Where-Object { $_ -like 'sdk/*' }).Count -ge 1)
        Add-Check '包里有审计日志' 'True' (@($names | Where-Object { $_ -like 'auth/*' }).Count -ge 1)

        $readme = $zip.Entries | Where-Object { $_.FullName -eq 'README.txt' } | Select-Object -First 1
        $reader = New-Object System.IO.StreamReader($readme.Open())
        $readmeText = $reader.ReadToEnd()
        $reader.Close()
        Add-Check 'README 写了时间范围' 'True' ($readmeText -match '时间范围')
        Add-Check 'README 写了来源说明' 'True' ($readmeText -match '采集宿主日志')

        $entry = $zip.Entries | Where-Object { $_.FullName -eq ('host/host-' + $todayKey + '.log') } | Select-Object -First 1
        $reader2 = New-Object System.IO.StreamReader($entry.Open())
        $logText = $reader2.ReadToEnd()
        $reader2.Close()
        Add-Check '包里的日志内容可读（含 ERROR 行）' 'True' ($logText -match 'error')
    }
    finally {
        $zip.Dispose()
    }

    # 只选一个来源
    $zipOne = Join-Path $WorkDir 'logs\bundle-host-only.zip'
    Invoke-WebRequest -Uri ($baseUrl + '/api/diag/bundle?from=' + $from + '&to=' + $to + '&sources=host') `
        -UseBasicParsing -TimeoutSec 180 -OutFile $zipOne -Headers @{ 'X-Api-Key' = $key }
    $zip2 = [System.IO.Compression.ZipFile]::OpenRead($zipOne)
    try {
        $names2 = @($zip2.Entries | ForEach-Object { $_.FullName })
        Add-Check '只勾 host：包里只有 host/ 与 README' 'True' (($names2 | Where-Object { $_ -notlike 'host/*' -and $_ -ne 'README.txt' }).Count -eq 0)
        Add-Check '只勾 host：没有 sdk 目录' 'False' (@($names2 | Where-Object { $_ -like 'sdk/*' }).Count -ge 1)
    }
    finally {
        $zip2.Dispose()
    }

    # 时间范围外的文件不能进包
    $zipToday = Join-Path $WorkDir 'logs\bundle-today-only.zip'
    Invoke-WebRequest -Uri ($baseUrl + '/api/diag/bundle?from=' + $todayKey + '&to=' + $todayKey + '&sources=host') `
        -UseBasicParsing -TimeoutSec 180 -OutFile $zipToday -Headers @{ 'X-Api-Key' = $key }
    $zip3 = [System.IO.Compression.ZipFile]::OpenRead($zipToday)
    try {
        $names3 = @($zip3.Entries | ForEach-Object { $_.FullName })
        Add-Check '范围=今天：只含今天那份日志' 'True' ($names3 -contains ('host/host-' + $todayKey + '.log'))
        Add-Check '范围=今天：不含昨天那份' 'False' ($names3 -contains ('host/host-' + $yesterdayKey + '.log'))
    }
    finally {
        $zip3.Dispose()
    }

    $badBundle = Call -Path ('/api/diag/bundle?from=' + $from + '&to=' + $to + '&sources=nope')
    Add-Check '打包未知来源被拒' 400 $badBundle.status

    # ---------------------------------------------------------------- 6) 鉴权与前端产物
    Write-Host "`n=== 6) 鉴权与前端产物 ===" -ForegroundColor Cyan
    $anon = Call -Path '/api/diag/sources' -Anonymous
    Add-Check '未登录看诊断被 401 拦' 401 $anon.status

    $html = [System.IO.File]::ReadAllText((Join-Path $WorkDir 'platform\wwwroot\index.html'))
    Add-Check '有诊断页签' 'True' ($html -match 'id="tab-diag"')
    Add-Check '有诊断页容器' 'True' ($html -match 'id="page-diag"')
    Add-Check '有来源卡片与文件表' 'True' (($html -match 'id="diagSources"') -and ($html -match 'id="diagFileRows"'))
    Add-Check '有查看尾部区域' 'True' ($html -match 'id="diagTail"')

    $diagJs = [System.IO.File]::ReadAllText((Join-Path $WorkDir 'platform\wwwroot\js\diag.js'))
    Add-Check 'diag.js 调来源接口' 'True' ($diagJs -match 'diagSources')
    Add-Check 'diag.js 有打包下载' 'True' ($diagJs -match 'diagBundleUrl')
    Add-Check 'diag.js 有看尾部' 'True' ($diagJs -match 'diagTail')

    $apiJs = [System.IO.File]::ReadAllText((Join-Path $WorkDir 'platform\wwwroot\js\api.js'))
    Add-Check 'api.js 有诊断接口' 'True' (($apiJs -match 'api/diag/bundle') -and ($apiJs -match 'api/diag/tail'))
}
finally {
    if (!$KeepRunning) { Stop-Platform }
}

Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('C6 回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    if ($KeepRunning) { Write-Host ('平台还在跑：' + $baseUrl + '　打开"诊断"页看 C6') -ForegroundColor Yellow }
    exit 0
}
Write-Host ('C6 回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
