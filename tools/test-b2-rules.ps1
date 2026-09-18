<#
    B2 回归测试：条码过滤规则（长度 / 前后缀 / 正则 / 黑白名单 / 多条规则组合 + 优先级）。

    全部离线，不需要相机和加密狗。脚本会：
      1) 用 /api/rules/test 试跑一份"组合规则"，逐条核对每个条码的判定与命中的规则；
      2) 保存规则到文件，喂几条 spool 事件，核对过滤统计与包裹上的"被丢掉的码"；
      3) 直接改规则文件（模拟现场手改），验证热加载后立即生效、不需要重启平台；
      4) 校验非法规则会被拒绝（优先级重复、正则语法错）。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-b2-rules.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\test-b2-rules.ps1 -SourceRuntime D:\dws\runtime -Port 8098
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = '',
    [int]$Port = 8098
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-b2-test' }

$platformExe = Join-Path $SourceRuntime 'platform\DwsEdge.Platform.exe'
if (!(Test-Path $platformExe)) { throw "找不到平台可执行文件：$platformExe（先跑一次 build.ps1）" }

$baseUrl = 'http://127.0.0.1:' + $Port
$results = New-Object System.Collections.Generic.List[object]

function Add-Check {
    param([string]$Name, $Expected, $Actual)
    $ok = ("$Expected" -eq "$Actual")
    $results.Add([pscustomobject]@{
        检查项 = $Name; 期望 = "$Expected"; 实际 = "$Actual"; 结果 = $(if ($ok) { 'PASS' } else { 'FAIL' })
    })
}

function Stop-Platform {
    Get-Process -Name 'DwsEdge.Platform' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like ($WorkDir + '*') } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

function Start-Platform {
    $exe = Join-Path $WorkDir 'platform\DwsEdge.Platform.exe'
    $log = Join-Path $WorkDir 'logs\b2-test-platform.log'
    Start-Process -FilePath $exe -ArgumentList @('--urls', $baseUrl) `
        -WorkingDirectory (Join-Path $WorkDir 'platform') -WindowStyle Hidden `
        -RedirectStandardOutput $log -RedirectStandardError ($log + '.err') | Out-Null

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

function Post-Json {
    param([string]$Path, $Body)
    $json = $Body | ConvertTo-Json -Depth 8 -Compress
    return Invoke-RestMethod -Uri ($baseUrl + $Path) -Method POST -ContentType 'application/json; charset=utf-8' -Body $json -TimeoutSec 20
}

<#
    取顶层是数组的接口。注意：PowerShell 5.1 的 Invoke-RestMethod 处理"顶层 JSON 数组"时
    结果不稳定（可能把整个数组塞进一个对象里，字段变成数组），所以这里统一用
    Invoke-WebRequest + ConvertFrom-Json。
#>
function Get-Array {
    param([string]$Path)
    $json = (Invoke-WebRequest -Uri ($baseUrl + $Path) -UseBasicParsing -TimeoutSec 15).Content
    if ([string]::IsNullOrWhiteSpace($json)) { return @() }
    return @($json | ConvertFrom-Json)
}

function New-ParcelEvent {
    param([long]$EventId, [string]$TraceId, [string]$Stage, [string]$Device, [long]$CapturedAtMs, [string[]]$Codes, [int]$Weight)
    $codeItems = @()
    foreach ($c in $Codes) { $codeItems += @{ value = $c; kind = '1d'; position = 'top' } }
    return (@{
        schemaVersion = 1; type = 'parcel'; eventId = $EventId; providerId = 'test'; deviceId = $Device;
        stage = $Stage; capturedAtMs = $CapturedAtMs; receivedAtMs = $CapturedAtMs; traceId = $TraceId;
        stagedResult = $true; weightGrams = $Weight; lengthMm = 0; widthMm = 0; heightMm = 0; volumeMm3 = 0;
        codes = $codeItems; images = @()
    } | ConvertTo-Json -Compress -Depth 6)
}

# ---------------------------------------------------------------- 组合规则
function New-TestRuleSet {
    return @{
        defaultAction = 'drop'      # 没有规则命中就丢弃 —— 组合规则最常见的用法
        ignoreCase = $true
        rules = @(
            @{ name = '拉黑单号'; priority = 8;  enabled = $true; action = 'drop'; blacklist = @('SF0000000000') }
            @{ name = '测试码';   priority = 5;  enabled = $true; action = 'drop'; prefix = 'TEST' }
            @{ name = 'SF 运单';  priority = 10; enabled = $true; action = 'keep'; prefix = 'SF'; minLength = 12; maxLength = 14 }
            @{ name = '京东单号'; priority = 20; enabled = $true; action = 'keep'; regex = '^JD\d{10}$' }
            @{ name = '噪声码';   priority = 30; enabled = $true; action = 'drop'; whitelist = @('*NOISE*') }
        )
    }
}

$testCodes = @('SF123456789012', 'SF0000000000', 'TEST0001', 'JD1234567890', 'YT1234567890123', 'XXNOISE99', 'SF123')

Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
Stop-Platform

if (Test-Path $WorkDir) {
    $backup = "$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Move-Item -LiteralPath $WorkDir -Destination $backup -Force
    Write-Host "旧目录已挪走：$backup" -ForegroundColor DarkGray
}

robocopy $SourceRuntime $WorkDir /E /XD spool data images logs /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $WorkDir 'spool'), (Join-Path $WorkDir 'data'), (Join-Path $WorkDir 'images'), (Join-Path $WorkDir 'logs') | Out-Null

Start-Platform
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'b9-auth-helper.ps1')
$null = Enable-TestAuth -WorkDir $WorkDir   # B9：管理接口要凭据，脚本用服务令牌

try {
# ---------------------------------------------------------------- 1) 规则测试接口（未配置规则时：全部保留）
Write-Host "`n=== 1) 默认状态：没有规则时不做任何过滤 ===" -ForegroundColor Cyan
$empty = Post-Json -Path '/api/rules/test' -Body @{ codes = $testCodes; ruleset = @{ defaultAction = 'keep'; ignoreCase = $true; rules = @() } }
Add-Check '空规则集：保留数' 7 $empty.kept
Add-Check '空规则集：丢弃数' 0 $empty.dropped

# ---------------------------------------------------------------- 2) 组合规则测试
Write-Host "`n=== 2) 组合规则试跑（长度+前缀+正则+黑白名单+优先级）===" -ForegroundColor Cyan
$ruleset = New-TestRuleSet
$test = Post-Json -Path '/api/rules/test' -Body @{ codes = $testCodes; ruleset = $ruleset }
Add-Check '保留数' 2 $test.kept
Add-Check '丢弃数' 5 $test.dropped

$expect = @{
    'SF123456789012' = @('True', 'SF 运单')
    'SF0000000000'   = @('False', '拉黑单号')
    'TEST0001'       = @('False', '测试码')
    'JD1234567890'   = @('True', '京东单号')
    'YT1234567890123' = @('False', '')
    'XXNOISE99'      = @('False', '噪声码')
    'SF123'          = @('False', '')
}
foreach ($code in $testCodes) {
    $decision = $test.decisions | Where-Object { $_.code -eq $code }
    $want = $expect[$code]
    Add-Check ("判定 " + $code) ($want[0]) $decision.kept
    Add-Check ("  命中的规则 " + $code) ($want[1]) $decision.matchedRule
}

# ---------------------------------------------------------------- 3) 保存规则 + 实际过滤
Write-Host "`n=== 3) 保存规则并实际过滤事件 ===" -ForegroundColor Cyan
$saved = Post-Json -Path '/api/rules' -Body $ruleset
Add-Check '保存成功' 'True' $saved.ok
Add-Check '生效的规则条数' 5 $saved.enabledCount

$current = Invoke-RestMethod -Uri ($baseUrl + '/api/rules') -TimeoutSec 10
Add-Check '重新读取的启用规则数' 5 $current.enabledCount
Add-Check '默认动作' 'drop' $current.defaultAction

$day = (Get-Date).ToString('yyyyMMdd')
$spoolFile = Join-Path $WorkDir ("spool\events-" + $day + ".jsonl")
$lines = New-Object System.Collections.Generic.List[string]
# A：一个保留 + 一个丢弃
$lines.Add((New-ParcelEvent -EventId 101 -TraceId 'A' -Stage 'enriched' -Device 'cam' -CapturedAtMs 1789667000000 -Codes @('SF123456789012','TEST0001') -Weight 100))
# B：全被丢弃 → 变成无码包裹
$lines.Add((New-ParcelEvent -EventId 102 -TraceId 'B' -Stage 'enriched' -Device 'cam' -CapturedAtMs 1789667001000 -Codes @('TEST0002') -Weight 200))
# C：黑名单命中（优先级高于 SF 通用规则）+ 一个正常码
$lines.Add((New-ParcelEvent -EventId 103 -TraceId 'C' -Stage 'enriched' -Device 'cam' -CapturedAtMs 1789667002000 -Codes @('SF0000000000','JD1234567890') -Weight 300))
[System.IO.File]::WriteAllLines($spoolFile, $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("已写入 spool " + $lines.Count + " 行") -ForegroundColor DarkGray

Stop-Platform
Start-Platform
# 等平台把 3 行事件消费完再断言：固定 sleep 3 秒在并发跑回归或磁盘忙时会偶发少 1 条（实测踩过）
$stats = $null
$deadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $deadline) {
    $stats = Invoke-RestMethod -Uri ($baseUrl + '/api/stats') -TimeoutSec 10
    if ($stats.parcels -ge 3) { break }
    Start-Sleep -Milliseconds 400
}
Add-Check '被丢弃的条码数' 3 $stats.filteredCodes
Add-Check '因过滤变成无码的包裹数' 1 $stats.filteredToNoread
Add-Check '包裹数' 3 $stats.parcels
Add-Check '无码包裹数' 1 $stats.noread
Add-Check '统计里的规则条数' 5 $stats.ruleCount

$parcels = Get-Array -Path '/api/parcels?limit=20'
$parcelA = $parcels | Where-Object { $_.traceId -eq 'A' }
$parcelB = $parcels | Where-Object { $_.traceId -eq 'B' }
$parcelC = $parcels | Where-Object { $_.traceId -eq 'C' }
Add-Check 'A 保留下来的码' 'SF123456789012' ($parcelA.codes -join ',')
Add-Check 'A 被丢掉的码' 'TEST0001' (($parcelA.filteredCodes | ForEach-Object { $_.code }) -join ',')
Add-Check 'A 丢掉原因里的规则名' '测试码' (($parcelA.filteredCodes | ForEach-Object { $_.rule }) -join ',')
Add-Check 'B 没有可用条码（无码包裹）' '' ($parcelB.codes -join ',')
Add-Check 'C 保留京东单号' 'JD1234567890' ($parcelC.codes -join ',')
Add-Check 'C 丢掉黑名单单号' 'SF0000000000' (($parcelC.filteredCodes | ForEach-Object { $_.code }) -join ',')

$filtered = Get-Array -Path '/api/rules/filtered?limit=10'
Add-Check '最近被丢弃的条码条数' 3 $filtered.Count

# ---------------------------------------------------------------- 4) 改文件热加载
Write-Host "`n=== 4) 直接改规则文件（现场手改）→ 热加载立即生效 ===" -ForegroundColor Cyan
$ruleFile = Join-Path $WorkDir 'config\barcode-rules.json'
$onDisk = Get-Content $ruleFile -Encoding UTF8 -Raw | ConvertFrom-Json
$newRules = @($onDisk.rules) + @(@{ name = '临时禁用 JD'; priority = 3; enabled = $true; action = 'drop'; prefix = 'JD' })
$onDisk.rules = $newRules
[System.IO.File]::WriteAllText($ruleFile, ($onDisk | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
Start-Sleep -Seconds 3

$afterReload = Invoke-RestMethod -Uri ($baseUrl + '/api/rules') -TimeoutSec 10
Add-Check '热加载后的启用规则数' 6 $afterReload.enabledCount

$lines2 = New-Object System.Collections.Generic.List[string]
$lines2.Add((New-ParcelEvent -EventId 104 -TraceId 'D' -Stage 'enriched' -Device 'cam' -CapturedAtMs 1789667003000 -Codes @('JD1234567890') -Weight 400))
[System.IO.File]::AppendAllLines($spoolFile, $lines2, (New-Object System.Text.UTF8Encoding($false)))
Start-Sleep -Seconds 4

$stats2 = Invoke-RestMethod -Uri ($baseUrl + '/api/stats') -TimeoutSec 10
Add-Check '热加载后新规则生效（又丢 1 个码）' 4 $stats2.filteredCodes
Add-Check '热加载后无码包裹数' 2 $stats2.noread

# ---------------------------------------------------------------- 5) 非法规则要被拒
Write-Host "`n=== 5) 非法规则要被拒绝 ===" -ForegroundColor Cyan
function Test-Reject {
    param([string]$Name, $Body)
    try {
        $null = Post-Json -Path '/api/rules' -Body $Body
        Add-Check $Name '被拒绝' '被接受（不符合预期）'
    }
    catch {
        Add-Check $Name '被拒绝' '被拒绝'
    }
}

Test-Reject -Name '优先级重复' -Body @{
    defaultAction = 'keep'; ignoreCase = $true
    rules = @(
        @{ name = 'A'; priority = 10; enabled = $true; action = 'keep'; prefix = 'SF' }
        @{ name = 'B'; priority = 10; enabled = $true; action = 'drop'; prefix = 'YT' }
    )
}
Test-Reject -Name '正则语法错误' -Body @{
    defaultAction = 'keep'; ignoreCase = $true
    rules = @(@{ name = '坏正则'; priority = 10; enabled = $true; action = 'drop'; regex = '^JD(\d{10}$' })
}
Test-Reject -Name '动作取值非法' -Body @{
    defaultAction = 'keep'; ignoreCase = $true
    rules = @(@{ name = '动作错'; priority = 10; enabled = $true; action = 'remove'; prefix = 'SF' })
}

}
finally {
    # 无论如何都要把测试用的平台进程收掉（否则它会一直占着端口和日志文件）
    Stop-Platform
}

# ---------------------------------------------------------------- 汇总
Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('B2 回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    exit 0
}

Write-Host ('B2 回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
