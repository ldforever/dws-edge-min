<#
    启动重试回归：SDK 起不来（没有相机 / 没插加密狗）时，宿主应该"保持存活并重试"，
    而不是像以前那样直接退出。

    怎么造出"SDK 起不来"：把清单里的相机 IP 改成一个不存在的地址（172.20.10.11），
    大华 SDK 会返回 3000（相机数与配置不符 / 没有相机连上）。加密狗没插时会是 2200，
    两个都算"值得重试"，所以断言里两个都接受。

    验证点：
      1) retryEnabled=true：宿主进程在 12 秒后仍然活着（旧行为是几秒内退出）；
      2) 宿主机日志里出现"第 N 次启动失败（SDK 返回 …）→ … 秒后重试"；
      3) logs\host-status.json 里 state=retrying、attempt>=1、code ∈ {2200,3000}；
      4) retryEnabled=false：退回老行为 —— 宿主在 30 秒内退出（退出码 2），证明开关真的生效。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-startup-retry.ps1
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-retry-test' }

$exe = Join-Path $SourceRuntime 'DwsEdge.Host.exe'
if (!(Test-Path $exe)) { throw "找不到采集宿主：$exe（先跑一次 build.ps1）" }

$results = New-Object System.Collections.Generic.List[object]
function Add-Check {
    param([string]$Name, $Expected, $Actual)
    $ok = ("$Expected" -eq "$Actual")
    $results.Add([pscustomobject]@{ 检查项 = $Name; 期望 = "$Expected"; 实际 = "$Actual"; 结果 = $(if ($ok) { 'PASS' } else { 'FAIL' }) }) | Out-Null
}

Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
if (Test-Path $WorkDir) { Move-Item -LiteralPath $WorkDir -Destination ("$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force }
robocopy $SourceRuntime $WorkDir /E /XD spool data images logs Log cache /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path "$WorkDir\spool", "$WorkDir\data", "$WorkDir\images", "$WorkDir\logs" | Out-Null

$cfgPath = Join-Path $WorkDir 'Cfg\LogisticsBase.cfg'
$gateway = Join-Path $WorkDir 'config\gateway.ini'
$hostLog = Join-Path $WorkDir ('logs\host-' + (Get-Date).ToString('yyyyMMdd') + '.log')
$statusFile = Join-Path $WorkDir 'logs\host-status.json'

# 1) 清单指向一个不存在的相机 IP（SDK 必然 3000），保证 provider=dahua-dws
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
# 用现成的清单生成脚本改：它会同时把 num 改成 1、清理重复声明（手写正则很容易造出"重复声明"，
# 那种错误属于配置错误，不该重试，会干扰本测试）
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $WorkDir 'tools\make-camera-cfg.ps1') `
    -Cameras 'ip=172.20.10.11' -RuntimeDir $WorkDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw '改相机清单失败' }

$gwText = [System.IO.File]::ReadAllText($gateway)
$gwText = [regex]::Replace($gwText, '(?m)^provider\s*=.*$', 'provider=dahua-dws')
[System.IO.File]::WriteAllText($gateway, $gwText, $utf8NoBom)

function Set-Retry {
    param([bool]$Enabled, [int]$IntervalSeconds, [int]$MaxMinutes)
    $text = [System.IO.File]::ReadAllText($gateway)
    $section = "[startup]`r`nretryEnabled=$($Enabled.ToString().ToLower())`r`nretryIntervalSeconds=$IntervalSeconds`r`nretryMaxMinutes=$MaxMinutes`r`nretryOn=2200,3000,3001`r`n"
    if ($text -match '(?m)^\[startup\]') {
        $text = [regex]::Replace($text, '(?ms)^\[startup\].*?(?=^\[|\z)', $section)
    }
    else {
        $text = $text.TrimEnd() + "`r`n`r`n" + $section
    }
    [System.IO.File]::WriteAllText($gateway, $text, $utf8NoBom)
}

function Get-LogText {
    if (!(Test-Path $hostLog)) { return '' }
    try {
        $fs = New-Object System.IO.FileStream($hostLog, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
        $text = $sr.ReadToEnd(); $sr.Close(); $fs.Close()
        return $text
    }
    catch { return '' }
}

function Start-HostTest {
    param([int]$WaitSeconds)
    $out = Join-Path $WorkDir 'logs\retry-test.out'
    $p = Start-Process -FilePath (Join-Path $WorkDir 'DwsEdge.Host.exe') -WorkingDirectory $WorkDir `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput $out -RedirectStandardError ($out + '.err')
    Start-Sleep -Seconds $WaitSeconds
    return $p
}

# ---------------------------------------------------------------- 1) 开启重试：宿主应该活着
Write-Host "`n=== 1) retryEnabled=true：SDK 起不来也不退出，等待重试 ===" -ForegroundColor Cyan
Set-Retry -Enabled $true -IntervalSeconds 2 -MaxMinutes 5
$hostProc = Start-HostTest -WaitSeconds 12

Add-Check '重试模式下宿主仍然存活（旧行为是直接退出）' 'False' $hostProc.HasExited
$logText = Get-LogText
Add-Check '日志里有"第 N 次启动失败（SDK 返回 …）"' 'True' ($logText -match '第 \d+ 次启动失败（SDK 返回 \d+')
Add-Check '日志里写明了几秒后重试' 'True' ($logText -match '秒后重试')
Add-Check '日志里给了人话解释（没相机/没加密狗/被占用）' 'True' (($logText -match '相机数与配置不符') -or ($logText -match '没检测到加密狗') -or ($logText -match '相机被占用'))

$state = $null; $attempt = 0; $code = 0
if (Test-Path $statusFile) {
    $json = Get-Content $statusFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $state = $json.state; $attempt = [int]$json.attempt; $code = [int]$json.code
}
Add-Check '写了 logs\host-status.json' 'True' (Test-Path $statusFile)
Add-Check '状态为 retrying' 'retrying' $state
Add-Check '重试次数 >= 1' 'True' ($attempt -ge 1)
Add-Check '返回码是 2200/3000 之一' 'True' (($code -eq 2200) -or ($code -eq 3000))

try { $hostProc.Kill(); $hostProc.WaitForExit(8000) | Out-Null } catch { }
Start-Sleep -Seconds 1

# ---------------------------------------------------------------- 2) 关闭重试：退回老行为
Write-Host "`n=== 2) retryEnabled=false：退回老行为（启动失败即退出）===" -ForegroundColor Cyan
if (Test-Path $hostLog) { Move-Item -LiteralPath $hostLog -Destination ($hostLog + '.prev') -Force }
if (Test-Path $statusFile) { Move-Item -LiteralPath $statusFile -Destination ($statusFile + '.prev') -Force }
Set-Retry -Enabled $false -IntervalSeconds 2 -MaxMinutes 5
$hostProc2 = Start-HostTest -WaitSeconds 25
Add-Check '关掉重试后宿主在 25 秒内退出' 'True' $hostProc2.HasExited
if ($host2.HasExited) { Add-Check '退出码 2（provider 启动失败）' 2 $host2.ExitCode }
else { try { $host2.Kill() } catch { } }

Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('启动重试回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    exit 0
}
Write-Host ('启动重试回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
