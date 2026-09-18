<#
    卸载 DWS 的开机自启计划任务（与 install-autostart.ps1 配套）。

    用法（需要管理员权限）：
        powershell -ExecutionPolicy Bypass -File .\runtime\tools\uninstall-autostart.ps1
        powershell -ExecutionPolicy Bypass -File .\runtime\tools\uninstall-autostart.ps1 -TaskPrefix DWS-Edge

    说明：只删计划任务，不动 runtime 里的任何数据；如果进程正在跑，会先停掉再删任务。
#>
param(
    [string]$TaskPrefix = 'DWS-Edge',
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$isAdmin) {
    throw "删除计划任务需要管理员权限：请用'以管理员身份运行'的 PowerShell 重新执行"
}

# 新版本只注册一个任务（<Prefix>）；旧版本曾经是 <Prefix>-Host / <Prefix>-Platform，这里一起清掉
$names = @($TaskPrefix, ($TaskPrefix + '-Host'), ($TaskPrefix + '-Platform'))
foreach ($name in $names) {
    $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if (!$task) {
        Write-Host ("  任务不存在，跳过：" + $name) -ForegroundColor DarkGray
        continue
    }
    try { Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue } catch { }
    Unregister-ScheduledTask -TaskName $name -Confirm:$false
    Write-Host ("  已删除任务：" + $name) -ForegroundColor Green
}

if (!$KeepRunning) {
    $stopped = 0
    foreach ($proc in @('DwsEdge.Host', 'DwsEdge.Platform')) {
        Get-Process -Name $proc -ErrorAction SilentlyContinue | ForEach-Object {
            try { $_.Kill(); $stopped++ } catch { }
        }
    }
    if ($stopped -gt 0) { Write-Host ("  已停止 " + $stopped + " 个正在运行的 DWS 进程") -ForegroundColor DarkGray }
}

Write-Host "完成。自启已卸载（runtime 数据未改动）。" -ForegroundColor Cyan
