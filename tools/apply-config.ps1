<#
    一键应用大华 SDK 配置：改配置 → 重启 SDK 校验 → 失败自动回滚。

    用法：
        # 只改触发模式（soft / hard / free）
        powershell -ExecutionPolicy Bypass -File .\tools\apply-config.ps1 -TriggerMode hard

        # 只改相机清单（清单文件支持 ip=...,pos=top 写法）
        powershell -ExecutionPolicy Bypass -File .\tools\apply-config.ps1 -CameraList .\config\cameras-17.example.txt

        # 两个一起改
        powershell -ExecutionPolicy Bypass -File .\tools\apply-config.ps1 -TriggerMode soft -CameraList .\config\cameras-17.example.txt

        # 只写配置、不做校验
        ... -SkipVerify

    流程：
        1) 备份当前 cfg 为 LogisticsBase.cfg.rollback-<时间戳>（回滚点）
        2) 调用 set-trigger-mode.ps1 / make-camera-cfg.ps1 应用配置（它们各自也会写备份）
        3) 用 DwsEdge.Host.exe --verify-config 启动 SDK 并回读校验
        4) 校验失败 → 自动把回滚点写回，并再校验一次确认回滚成功
#>
param(
    [string]$RuntimeDir = '',
    [ValidateSet('', 'soft', 'hard', 'free')][string]$TriggerMode = '',
    [string]$CameraList = '',
    [string[]]$Cameras = @(),
    [switch]$SkipVerify,
    [switch]$StopHost,
    [switch]$RestartHost,
    [int]$VerifyTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($RuntimeDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $RuntimeDir = Join-Path (Split-Path -Parent $scriptDir) 'runtime'
}

$cfgPath = Join-Path $RuntimeDir 'Cfg\LogisticsBase.cfg'
$hostExe = Join-Path $RuntimeDir 'DwsEdge.Host.exe'
$toolsDir = Join-Path (Split-Path -Parent $RuntimeDir) 'tools'
$setTrigger = Join-Path $toolsDir 'set-trigger-mode.ps1'
$makeCamera = Join-Path $toolsDir 'make-camera-cfg.ps1'

if (!(Test-Path $cfgPath)) { throw "找不到配置文件：$cfgPath" }
if (!(Test-Path $hostExe)) { throw "找不到采集宿主：$hostExe" }
if ([string]::IsNullOrEmpty($TriggerMode) -and [string]::IsNullOrEmpty($CameraList) -and $Cameras.Count -eq 0) {
    throw "至少指定一个要改的项：-TriggerMode / -CameraList / -Cameras"
}

$gb2312 = [System.Text.Encoding]::GetEncoding(936)

function Read-CfgState {
    param([string]$Path)

    $text = [System.IO.File]::ReadAllText($Path, $gb2312)
    $imageAcq = [regex]::Match($text, '<ImageAcq\b[^>]*>')
    $readCode = [regex]::Match($text, '<ReadCodeMode\b[^>]*>')
    $cameras = [regex]::Matches($text, '<Camera\b[^>]*>')

    $mode = '?'
    if ($imageAcq.Success -and $imageAcq.Value -match 'mode="([^"]*)"') { $mode = $Matches[1] }

    $num = '?'
    if ($imageAcq.Success -and $imageAcq.Value -match 'num="([^"]*)"') { $num = $Matches[1] }

    $trigger = '?'
    if ($readCode.Success -and $readCode.Value -match 'triggerMode="([^"]*)"') { $trigger = $Matches[1] }

    $enabled = 0
    foreach ($c in $cameras) { if ($c.Value -match 'enable="1"') { $enabled++ } }

    return [pscustomobject]@{
        Mode        = $mode
        Num         = $num
        TriggerMode = $trigger
        Enabled     = $enabled
    }
}

$before = Read-CfgState -Path $cfgPath

# 0) 运行中的采集宿主：校验会再起一个实例去初始化 SDK，会互相抢相机
$running = @(Get-Process -Name 'DwsEdge.Host' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    if (!$StopHost) {
        $pids = (($running | ForEach-Object { $_.Id }) -join ', ')
        throw ("检测到采集宿主正在运行（PID: " + $pids + "）。请先停止它，或者加 -StopHost 让脚本自动停止后再校验。")
    }
    Write-Host "停止运行中的采集宿主…" -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# 1) 回滚点
$rollback = "$cfgPath.rollback-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
Copy-Item -LiteralPath $cfgPath -Destination $rollback -Force
Write-Host "回滚点：$rollback" -ForegroundColor DarkGray

# 2) 应用配置
# 改动由 set-trigger-mode.ps1 / make-camera-cfg.ps1 实际写入，它们自己也会写 .bak 备份；
# 这一步失败（清单格式错、cfg 结构不认识…）也要回滚，不能把半套配置留在现场。
try {
    if (![string]::IsNullOrEmpty($TriggerMode)) {
        Write-Host "应用触发模式：$TriggerMode" -ForegroundColor Cyan
        & $setTrigger -Mode $TriggerMode -RuntimeDir $RuntimeDir
    }

    if (![string]::IsNullOrEmpty($CameraList) -or $Cameras.Count -gt 0) {
        Write-Host "应用相机清单…" -ForegroundColor Cyan
        # 注意：这里必须用"哈希表 splat"，不能用数组 splat——
        # PowerShell 5.1 里数组 splat 的元素会按位置绑定，"-CameraList" 会被当成 -CfgPath 的值。
        $makeParams = @{ RuntimeDir = $RuntimeDir }
        if (![string]::IsNullOrEmpty($CameraList)) { $makeParams['CameraList'] = $CameraList }
        if ($Cameras.Count -gt 0) { $makeParams['Cameras'] = $Cameras }
        & $makeCamera @makeParams
    }
}
catch {
    Write-Host ""
    Write-Host ("应用配置失败：" + $_.Exception.Message) -ForegroundColor Red
    Write-Host "正在回滚到应用前的配置…" -ForegroundColor Yellow
    Copy-Item -LiteralPath $rollback -Destination $cfgPath -Force
    Write-Host ("已回滚（回滚点：" + $rollback + "）。请修正参数后重试。") -ForegroundColor Green
    exit 1
}

$after = Read-CfgState -Path $cfgPath
Write-Host ""
Write-Host "变更摘要（改前 → 改后）" -ForegroundColor Cyan
Write-Host ("  mode        : {0} → {1}" -f $before.Mode, $after.Mode)
Write-Host ("  num         : {0} → {1}" -f $before.Num, $after.Num)
Write-Host ("  enable 相机 : {0} → {1}" -f $before.Enabled, $after.Enabled)
Write-Host ("  triggerMode : {0} → {1}" -f $before.TriggerMode, $after.TriggerMode)

if ($SkipVerify) {
    Write-Host ""
    Write-Host "已写入配置（-SkipVerify：未做重启校验）。重启采集宿主后生效。" -ForegroundColor Yellow
    return
}

# 3) 重启校验：用 --verify-config 起一次 SDK 并回读配置
function Invoke-Verify {
    param([string]$Tag)

    $stamp = Get-Date -Format 'HHmmssfff'
    $out = Join-Path $env:TEMP ("dws-verify-" + $Tag + "-" + $stamp + ".out.txt")
    $err = Join-Path $env:TEMP ("dws-verify-" + $Tag + "-" + $stamp + ".err.txt")

    # 用 .NET Process 直接启动：Start-Process -PassThru 在脚本里可能拿不到 ExitCode。
    # 先异步读取两个输出流，再 WaitForExit(超时)，避免管道写满导致死锁。
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $hostExe
    $psi.Arguments = '--verify-config'
    $psi.WorkingDirectory = $RuntimeDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    [void]$proc.Start()
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()

    $finished = $proc.WaitForExit($VerifyTimeoutSeconds * 1000)
    if (!$finished) {
        try { $proc.Kill() } catch { }
    }

    $stdout = ''
    $stderr = ''
    try { $stdout = $outTask.Result } catch { }
    try { $stderr = $errTask.Result } catch { }

    [System.IO.File]::WriteAllText($out, $stdout, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText($err, $stderr, (New-Object System.Text.UTF8Encoding($false)))

    if ($finished) {
        $script:VerifyExit = [int]$proc.ExitCode
    }
    else {
        Write-Host "校验超时（> $VerifyTimeoutSeconds 秒）" -ForegroundColor Red
        $script:VerifyExit = -1
    }
    $script:VerifyOutput = $out

    if (Test-Path $out) {
        Get-Content -Encoding UTF8 $out | Where-Object { $_ -match '\[verify\]|校验：|校验不通过|配置校验' } | ForEach-Object { Write-Host ("  " + $_) }
    }
    return $out
}

Write-Host ""
Write-Host "启动 SDK 校验配置…" -ForegroundColor Cyan
$verifyOut = Invoke-Verify -Tag 'apply'
Write-Host ("  校验退出码：" + $script:VerifyExit) -ForegroundColor DarkGray

if ($script:VerifyExit -eq 0) {
    Write-Host ""
    Write-Host "配置已生效：SDK 启动成功且回读校验通过。" -ForegroundColor Green
    if ($RestartHost) {
        Start-Process -FilePath $hostExe -WorkingDirectory $RuntimeDir | Out-Null
        Write-Host "已重新启动采集宿主。" -ForegroundColor Green
    }
    elseif ($running.Count -gt 0) {
        Write-Host "提示：采集宿主已被停止，请用 run.ps1 或服务方式重新启动（或下次加 -RestartHost）。" -ForegroundColor Yellow
    }
    exit 0
}

# 4) 校验失败 → 自动回滚
Write-Host ""
Write-Host "校验未通过，正在回滚配置…" -ForegroundColor Yellow
Copy-Item -LiteralPath $rollback -Destination $cfgPath -Force

$recheckOut = Invoke-Verify -Tag 'rollback'
Write-Host ("  回滚后校验退出码：" + $script:VerifyExit) -ForegroundColor DarkGray

if ($script:VerifyExit -eq 0) {
    Write-Host "已回滚到应用前的配置，且回滚后校验通过。" -ForegroundColor Green
    Write-Host ("失败原因见上面的 校验不通过 明细以及日志：{0}" -f $verifyOut) -ForegroundColor Yellow
    exit 2
}

Write-Host "回滚后校验仍失败，请人工检查配置与设备状态。" -ForegroundColor Red
Write-Host ("  本次校验输出：{0}" -f $verifyOut) -ForegroundColor Red
Write-Host ("  回滚后输出  ：{0}" -f $recheckOut) -ForegroundColor Red
exit 3
