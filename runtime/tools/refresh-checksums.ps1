<#
    重算交付包的 checksums.txt（**不改版本号**）。

    什么时候用：现场热修（覆盖了 exe/dll/前端/脚本）之后，包里的 checksums.txt 会和实际内容对不上，
    check-package.ps1 会报"哈希不符"。跑一下本脚本把校验和重新对齐即可 ——
    版本号（VERSION.txt / 仓库 VERSION）保持原样，不重新打包、不动现场配置。

    生成格式与 tools\make-package.ps1 完全一致：
      * 头部两行注释（版本 + 生成时间）
      * 关键文件（exe/dll/前端/config/VERSION.txt）
      * runtime\tools\*.ps1（按文件名排序，含本脚本自己）

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\refresh-checksums.ps1 -Package <包目录>
        powershell -ExecutionPolicy Bypass -File .\tools\refresh-checksums.ps1     # 自动找桌面上最新的包
#>
param(
    [string]$Package = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($Package)) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $cands = Get-ChildItem $desktop -Directory -Filter 'DWS-Edge-Min_*' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch '\._old-' } | Sort-Object LastWriteTime -Descending
    if (-not $cands) { throw '没找到交付包，请用 -Package <包目录> 指定' }
    $Package = $cands[0].FullName
}
$Package = (Resolve-Path $Package).Path

# 版本号从包里的 VERSION.txt 读 —— 这就是"不动版本号"：以包内现有版本为准
$versionTxt = Join-Path $Package 'VERSION.txt'
if (!(Test-Path $versionTxt)) { throw "包里没有 VERSION.txt：$versionTxt" }
$versionLine = (Select-String -Path $versionTxt -Pattern '^版本号\s*:' -Encoding UTF8 | Select-Object -First 1).Line
$version = if ($versionLine) { ($versionLine -split ':', 2)[1].Trim() } else { '未标注' }

$keyFiles = @(
    'runtime\DwsEdge.Host.exe', 'runtime\DwsEdge.Shell.exe', 'runtime\DwsEdge.Core.dll',
    'runtime\platform\DwsEdge.Platform.exe', 'runtime\platform\DwsEdge.Platform.dll',
    'runtime\platform\appsettings.json', 'runtime\platform\wwwroot\index.html',
    'runtime\Cfg\LogisticsBase.cfg', 'runtime\config\gateway.ini',
    'VERSION.txt'
)

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# DWS Edge Min $version 关键文件校验和（SHA256）")
$lines.Add("# 生成时间：" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))

$count = 0
foreach ($rel in $keyFiles) {
    $full = Join-Path $Package $rel
    if (Test-Path $full) {
        $lines.Add(((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash + '  ' + $rel))
        $count++
    }
    else {
        Write-Host ("  缺文件（已跳过）：" + $rel) -ForegroundColor Yellow
    }
}

$toolsDir = Join-Path $Package 'runtime\tools'
if (Test-Path $toolsDir) {
    Get-ChildItem $toolsDir -Filter '*.ps1' -File | Sort-Object Name | ForEach-Object {
        $lines.Add(((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  runtime\tools\' + $_.Name))
        $count++
    }
}

$target = Join-Path $Package 'checksums.txt'
[System.IO.File]::WriteAllText($target, ($lines -join "`r`n"), (New-Object System.Text.UTF8Encoding($true)))

Write-Host ('已重算 checksums.txt（版本保持 ' + $version + '，共 ' + $count + ' 个文件）：') -ForegroundColor Green
Write-Host ('  ' + $target)
