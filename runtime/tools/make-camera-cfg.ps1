<#
    生成/改写大华 cfg 里的相机清单：ImageAcq 的 mode/num + <Camera .../> 声明。

    用法：
        # 1) 用清单文件（推荐，17 台六面扫示例见 config\cameras-17.example.txt）
        powershell -ExecutionPolicy Bypass -File .\tools\make-camera-cfg.ps1 -CameraList .\config\cameras-17.example.txt

        # 2) 直接给几台
        powershell -ExecutionPolicy Bypass -File .\tools\make-camera-cfg.ps1 -Cameras "ip=172.20.10.11","ip=172.20.10.12"

        # 3) 只看结果、不改文件
        powershell -ExecutionPolicy Bypass -File .\tools\make-camera-cfg.ps1 -CameraList ... -Preview

    说明：
        * 每台相机写成 ip=... / key=... / id=厂商:序列号，脚本会自动生成 enable="1" 的声明；
        * mode 默认改为 2（按 IP/Key/Id 指定相机），num 自动等于相机台数；
        * cfg 是 GB2312 编码，脚本按 GB2312 读写，并自动生成带时间戳的备份。
#>
param(
    [string[]]$Cameras = @(),
    [string]$CameraList = '',
    [string]$CfgPath = '',
    [string]$RuntimeDir = '',
    [ValidateSet('auto', 'keep', '1', '2', '3', '4')][string]$Mode = 'auto',
    [switch]$Preview
)

$ErrorActionPreference = 'Stop'

# ---------- 路径 ----------
if ([string]::IsNullOrEmpty($RuntimeDir)) {
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
# 兼容两种布局：开发仓库是 <仓库>\tools\ → <仓库>\runtime；交付包是 <runtime>\tools\ → <runtime>
$parentDir = Split-Path -Parent $scriptDir
$RuntimeDir = Join-Path $parentDir 'runtime'
foreach ($candidate in @((Join-Path $parentDir 'runtime'), $parentDir)) {
    if (Test-Path (Join-Path $candidate 'Cfg\LogisticsBase.cfg')) { $RuntimeDir = $candidate; break }
}
}
if ([string]::IsNullOrEmpty($CfgPath)) {
    $CfgPath = Join-Path $RuntimeDir 'Cfg\LogisticsBase.cfg'
}
if (!(Test-Path $CfgPath)) { throw "找不到 cfg：$CfgPath" }

# ---------- 读取相机清单 ----------
$entries = New-Object System.Collections.Generic.List[string]

if (![string]::IsNullOrEmpty($CameraList)) {
    if (!(Test-Path $CameraList)) { throw "找不到清单文件：$CameraList" }
    foreach ($line in (Get-Content -Encoding UTF8 $CameraList)) {
        $text = $line.Trim()
        if ($text.Length -eq 0 -or $text.StartsWith('#')) { continue }
        $entries.Add($text)
    }
}

foreach ($item in $Cameras) {
    $text = "$item".Trim()
    if ($text.Length -gt 0) { $entries.Add($text) }
}

if ($entries.Count -eq 0 -and $Mode -ne 'keep') {
    throw "没有提供相机清单。请用 -CameraList <文件> 或 -Cameras @('ip=x',...)"
}

# 校验条目格式，并拆成 key/value/pos（写回 cfg 时值要带引号）
$knownPositions = @('top', 'bottom', 'left', 'right', 'front', 'rear', 'line', 'spare')
$invalidPositions = New-Object System.Collections.Generic.List[string]

$valid = New-Object System.Collections.Generic.List[object]
foreach ($entry in $entries) {
    # 支持 "ip=172.20.10.11,pos=top" 这种写法（pos 可选）
    $pos = ''
    $main = $entry
    $comma = $entry.LastIndexOf(',')
    if ($comma -gt 0) {
        $tail = $entry.Substring($comma + 1).Trim()
        if ($tail -match '(?i)^pos\s*=\s*(.+)$') {
            $pos = $Matches[1].Trim().Trim('"')
            $main = $entry.Substring(0, $comma).Trim()
        }
    }

    $parts = $main -split '=', 2
    $key = $parts[0].Trim().ToLowerInvariant()
    if ($key -ne 'ip' -and $key -ne 'key' -and $key -ne 'id') {
        throw "相机条目格式错误：'$entry'，应为 ip=... / key=... / id=...，可选在逗号后加 pos=top"
    }
    $value = if ($parts.Count -gt 1) { $parts[1].Trim().Trim('"') } else { '' }
    if ($value.Length -eq 0) {
        throw "相机条目的值不能为空：'$entry'"
    }
    if ($pos.Length -gt 0 -and ($knownPositions -notcontains $pos.ToLowerInvariant())) {
        $invalidPositions.Add($entry)
    }

    $valid.Add([pscustomobject]@{ Key = $key; Value = $value; Pos = $pos })
}

$duplicates = $valid | Group-Object { $_.Key + '=' + $_.Value } | Where-Object { $_.Count -gt 1 }
if ($duplicates) {
    throw ("相机清单里有重复条目：" + ($duplicates | ForEach-Object { $_.Name }) )
}

# ---------- 读写 cfg（GB2312） ----------
$gb2312 = [System.Text.Encoding]::GetEncoding(936)
$text = [System.IO.File]::ReadAllText($CfgPath, $gb2312)

$imageAcqMatch = [regex]::Match($text, '<ImageAcq\b[^>]*/?>')
if (!$imageAcqMatch.Success) { throw "cfg 里找不到 <ImageAcq ...> 节点" }

$modeValue = $Mode
if ($Mode -eq 'auto') { $modeValue = if ($valid.Count -gt 0) { '2' } else { 'keep' } }
if ($modeValue -eq 'keep') {
    $current = [regex]::Match($imageAcqMatch.Value, 'mode="([^"]*)"')
    $modeValue = if ($current.Success) { $current.Groups[1].Value } else { '1' }
}

$newImageAcq = $imageAcqMatch.Value
$newImageAcq = [regex]::Replace($newImageAcq, 'mode="[^"]*"', 'mode="' + $modeValue + '"')
$newImageAcq = [regex]::Replace($newImageAcq, 'num="[^"]*"', 'num="' + $valid.Count + '"')

$newText = $text.Substring(0, $imageAcqMatch.Index) + $newImageAcq + $text.Substring($imageAcqMatch.Index + $imageAcqMatch.Length)

# 整块替换相机声明：把原来所有 <Camera .../> 行换成新的 N 行（保留缩进）
$cameraMatches = [regex]::Matches($newText, '(?m)^([ \t]*)<Camera\b[^>]*/>[ \t]*\r?\n?')
if ($cameraMatches.Count -eq 0) {
    throw "cfg 里没有任何 <Camera .../> 行，无法替换。请先确认 cfg 版本"
}

# 这些行必须连续；中间夹杂别的内容时不敢自动替换，避免误删
for ($i = 1; $i -lt $cameraMatches.Count; $i++) {
    $between = $newText.Substring($cameraMatches[$i - 1].Index + $cameraMatches[$i - 1].Length,
        $cameraMatches[$i].Index - ($cameraMatches[$i - 1].Index + $cameraMatches[$i - 1].Length))
    if ($between.Trim().Length -gt 0) {
        throw "cfg 里的 <Camera> 行不连续，无法安全替换。请手动调整或先备份后手工编辑"
    }
}

$indent = $cameraMatches[0].Groups[1].Value
$blockStart = $cameraMatches[0].Index
$blockEnd = $cameraMatches[$cameraMatches.Count - 1].Index + $cameraMatches[$cameraMatches.Count - 1].Length

$blockBuilder = New-Object System.Text.StringBuilder
foreach ($entry in $valid) {
    [void]$blockBuilder.Append($indent)
    [void]$blockBuilder.Append('<Camera ')
    [void]$blockBuilder.Append($entry.Key)
    [void]$blockBuilder.Append('="')
    [void]$blockBuilder.Append($entry.Value)
    [void]$blockBuilder.Append('" enable="1" />')
    [void]$blockBuilder.Append("`r`n")
}

$newText = $newText.Substring(0, $blockStart) + $blockBuilder.ToString() + $newText.Substring($blockEnd)

# ---------- 输出 ----------
Write-Host ""
Write-Host ("相机清单（共 {0} 台，模式 mode={1}）" -f $valid.Count, $modeValue) -ForegroundColor Cyan
foreach ($entry in $valid) {
    $suffix = if ($entry.Pos) { '   pos=' + $entry.Pos } else { '' }
    Write-Host ('  <Camera {0}="{1}" enable="1" />{2}' -f $entry.Key, $entry.Value, $suffix)
}

$posEntries = @($valid | Where-Object { $_.Pos -and $_.Pos.Length -gt 0 })
$posPath = Join-Path $RuntimeDir 'config\camera-positions.ini'

if ($Preview) {
    Write-Host ""
    Write-Host "预览模式：未写入文件。修改后 ImageAcq 为：" -ForegroundColor Yellow
    Write-Host "  $newImageAcq"
    Write-Host ("将写入方位映射 {0} 条：{1}" -f $posEntries.Count, $posPath) -ForegroundColor Yellow
    return
}

$backup = "$CfgPath.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
Copy-Item -LiteralPath $CfgPath -Destination $backup -Force
[System.IO.File]::WriteAllText($CfgPath, $newText, $gb2312)

Write-Host ""
Write-Host "已写入：$CfgPath" -ForegroundColor Green
Write-Host "备份：$backup"
Write-Host ("ImageAcq 已更新为：{0}" -f $newImageAcq)

# 方位映射：相机回调不带方位时，采集宿主用它兜底
if ($posEntries.Count -gt 0) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# 相机方位映射（由 tools\make-camera-cfg.ps1 生成）')
    $lines.Add('# key = 相机 ip / 序列号 / 完整 id；value = 方位（top/bottom/left/right/front/rear）')
    foreach ($entry in $posEntries) {
        $lines.Add($entry.Value + '=' + $entry.Pos)
    }
    $posDir = Split-Path -Parent $posPath
    if (!(Test-Path $posDir)) { New-Item -ItemType Directory -Force -Path $posDir | Out-Null }
    [System.IO.File]::WriteAllLines($posPath, $lines, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("已写入方位映射 {0} 条：{1}" -f $posEntries.Count, $posPath) -ForegroundColor Green
}
else {
    Write-Host "清单里没有 pos=，未生成方位映射文件（条码方位将取决于相机回调是否自带 Position）" -ForegroundColor Yellow
}

if ($invalidPositions.Count -gt 0) {
    Write-Host ("注意：以下方位不在常用取值(top/bottom/left/right/front/rear/line/spare)内，请确认：" + ($invalidPositions -join '; ')) -ForegroundColor Yellow
}

Write-Host "下一步：重启采集宿主（DwsEdge.Host）让配置生效；启动时会自动做一次相机配置自检。"
