<#
    停止采集宿主与业务平台（配合 start-all.ps1 的隐藏启动）。

    优先按 logs\pids.json 里记录的 PID 停；PID 已经变了/文件被删了，就退回
    "按可执行文件路径在本 runtime 下"来兜底，避免误杀别的程序。

    用法：
        powershell -ExecutionPolicy Bypass -File .\runtime\tools\stop-all.ps1
        powershell -ExecutionPolicy Bypass -File .\runtime\tools\stop-all.ps1 -Status   # 只看状态不停
#>
param(
    [string]$RuntimeDir = '',
    [switch]$Status
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($RuntimeDir)) {
    $parentDir = Split-Path -Parent $scriptDir
    foreach ($candidate in @((Join-Path $parentDir 'runtime'), $parentDir)) {
        if (Test-Path (Join-Path $candidate 'DwsEdge.Host.exe')) { $RuntimeDir = $candidate; break }
    }
    if ([string]::IsNullOrEmpty($RuntimeDir)) { $RuntimeDir = Join-Path $parentDir 'runtime' }
}
$RuntimeDir = [System.IO.Path]::GetFullPath($RuntimeDir)

function Get-DwsProcesses {
    return @(Get-Process -Name 'DwsEdge.Host', 'DwsEdge.Platform' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ($_.Path -like ($RuntimeDir + '*')) })
}

$found = Get-DwsProcesses
if ($found.Count -eq 0) {
    Write-Host "没有正在运行的 DWS 进程（$RuntimeDir）" -ForegroundColor DarkGray
    exit 0
}

Write-Host "当前进程：" -ForegroundColor Cyan
$found | ForEach-Object { Write-Host ("  PID " + $_.Id + "  " + $_.ProcessName + "  启动于 " + $_.StartTime) }
if ($Status) { exit 0 }

foreach ($proc in $found) {
    try {
        Stop-Process -Id $proc.Id -Force
        Write-Host ("  已停止 PID " + $proc.Id + "（" + $proc.ProcessName + "）") -ForegroundColor Green
    } catch {
        Write-Host ("  停止失败 PID " + $proc.Id + "：" + $_.Exception.Message) -ForegroundColor Red
    }
}

$stateFile = Join-Path $RuntimeDir 'logs\pids.json'
if (Test-Path $stateFile) { Remove-Item -LiteralPath $stateFile -Force -ErrorAction SilentlyContinue }
Write-Host "已全部停止。" -ForegroundColor Cyan
