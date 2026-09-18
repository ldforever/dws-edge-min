<#
    运行采集宿主与业务平台。

    用法：
        .\run.ps1                           只跑采集宿主（常驻，Ctrl+C 停止）
        .\run.ps1 -TriggerOnce -Duration 8  采集宿主 + 启动后软触发一次
        .\run.ps1 -TriggerInterval 3000     采集宿主 + 每 3 秒触发一次（模拟连续过包）
        .\run.ps1 -Platform                 只跑业务平台（浏览器打开 http://本机IP:8090）
        .\run.ps1 -Both -TriggerOnce        同时跑平台（后台）和采集宿主

        .\run.ps1 -Background               隐藏启动两个进程（不弹黑窗口），立即返回
                                            配套：runtime\tools\stop-all.ps1 停止、-Status 看状态
#>
param(
    [int]$Duration = 0,
    [switch]$TriggerOnce,
    [int]$TriggerInterval = 0,
    [int]$TriggerDelay = -1,
    [switch]$Platform,
    [switch]$Both,
    [switch]$Background
)

$ErrorActionPreference = 'Stop'

$runtimeDir  = Join-Path $PSScriptRoot 'runtime'
$edgeExe     = Join-Path $runtimeDir 'DwsEdge.Host.exe'
$platformExe = Join-Path $runtimeDir 'platform\DwsEdge.Platform.exe'

# -Background：交给 tools\start-all.ps1 隐藏启动（不弹黑窗口），本脚本立即返回
if ($Background) {
    # 兼容两种布局：开发仓库是 <仓库>\tools\；交付包里 tools 在 <包>\runtime\tools\
    $toolsDir = Join-Path $PSScriptRoot 'tools'
    if (!(Test-Path (Join-Path $toolsDir 'start-all.ps1'))) { $toolsDir = Join-Path $runtimeDir 'tools' }
    $startAll = Join-Path $toolsDir 'start-all.ps1'
    if (!(Test-Path $startAll)) { throw "找不到 $startAll（确认 runtime\tools 目录完整）" }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $startAll -RuntimeDir $runtimeDir
    exit $LASTEXITCODE
}

if (($Platform -or $Both) -and !(Test-Path $platformExe)) {
    throw "还没编译业务平台，请先运行 .\build.ps1"
}
if (!$Platform -and !(Test-Path $edgeExe)) {
    throw "还没编译采集宿主，请先运行 .\build.ps1"
}

$platformProc = $null
if ($Both) {
    Write-Host "启动业务平台（后台）..." -ForegroundColor DarkGray
    $platformProc = Start-Process -FilePath $platformExe -WorkingDirectory (Split-Path $platformExe) -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2
}

try {
    if ($Platform) {
        Write-Host "业务平台运行中： http://localhost:8090  （Ctrl+C 停止）" -ForegroundColor Green
        & $platformExe
    }
    else {
        $arguments = @()
        if ($Duration -gt 0) { $arguments += @('--duration', $Duration) }
        if ($TriggerOnce) { $arguments += '--trigger-once' }
        if ($TriggerInterval -gt 0) { $arguments += @('--trigger-interval', $TriggerInterval) }
        if ($TriggerDelay -ge 0) { $arguments += @('--trigger-delay', $TriggerDelay) }

        Push-Location $runtimeDir
        try {
            if ($arguments.Count -gt 0) { & $edgeExe @arguments } else { & $edgeExe }
        }
        finally { Pop-Location }
    }
}
finally {
    if ($platformProc -and !$platformProc.HasExited) {
        Stop-Process -Id $platformProc.Id -Force
        Write-Host "已停止业务平台。" -ForegroundColor DarkGray
    }
}
