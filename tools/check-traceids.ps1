<#
    追踪号查重（压测/验收用）。

    扫描 spool\events-*.jsonl，统计：
      - 事件数、唯一追踪号数（= 包裹数）、平均每包裹事件数
      - 缺少追踪号的事件数（平台会用"相机+时间戳"兜底，可追溯性较弱）
      - 疑似追踪号冲突：同一追踪号下，后到的条码与已有条码完全不相交（很可能是两个包裹被并成一条）

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\check-traceids.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\check-traceids.ps1 -SpoolDir D:\dws\spool
#>
param(
    [string]$RuntimeDir = '',
    [string]$SpoolDir = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($RuntimeDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $RuntimeDir = Join-Path (Split-Path -Parent $scriptDir) 'runtime'
}
if ([string]::IsNullOrEmpty($SpoolDir)) {
    $SpoolDir = Join-Path $RuntimeDir 'spool'
}
if (!(Test-Path $SpoolDir)) {
    throw "找不到 spool 目录：$SpoolDir"
}

$files = @(Get-ChildItem $SpoolDir -Filter 'events-*.jsonl' | Sort-Object Name)
if ($files.Count -eq 0) {
    Write-Host "没有找到 events-*.jsonl：$SpoolDir" -ForegroundColor Yellow
    return
}

$events = 0
$missingTrace = 0
$conflicts = 0
$traceCodes = @{}
$conflictSamples = New-Object System.Collections.Generic.List[string]

foreach ($file in $files) {
    foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        try { $evt = $line | ConvertFrom-Json } catch { continue }
        if ($evt.type -ne 'parcel') { continue }

        $events++

        $trace = $evt.traceId
        if ([string]::IsNullOrEmpty($trace)) {
            $missingTrace++
            $trace = '(无追踪号) ' + $evt.deviceId + '|' + $evt.capturedAtMs
        }

        $codes = @()
        if ($evt.codes) { $codes = @($evt.codes | ForEach-Object { $_.value } | Where-Object { $_ }) }

        if (!$traceCodes.ContainsKey($trace)) {
            $traceCodes[$trace] = New-Object System.Collections.Generic.List[string]
        }
        $existing = $traceCodes[$trace]

        if ($codes.Count -gt 0 -and $existing.Count -gt 0) {
            $common = $false
            foreach ($c in $codes) {
                if ($existing.Contains($c)) { $common = $true; break }
            }
            if (!$common) {
                $conflicts++
                if ($conflictSamples.Count -lt 10) {
                    $conflictSamples.Add($trace + '  已有[' + ($existing -join ',') + ']  新事件[' + ($codes -join ',') + ']')
                }
            }
        }

        foreach ($c in $codes) {
            if (!$existing.Contains($c)) { $existing.Add($c) }
        }
    }
}

$unique = $traceCodes.Count
$avg = if ($unique -gt 0) { [math]::Round($events / $unique, 2) } else { 0 }

Write-Host ""
Write-Host ("追踪号查重报告（{0} 个文件）" -f $files.Count) -ForegroundColor Cyan
Write-Host ("  spool 目录      : {0}" -f $SpoolDir)
Write-Host ("  包裹事件总数    : {0}" -f $events)
Write-Host ("  唯一追踪号（包裹）: {0}" -f $unique)
Write-Host ("  平均每包裹事件数: {0}（大华分两次上报时正常值≈2）" -f $avg)
Write-Host ("  缺追踪号事件    : {0}" -f $missingTrace) -ForegroundColor $(if ($missingTrace -gt 0) { 'Yellow' } else { 'Gray' })
Write-Host ("  疑似追踪号冲突  : {0}" -f $conflicts) -ForegroundColor $(if ($conflicts -gt 0) { 'Red' } else { 'Green' })

if ($conflictSamples.Count -gt 0) {
    Write-Host ""
    Write-Host "冲突样例（最多 10 条）：" -ForegroundColor Red
    foreach ($sample in $conflictSamples) { Write-Host ("  " + $sample) }
}

Write-Host ""
if ($conflicts -eq 0 -and $missingTrace -eq 0) {
    Write-Host "结论：追踪号合并正常，未发现冲突。" -ForegroundColor Green
}
else {
    Write-Host "结论：存在可疑项，请核对采集宿主的追踪号生成与现场触发时序。" -ForegroundColor Yellow
}
