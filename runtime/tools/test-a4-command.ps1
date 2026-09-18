<#
    A4 回归测试：软触发与补码命令。

    验收点（需求原文）：命令返回码正确并写入日志；触发模式为软触发时生效。

    用 simulator provider 跑（不需要相机和加密狗），逐项验证：
      1) --command-status：能看到 provider 与当前触发模式；
      2) triggerMode=2（软触发）时 --soft-trigger 成功（退出码 0），并真的产出了包裹事件；
      3) triggerMode=1/0（硬触发/自由拉流）时 --soft-trigger 被拒（退出码 5），
         而且**一条事件都没产生**（证明命令真的没执行）；--force 可以跳过校验；
      4) --recode --code <条码> 补码成功；缺 --code 时退出码 1；
      5) 每条命令都写进 logs\host-*.log（命令、参数、结果、退出码都在）；
      6) 参数错误返回 1、帮助返回 0；
      7) 包装脚本 tools\host-command.ps1 能把退出码翻译成人话。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-a4-command.ps1
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-a4-test' }
if (![System.IO.Path]::IsPathRooted($SourceRuntime)) { $SourceRuntime = [System.IO.Path]::GetFullPath($SourceRuntime) }
if (![System.IO.Path]::IsPathRooted($WorkDir)) { $WorkDir = [System.IO.Path]::GetFullPath($WorkDir) }

$exe = Join-Path $SourceRuntime 'DwsEdge.Host.exe'
if (!(Test-Path $exe)) { throw "找不到采集宿主：$exe（先跑一次 build.ps1）" }

$results = New-Object System.Collections.Generic.List[object]
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Add-Check {
    param([string]$Name, $Expected, $Actual)
    $ok = ("$Expected" -eq "$Actual")
    $results.Add([pscustomobject]@{
        检查项 = $Name; 期望 = "$Expected"; 实际 = "$Actual"; 结果 = $(if ($ok) { 'PASS' } else { 'FAIL' })
    }) | Out-Null
}

Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
if (Test-Path $WorkDir) {
    Move-Item -LiteralPath $WorkDir -Destination ("$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force
}
robocopy $SourceRuntime $WorkDir /E /XD spool data images logs cache /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $WorkDir 'spool'), (Join-Path $WorkDir 'data'), (Join-Path $WorkDir 'images'), (Join-Path $WorkDir 'logs') | Out-Null

$hostExe = Join-Path $WorkDir 'DwsEdge.Host.exe'
$gateway = Join-Path $WorkDir 'config\gateway.ini'
$today = (Get-Date).ToString('yyyyMMdd')
$spoolFile = Join-Path $WorkDir ('spool\events-' + $today + '.jsonl')
$hostLog = Join-Path $WorkDir ('logs\host-' + $today + '.log')

# 用模拟器跑：不需要相机与加密狗
$gatewayText = [System.IO.File]::ReadAllText($gateway)
$gatewayText = [regex]::Replace($gatewayText, '(?m)^provider=.*$', 'provider=simulator')
[System.IO.File]::WriteAllText($gateway, $gatewayText, $utf8NoBom)

function Set-TriggerMode {
    param([string]$Mode)
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptDir 'set-trigger-mode.ps1') `
        -Mode $Mode -RuntimeDir $WorkDir | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "切换触发模式失败：$Mode" }
}

<#
    跑一次宿主命令并拿退出码。
    刻意用 Start-Process + 输出重定向 + 超时，而不是 "& exe ... | Out-String"：
    管道要等所有持有 stdout 的进程/线程关闭才算结束，宿主一旦没按命令模式退出就会把脚本挂死；
    这里的超时能把它变成一条明确的失败（timeout=true）。
#>
function Invoke-Host {
    param([string[]]$Arguments, [int]$TimeoutMs = 30000)
    # 直接用 .NET 的 Process：Start-Process -PassThru 拿到的对象在有些情况下读不到 ExitCode
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $hostExe
    $psi.WorkingDirectory = $WorkDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    # 用 Arguments 字符串而不是 ArgumentList：后者只在 .NET Core 及以上的 ProcessStartInfo 上有，
    # Windows PowerShell 5.1 跑这段会直接空引用。
    $psi.Arguments = (($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -join ' ')

    $process = [System.Diagnostics.Process]::Start($psi)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()

    if (!$process.WaitForExit($TimeoutMs)) {
        try { $process.Kill() } catch { }
        return @{ exitCode = -999; output = ($stdoutTask.Result + $stderrTask.Result); timeout = $true }
    }
    $text = $stdoutTask.Result + $stderrTask.Result
    return @{ exitCode = $process.ExitCode; output = $text; timeout = $false }
}

function Get-SpoolCount {
    if (!(Test-Path $spoolFile)) { return 0 }
    return @([System.IO.File]::ReadAllLines($spoolFile) | Where-Object { $_.Trim().Length -gt 0 }).Count
}

<# 只数"包裹事件"：命令模式下每次启动宿主都会写一条相机快照事件，按总行数断言会多算 #>
function Get-ParcelEventCount {
    if (!(Test-Path $spoolFile)) { return 0 }
    $count = 0
    foreach ($line in [System.IO.File]::ReadAllLines($spoolFile)) {
        if ($line -match '"type":"parcel"') { $count++ }
    }
    return $count
}

function Get-LogText {
    if (!(Test-Path $hostLog)) { return '' }
    return [System.IO.File]::ReadAllText($hostLog)
}

# ---------------------------------------------------------------- 1) 状态命令
Write-Host "`n=== 1) --command-status：看 provider 与触发模式 ===" -ForegroundColor Cyan
$status = Invoke-Host @('--command-status')
Add-Check '状态命令退出码 0' 0 $status.exitCode
Add-Check '状态命令是"执行完就退出"（没有超时）' 'False' $status.timeout
Add-Check '输出里有 provider=simulator' 'True' ($status.output -match 'provider=simulator')
Add-Check '输出里说支持软触发' 'True' ($status.output -match '支持软触发/补码=是')
Add-Check '输出里带触发模式' 'True' ($status.output -match '触发模式=')

# ---------------------------------------------------------------- 2) 软触发模式：成功
Write-Host "`n=== 2) triggerMode=2：软触发成功且真的出码 ===" -ForegroundColor Cyan
Set-TriggerMode -Mode 'soft'
$before = Get-ParcelEventCount
$logBefore = (Get-LogText).Length

$trigger = Invoke-Host @('--soft-trigger')
Add-Check '软触发退出码 0' 0 $trigger.exitCode
Add-Check '软触发命令执行完就退出' 'False' $trigger.timeout
Add-Check '输出里有"软触发成功"' 'True' ($trigger.output -match '软触发成功')
Start-Sleep -Seconds 2
$after = Get-ParcelEventCount
Add-Check '软触发真的产出了包裹事件（检测 + 补全共 2 条）' ($before + 2) $after

$logText = Get-LogText
Add-Check '日志文件被写入（命令执行后有新增）' 'True' ($logText.Length -gt $logBefore)
Add-Check '日志里有命令记录' 'True' ($logText -match '\[cmd\] 收到命令：soft-trigger')
Add-Check '日志里有命令结果与退出码' 'True' ($logText -match '\[cmd\] 软触发成功（返回 0）　退出码 0')

# ---------------------------------------------------------------- 3) 硬触发/自由拉流：被拒
Write-Host "`n=== 3) triggerMode=1/0：软触发被拒，且一条事件都不产生 ===" -ForegroundColor Cyan
foreach ($mode in @('hard', 'free')) {
    Set-TriggerMode -Mode $mode
    $countBefore = Get-ParcelEventCount
    $blocked = Invoke-Host @('--soft-trigger')
    Start-Sleep -Seconds 2
    $countAfter = Get-ParcelEventCount

    Add-Check ("触发模式 " + $mode + " 时软触发退出码 5") 5 $blocked.exitCode
    Add-Check ("触发模式 " + $mode + " 时命令正常退出（无超时）") 'False' $blocked.timeout
    Add-Check ("触发模式 " + $mode + " 时提示去改模式") 'True' (($blocked.output -match 'set-trigger-mode') -and ($blocked.output -match '软触发不会生效'))
    Add-Check ("触发模式 " + $mode + " 时没有产生事件（命令没执行）") $countBefore $countAfter
    Add-Check ("触发模式 " + $mode + " 的拒绝写进了日志") 'True' ((Get-LogText) -match '软触发不会生效')
}

# ---------------------------------------------------------------- 4) --force 跳过校验
Write-Host "`n=== 4) --force：跳过触发模式校验 ===" -ForegroundColor Cyan
Set-TriggerMode -Mode 'hard'
$forced = Invoke-Host @('--soft-trigger', '--force')
Add-Check '带 --force 时退出码 0' 0 $forced.exitCode
Add-Check '--force 命令执行完就退出' 'False' $forced.timeout
Add-Check '日志里标注了 --force' 'True' ((Get-LogText) -match '--force：跳过触发模式校验')

# ---------------------------------------------------------------- 5) 补码
Write-Host "`n=== 5) 人工补码 ===" -ForegroundColor Cyan
$code = 'SF999000000001'
$recode = Invoke-Host @('--recode', '--code', $code)
Add-Check '补码退出码 0' 0 $recode.exitCode
Add-Check '补码命令执行完就退出' 'False' $recode.timeout
Add-Check '输出里有"补码成功"' 'True' ($recode.output -match '补码成功')
Add-Check '日志里记下了补的条码' 'True' ((Get-LogText) -match ('\[cmd\] 补码成功：' + $code))

$recodeNoCode = Invoke-Host @('--recode')
Add-Check '补码不带 --code 退出码 1' 1 $recodeNoCode.exitCode
Add-Check '提示缺少 --code' 'True' ($recodeNoCode.output -match '缺少 --code')

# ---------------------------------------------------------------- 6) 参数与帮助
Write-Host "`n=== 6) 参数错误与帮助 ===" -ForegroundColor Cyan
$badArg = Invoke-Host @('--not-a-command')
Add-Check '未知参数退出码 1' 1 $badArg.exitCode
Add-Check '未知参数下宿主也立刻退出（不会误进常驻）' 'False' $badArg.timeout
$help = Invoke-Host @('--help')
Add-Check '帮助退出码 0' 0 $help.exitCode
Add-Check '帮助里有 A4 命令说明' 'True' (($help.output -match '--soft-trigger') -and ($help.output -match '退出码'))

# ---------------------------------------------------------------- 7) 包装脚本
Write-Host "`n=== 7) 包装脚本 host-command.ps1 ===" -ForegroundColor Cyan
$wrapStatus = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptDir 'host-command.ps1') `
    -Status -RuntimeDir $WorkDir 2>&1 | Out-String
Add-Check '包装脚本 -Status 成功' 0 $LASTEXITCODE
Add-Check '包装脚本输出结论' 'True' ($wrapStatus -match '命令结论')

$wrapTrigger = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptDir 'host-command.ps1') `
    -SoftTrigger -RuntimeDir $WorkDir 2>&1 | Out-String
Add-Check '硬触发模式下包装脚本 -SoftTrigger 退出码 5' 5 $LASTEXITCODE
Add-Check '包装脚本把"触发模式不允许"翻译成人话' 'True' ($wrapTrigger -match '触发模式不允许')

$wrapRecode = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptDir 'host-command.ps1') `
    -Recode -Code 'SF999000000002' -RuntimeDir $WorkDir 2>&1 | Out-String
Add-Check '包装脚本 -Recode 成功' 0 $LASTEXITCODE
Add-Check '包装脚本里补码成功' 'True' ($wrapRecode -match '补码成功')

Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('A4 回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    exit 0
}
Write-Host ('A4 回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
