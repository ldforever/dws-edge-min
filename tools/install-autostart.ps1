<#
    DWS 形态 A：用 Windows 计划任务做开机自启（采集宿主 + 业务平台）。

    两种触发方式：
      * onlogon（默认）：当前用户登录后启动 —— **有桌面会话**，大华 SDK 最稳；
      * onstart：开机就启动、以 SYSTEM 账户运行 —— 无人值守场景，但**必须先真机验证**
        "服务账户、无桌面会话"下 SDK 能否正常取流与识别加密狗。

    每个任务都配了"失败自动重启（每分钟，最多 3 次）"与"不限运行时长"。

    用法（**需要管理员权限的 PowerShell**）：
        powershell -ExecutionPolicy Bypass -File .\runtime\tools\install-autostart.ps1
        powershell -ExecutionPolicy Bypass -File .\runtime\tools\install-autostart.ps1 -Trigger onstart
        powershell -ExecutionPolicy Bypass -File .\runtime\tools\install-autostart.ps1 -Shell          # 套壳一体机：登录后直接起界面外壳
        powershell -ExecutionPolicy Bypass -File .\runtime\tools\install-autostart.ps1 -WhatIfOnly   # 只看会建什么
#>
param(
    [string]$RuntimeDir = '',
    [string]$TaskPrefix = 'DWS-Edge',
    [ValidateSet('onlogon', 'onstart')][string]$Trigger = 'onlogon',
    [switch]$Shell,                    # 用 DwsEdge.Shell.exe（界面外壳）自启：它自己会把宿主+平台拉起来
    [switch]$WhatIfOnly
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($RuntimeDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $root = Split-Path -Parent $scriptDir
    $RuntimeDir = if (Test-Path (Join-Path $root 'DwsEdge.Host.exe')) { $root } else { Join-Path $root 'runtime' }
}
$RuntimeDir = [System.IO.Path]::GetFullPath($RuntimeDir)

$hostExe = Join-Path $RuntimeDir 'DwsEdge.Host.exe'
$platformExe = Join-Path $RuntimeDir 'platform\DwsEdge.Platform.exe'
foreach ($exe in @($hostExe, $platformExe)) {
    if (!(Test-Path $exe)) { throw "找不到可执行文件：$exe（先确认 runtime 目录完整）" }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$isAdmin -and !$WhatIfOnly) {
    throw "注册计划任务需要管理员权限：请用'以管理员身份运行'的 PowerShell 重新执行"
}

<#
    只注册"一个"任务：由它调用 tools\start-all.ps1 把两个进程都隐藏启动（不弹黑窗口）。
    这样现场不会看到两个控制台窗口，关掉远程桌面也不会把进程带走；
    手动启停用 tools\start-all.ps1 / tools\stop-all.ps1。
#>
$startAll = Join-Path $RuntimeDir 'tools\start-all.ps1'
if (!(Test-Path $startAll)) { throw "找不到 $startAll（交付包 tools 目录不完整）" }

if ($Shell) {
    # 套壳一体机：登录后直接起界面外壳，外壳自己会拉起采集宿主 + 平台，
    # 现场看到的就是一个"桌面应用"窗口（无地址栏），而不是黑窗口 + 浏览器。
    $shellExe = Join-Path $RuntimeDir 'DwsEdge.Shell.exe'
    if (!(Test-Path $shellExe)) { throw "找不到界面外壳：$shellExe（先跑一次 build.ps1，或确认包里带了 DwsEdge.Shell.exe）" }
    $tasks = @(
        @{ name = $TaskPrefix; label = '界面外壳（内含采集宿主 + 业务平台）'; exe = $shellExe;
           work = $RuntimeDir; args = '' }
    )
} else {
    $tasks = @(
        @{ name = $TaskPrefix; label = '采集宿主 + 业务平台'; exe = 'powershell.exe';
           work = $RuntimeDir
           args = ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $startAll + '" -RuntimeDir "' + $RuntimeDir + '"') }
    )
}

Write-Host "============================================================"
Write-Host " DWS 开机自启（形态 A：计划任务）"
Write-Host " 运行时目录：$RuntimeDir"
Write-Host " 触发方式　：$Trigger$(if ($Trigger -eq 'onlogon') { '（用户登录后启动，有桌面会话）' } else { '（开机启动，以 SYSTEM 运行）' })"
Write-Host "============================================================"

foreach ($task in $tasks) {
    Write-Host ("  [" + $task.label + "] 任务名 " + $task.name + " → " + $task.exe) -ForegroundColor Cyan
    # 演练模式只打印，不碰计划任务服务（New-ScheduledTaskAction 本身就需要权限）
    if ($WhatIfOnly) {
        Write-Host ("      命令行：" + $task.exe + " " + $task.args) -ForegroundColor DarkGray
        Write-Host ("      工作目录：" + $task.work + "　（隐藏窗口启动，不弹黑窗口）") -ForegroundColor DarkGray
        Write-Host ("      触发：" + $Trigger + "　运行账户：" +
            $(if ($Trigger -eq 'onlogon') { $identity.Name + '（交互式登录，有桌面会话）' } else { 'SYSTEM（开机即启，无桌面会话）' }) +
            "　失败重启：每分钟最多 3 次；不限运行时长") -ForegroundColor DarkGray
        Write-Host "      （-WhatIfOnly：只打印，不注册任务；实际注册请用管理员 PowerShell）" -ForegroundColor DarkGray
        continue
    }

    $action = New-ScheduledTaskAction -Execute $task.exe -Argument $task.args -WorkingDirectory $task.work
    if ($Trigger -eq 'onlogon') {
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
        $principal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Highest
    } else {
        $trigger = New-ScheduledTaskTrigger -AtStartup
        $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    }
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit (New-TimeSpan -Seconds 0) `
        -MultipleInstances IgnoreNew

    $existing = Get-ScheduledTask -TaskName $task.name -ErrorAction SilentlyContinue
    if ($existing) {
        Unregister-ScheduledTask -TaskName $task.name -Confirm:$false
        Write-Host "      已存在同名任务，先删除后重建" -ForegroundColor DarkGray
    }
    Register-ScheduledTask -TaskName $task.name -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description ("DWS 物流解码平台 · " + $task.label + "（$Trigger 自启）") | Out-Null
    Write-Host "      注册成功" -ForegroundColor Green
}

if (!$WhatIfOnly) {
    Write-Host ""
    Write-Host " 已注册的任务：" -ForegroundColor Cyan
    Get-ScheduledTask -TaskName ($TaskPrefix + '-*') | Select-Object TaskName, State | Format-Table -AutoSize
    Write-Host " 立即启动一次（不重启设备也能验证）：" -ForegroundColor DarkGray
    foreach ($task in $tasks) { Write-Host ("   Start-ScheduledTask -TaskName " + $task.name) }
    Write-Host " 卸载：tools\uninstall-autostart.ps1" -ForegroundColor DarkGray
}
