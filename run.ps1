<#
    运行采集宿主。

    用法：
        powershell -ExecutionPolicy Bypass -File .\run.ps1
        powershell -ExecutionPolicy Bypass -File .\run.ps1 -Duration 30
        powershell -ExecutionPolicy Bypass -File .\run.ps1 -TriggerOnce -Duration 8
        powershell -ExecutionPolicy Bypass -File .\run.ps1 -TriggerInterval 3000
#>
param(
    [int]$Duration = 0,
    [switch]$TriggerOnce,
    [int]$TriggerInterval = 0,
    [int]$TriggerDelay = -1
)

$ErrorActionPreference = 'Stop'

$exe = Join-Path $PSScriptRoot 'runtime\DwsEdge.Host.exe'
if (!(Test-Path $exe)) {
    throw "还没编译，请先运行 .\build.ps1"
}

Push-Location (Split-Path $exe)
try {
    $arguments = @()
    if ($Duration -gt 0) { $arguments += @('--duration', $Duration) }
    if ($TriggerOnce) { $arguments += '--trigger-once' }
    if ($TriggerInterval -gt 0) { $arguments += @('--trigger-interval', $TriggerInterval) }
    if ($TriggerDelay -ge 0) { $arguments += @('--trigger-delay', $TriggerDelay) }

    if ($arguments.Count -gt 0) {
        & $exe @arguments
    }
    else {
        & $exe
    }
}
finally {
    Pop-Location
}
