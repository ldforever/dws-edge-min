<#
    切换大华 SDK 的触发模式（改 runtime\Cfg\LogisticsBase.cfg 里的 ReadCodeMode.triggerMode）。

        soft = 软触发 (triggerMode="2")  —— 配合 CameraSoftTrigger / 本项目的软触发开关
        hard = 硬触发 (triggerMode="1")  —— 默认值，光电触发
        free = 自由拉流 (triggerMode="0") —— 狂扫模式

    会自动生成带时间戳的备份；改完需要重启 DwsEdge.Host 才生效（SDK 只在初始化时读配置）。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\set-trigger-mode.ps1 -Mode soft
        powershell -ExecutionPolicy Bypass -File .\tools\set-trigger-mode.ps1 -Mode hard
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('soft', 'hard', 'free')]
    [string]$Mode,

    [string]$RuntimeDir = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($RuntimeDir)) {
    # 用 $MyInvocation 取脚本路径，避免某些 PowerShell 版本在 param 默认值里拿不到 $PSScriptRoot
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
# 兼容两种布局：开发仓库是 <仓库>\tools\ → <仓库>\runtime；交付包是 <runtime>\tools\ → <runtime>
$parentDir = Split-Path -Parent $scriptDir
$RuntimeDir = Join-Path $parentDir 'runtime'
foreach ($candidate in @((Join-Path $parentDir 'runtime'), $parentDir)) {
    if (Test-Path (Join-Path $candidate 'Cfg\LogisticsBase.cfg')) { $RuntimeDir = $candidate; break }
}
}

$cfgPath = Join-Path $RuntimeDir 'Cfg\LogisticsBase.cfg'
if (!(Test-Path $cfgPath)) {
    throw "找不到配置文件：$cfgPath"
}

$target = '2'
if ($Mode -eq 'hard') { $target = '1' }
if ($Mode -eq 'free') { $target = '0' }

function Find-Bytes {
    param([byte[]]$Haystack, [byte[]]$Needle, [int]$Start)
    for ($i = $Start; $i -le $Haystack.Length - $Needle.Length; $i++) {
        $match = $true
        for ($j = 0; $j -lt $Needle.Length; $j++) {
            if ($Haystack[$i + $j] -ne $Needle[$j]) { $match = $false; break }
        }
        if ($match) { return $i }
    }
    return -1
}

$bytes = [System.IO.File]::ReadAllBytes($cfgPath)

$anchor = [System.Text.Encoding]::ASCII.GetBytes('<ReadCodeMode')
$anchorIndex = Find-Bytes -Haystack $bytes -Needle $anchor -Start 0
if ($anchorIndex -lt 0) {
    throw "cfg 里找不到 <ReadCodeMode> 节点"
}

$needle = [System.Text.Encoding]::ASCII.GetBytes('triggerMode="')
$index = Find-Bytes -Haystack $bytes -Needle $needle -Start $anchorIndex
if ($index -lt 0) {
    throw "cfg 里找不到 triggerMode= 属性"
}

$valueIndex = $index + $needle.Length
$oldValue = [System.Text.Encoding]::ASCII.GetString($bytes, $valueIndex, 1)

if ($oldValue -eq $target) {
    Write-Host "当前已经是 $Mode（triggerMode=$target），无需修改。" -ForegroundColor Yellow
    return
}

$backup = "$cfgPath.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
Copy-Item -LiteralPath $cfgPath -Destination $backup -Force

$bytes[$valueIndex] = [byte][char]$target
[System.IO.File]::WriteAllBytes($cfgPath, $bytes)

Write-Host "已把 triggerMode 从 $oldValue 改成 $target（$Mode）" -ForegroundColor Green
Write-Host "备份文件：$backup"
Write-Host "注意：SDK 只在初始化时读配置，需要重启 DwsEdge.Host 才生效。"
