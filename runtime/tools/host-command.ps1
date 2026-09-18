<#
    A4：给现场用的"命令通道"包装脚本。

    直接调 DwsEdge.Host.exe 不方便记忆退出码，这里把三条命令包成开关，并把退出码翻译成人话：

        .\tools\host-command.ps1 -Status                  看当前 provider 与触发模式
        .\tools\host-command.ps1 -SoftTrigger             软触发一次（要求 triggerMode=2）
        .\tools\host-command.ps1 -SoftTrigger -Force      跳过触发模式校验（排查用）
        .\tools\host-command.ps1 -Recode -Code SF123      人工补码

    退出码与宿主一致：0 成功 / 1 参数错误 / 2 启动失败 / 3 未处理异常 /
                      4 命令执行失败 / 5 触发模式不允许 / 6 provider 不支持
#>
param(
    [switch]$Status,
    [switch]$SoftTrigger,
    [switch]$Recode,
    [string]$Code = '',
    [long]$TimeMs = 0,
    [switch]$Force,
    [string]$RuntimeDir = ''
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($RuntimeDir)) {
    $RuntimeDir = Join-Path (Split-Path -Parent $scriptDir) 'runtime'
}

$exe = Join-Path $RuntimeDir 'DwsEdge.Host.exe'
if (!(Test-Path $exe)) { throw "找不到采集宿主：$exe" }

$args = @()
if ($Status) { $args += '--command-status' }
if ($SoftTrigger) { $args += '--soft-trigger' }
if ($Recode) { $args += '--recode' }
if (![string]::IsNullOrEmpty($Code)) { $args += @('--code', $Code) }
if ($TimeMs -gt 0) { $args += @('--time-ms', $TimeMs.ToString()) }
if ($Force) { $args += '--force' }

if ($args.Count -eq 0) {
    Write-Host "用 法：host-command.ps1 -Status | -SoftTrigger [-Force] | -Recode -Code <条码>" -ForegroundColor Yellow
    exit 1
}
if ($Recode -and [string]::IsNullOrEmpty($Code)) {
    Write-Host "补码要带条码：-Recode -Code SF1234567890" -ForegroundColor Yellow
    exit 1
}

Push-Location $RuntimeDir
try {
    & $exe @args
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}

$conclusion = switch ($code) {
    0 { '成功' }
    1 { '参数错误（看上面的提示）' }
    2 { '采集宿主启动失败（加密狗 / 相机 / SDK 配置）' }
    3 { '未处理异常（看 logs\host-*.log）' }
    4 { '命令执行失败（provider 返回非 0，看日志）' }
    5 { '触发模式不允许：当前不是软触发模式（用 tools\set-trigger-mode.ps1 -Mode soft 改好，或加 -Force）' }
    6 { '当前 provider 不支持这条命令' }
    default { '未知退出码' }
}

$color = if ($code -eq 0) { 'Green' } elseif ($code -eq 5) { 'Yellow' } else { 'Red' }
Write-Host ""
Write-Host ("[命令结论] 退出码 " + $code + " —— " + $conclusion) -ForegroundColor $color
exit $code
