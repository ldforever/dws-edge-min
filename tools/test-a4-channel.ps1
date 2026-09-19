<#
    A4 回归测试：宿主【运行中】的命令通道（命名管道）。

    验收点：宿主跑着的时候也能软触发/补码/查状态 —— 不用停宿主、也不再起第二个
    宿主进程去抢相机（老做法会得到 3001 相机被占用）。

    用 simulator provider 跑（不需要相机与加密狗），逐项验证：
      1) 宿主启动后命令通道确实起来了（日志里有"命令通道已开启"）；
      2) host-command.ps1 在宿主运行中走通道（输出带 [命令通道] 宿主在跑），退出码 0；
      3) --soft-trigger 由【正在跑的】宿主执行：宿主日志里出现
         "[命令通道执行 soft-trigger → 退出码 0"，而且真的产出了包裹事件；
      4) --recode 走通道补码成功；
      5) 宿主停掉后通道自动退回"新起进程"的老办法（不会卡住、退出码仍然正确）。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-a4-channel.ps1
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-a4-channel' }
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

# 模拟器：不需要相机与加密狗，而且软触发一定会产出一个包裹
$gatewayText = [System.IO.File]::ReadAllText($gateway)
$gatewayText = [regex]::Replace($gatewayText, '(?m)^provider=.*$', 'provider=simulator')
[System.IO.File]::WriteAllText($gateway, $gatewayText, $utf8NoBom)

# 软触发模式（triggerMode=2），通道里的 soft-trigger 才不会被拒
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptDir 'set-trigger-mode.ps1') `
    -Mode 'soft' -RuntimeDir $WorkDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw "切换触发模式失败" }

function Get-LogText {
    if (!(Test-Path $hostLog)) { return '' }
    try {
        $fs = New-Object System.IO.FileStream($hostLog, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
        $text = $sr.ReadToEnd()
        $sr.Close(); $fs.Close()
        return $text
    }
    catch { return '' }
}

function Get-ParcelEventCount {
    if (!(Test-Path $spoolFile)) { return 0 }
    # 宿主一直开着这个文件写，必须共享读；直接 ReadAllLines 会撞 IOException
    $text = ''
    try {
        $fs = New-Object System.IO.FileStream($spoolFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
        $text = $sr.ReadToEnd()
        $sr.Close(); $fs.Close()
    }
    catch { return 0 }

    $count = 0
    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match '"type":"parcel"') { $count++ }
    }
    return $count
}

# 调包装脚本，拿退出码 + 输出
function Invoke-CommandScript {
    param([string[]]$ScriptArgs)
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptDir 'host-command.ps1') `
        @ScriptArgs -RuntimeDir $WorkDir 2>&1 | Out-String
    return @{ exitCode = $LASTEXITCODE; output = $out }
}

# ---------------------------------------------------------------- 1) 启动常驻宿主
Write-Host "`n=== 1) 启动常驻宿主（模拟器 provider），等命令通道就绪 ===" -ForegroundColor Cyan
$hostOut = Join-Path $WorkDir 'logs\host-console.out'
$hostErr = Join-Path $WorkDir 'logs\host-console.err'
$hostProc = Start-Process -FilePath $hostExe -WorkingDirectory $WorkDir `
    -ArgumentList @('--duration', '90') -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $hostOut -RedirectStandardError $hostErr

$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    if ((Get-LogText) -match '命令通道已开启') { $ready = $true; break }
    if ($hostProc.HasExited) { break }
}
Add-Check '常驻宿主起来了（进程还活着）' 'False' $hostProc.HasExited
Add-Check '日志里出现"命令通道已开启"' 'True' $ready
Add-Check '日志里带管道名' 'True' ((Get-LogText) -match 'dws-edge-host-')
Add-Check '通道就绪时宿主仍在跑（不是一次性进程）' 'False' $hostProc.HasExited

# ---------------------------------------------------------------- 2) 运行中查状态
Write-Host "`n=== 2) 宿主机运行中用 host-command.ps1 -Status（应走常驻通道）===" -ForegroundColor Cyan
$status = Invoke-CommandScript @('-Status')
Add-Check '运行中 -Status 退出码 0' 0 $status.exitCode
Add-Check '确实走了常驻通道（输出带 [命令通道] 宿主在跑）' 'True' ($status.output -match '\[命令通道\] 宿主在跑')
Add-Check '通道返回了 provider=simulator' 'True' ($status.output -match 'provider=simulator')
Add-Check '此时宿主进程仍然只有一个（没被命令顶掉）' 'False' $hostProc.HasExited

# ---------------------------------------------------------------- 3) 运行中软触发
Write-Host "`n=== 3) 宿主机运行中软触发一次（应产出包裹）===" -ForegroundColor Cyan
$before = Get-ParcelEventCount
$trigger = Invoke-CommandScript @('-SoftTrigger')
Add-Check '运行中 -SoftTrigger 退出码 0' 0 $trigger.exitCode
Add-Check '确实走了常驻通道' 'True' ($trigger.output -match '\[命令通道\] 宿主在跑')
Add-Check '输出里有"软触发成功"' 'True' ($trigger.output -match '软触发成功')
Add-Check '宿主进程全程没退出（是同一个宿主执行的）' 'False' $hostProc.HasExited

$logText = Get-LogText
Add-Check '宿主日志里有"命令通道执行 soft-trigger → 退出码 0"' 'True' ($logText -match '命令通道执行 soft-trigger → 退出码 0')
Add-Check '宿主日志里有 [cmd] 软触发成功（命令真的执行了）' 'True' ($logText -match '\[cmd\] 软触发成功（返回 0）')

Start-Sleep -Seconds 3
$after = Get-ParcelEventCount
Add-Check '软触发真的产出了包裹事件（检测 + 补全共 2 条）' ($before + 2) $after

# ---------------------------------------------------------------- 4) 运行中补码
Write-Host "`n=== 4) 宿主机运行中人工补码（走通道）===" -ForegroundColor Cyan
$code = 'SF888000000001'
$recode = Invoke-CommandScript @('-Recode', '-Code', $code)
Add-Check '运行中 -Recode 退出码 0' 0 $recode.exitCode
Add-Check '确实走了常驻通道' 'True' ($recode.output -match '\[命令通道\] 宿主在跑')
Add-Check '输出里有"补码成功"' 'True' ($recode.output -match '补码成功')
Add-Check '宿主日志里记下了补的条码' 'True' ((Get-LogText) -match ('\[cmd\] 补码成功：' + $code))
Add-Check '宿主进程依然没退出' 'False' $hostProc.HasExited

# ---------------------------------------------------------------- 5) 宿主停掉后退回老办法
Write-Host "`n=== 5) 宿主停掉后：通道自动退回【新起进程】执行 ===" -ForegroundColor Cyan
try { $hostProc.Kill(); $hostProc.WaitForExit(10000) | Out-Null } catch { }
Start-Sleep -Seconds 2

$afterStop = Invoke-CommandScript @('-Status')
Add-Check '宿主停掉后 -Status 仍然退出码 0（退回了新起进程的老办法）' 0 $afterStop.exitCode
Add-Check '输出里说明了通道连不上、走老办法' 'True' ($afterStop.output -match '连不上')
Add-Check '老办法也给出了命令结论' 'True' ($afterStop.output -match '命令结论')

Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('A4 命令通道回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    exit 0
}
Write-Host ('A4 命令通道回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
