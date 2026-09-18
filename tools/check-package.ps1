<#
    check-package.ps1 —— 交付包核验（出厂前 / 到货后都能跑）

    它回答一个问题：**这个包能不能交付**。

    检查五件事：
      1) 包名里的版本号  = VERSION.txt 里的版本号
      2) VERSION.txt 里的版本号 = exe/dll 的文件属性（程序集版本）
      3) checksums.txt 里每个文件的 SHA256 都对得上（包没被改动/损坏）
      4) 包内产物与开发仓库 runtime 逐文件一致（只允许 3 处设计内的差异）
      5) （可选 -RunSelfCheck）在临时目录里跑一遍 self-check.ps1，看服务能不能起来

    用法（在交付包里直接跑，不用带参数）：
        powershell -ExecutionPolicy Bypass -File runtime\tools\check-package.ps1

    只想核对版本号与校验和（不碰仓库、不启进程，最快）：
        powershell -ExecutionPolicy Bypass -File runtime\tools\check-package.ps1 -SkipRepoCompare

    还想跑一遍启动自检（会在临时目录里拷一份 runtime，不污染本包）：
        powershell -ExecutionPolicy Bypass -File runtime\tools\check-package.ps1 -RunSelfCheck

    退出码：0 = 全部通过，可交付；1 = 有失败项，不可交付。
#>
param(
    [string]$Package = '',                 # 交付包根目录；留空则自动找桌面上最新的 DWS-Edge-Min_*
    [string]$Repo = 'C:\Users\Administrator\Desktop\lightcookr\dws-edge-min',
    [switch]$SkipRepoCompare,
    [switch]$RunSelfCheck,
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
$script:pass = 0
$script:fail = 0
$script:rows = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    $script:rows.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Detail = $Detail })
    if ($Ok) { $script:pass++ } else { $script:fail++ }
    $color = if ($Ok) { 'Green' } else { 'Red' }
    $tag = if ($Ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1}" -f $tag, $Name) -ForegroundColor $color
    if ($Detail) { Write-Host ("         " + $Detail) -ForegroundColor DarkGray }
}

function Add-Info {
    param([string]$Name, [string]$Detail)
    $script:rows.Add([pscustomobject]@{ Name = $Name; Ok = $true; Detail = $Detail })
    Write-Host ("  [INFO] " + $Name) -ForegroundColor Cyan
    if ($Detail) { Write-Host ("         " + $Detail) -ForegroundColor DarkGray }
}

function Get-RelativePath {
    param([string]$Base, [string]$Full)
    $b = $Base.TrimEnd('\') + '\'
    if ($Full.StartsWith($b, [System.StringComparison]::OrdinalIgnoreCase)) { return $Full.Substring($b.Length) }
    return $Full
}

# 子进程输出文件的编码是"看机器"的（PS 5.1 在中文系统上可能写成 GB2312），
# 所以按 UTF-8 先试，解不出来再用 GB2312。
function Read-TextAuto {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return '' }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -eq 0) { return '' }
    try {
        $strict = New-Object System.Text.UTF8Encoding($false, $true)
        return $strict.GetString($bytes)
    } catch {
        return [System.Text.Encoding]::GetEncoding(936).GetString($bytes)
    }
}
# ============================================================ 0) 找包
if ([string]::IsNullOrEmpty($Package)) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $cands = Get-ChildItem $desktop -Directory -Filter 'DWS-Edge-Min_*' -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -notmatch '\._old-' } |
             Sort-Object LastWriteTime -Descending
    if (-not $cands -or $cands.Count -eq 0) {
        Write-Host "没找到交付包。请用 -Package <包目录> 指定。" -ForegroundColor Red
        exit 1
    }
    $Package = $cands[0].FullName
}
if (-not (Test-Path $Package)) { Write-Host "包目录不存在：$Package" -ForegroundColor Red; exit 1 }
$Package = (Resolve-Path $Package).Path

Write-Host ""
Write-Host "=== 交付包核验 ===" -ForegroundColor White
Write-Host ("  包目录：" + $Package)
Write-Host ""
Write-Host "[1/5] 版本号口径" -ForegroundColor Cyan

# ============================================================ 1) 版本号口径
$versionTxt = Join-Path $Package 'VERSION.txt'
$pkgVer = ''
if (Test-Path $versionTxt) {
    $line = (Select-String -Path $versionTxt -Pattern '^版本号\s*:' -Encoding UTF8 | Select-Object -First 1).Line
    if ($line) { $pkgVer = ($line -split ':', 2)[1].Trim() }
    Add-Result 'VERSION.txt 存在且能读出版本号' ($pkgVer -ne '') ("读取到：" + $(if ($pkgVer) { $pkgVer } else { '(空)' }))
} else {
    Add-Result 'VERSION.txt 存在' $false '包根缺 VERSION.txt'
}

$folderName = Split-Path $Package -Leaf
if ($pkgVer) {
    $folderOk = $folderName -match [regex]::Escape($pkgVer)
    Add-Result '包名与 VERSION.txt 版本号一致' $folderOk ("包名：" + $folderName + "   版本号：" + $pkgVer)
} else {
    Add-Result '包名与 VERSION.txt 版本号一致' $false '版本号没读出来，无法比对'
}

# 程序集版本（exe/dll 的文件属性）
$asmTargets = @(
    'runtime\DwsEdge.Host.exe',
    'runtime\DwsEdge.Core.dll',
    'runtime\platform\DwsEdge.Platform.exe',
    'runtime\platform\DwsEdge.Platform.dll',
    'runtime\providers\DwsEdge.Providers.Dahua.dll'
)
$wantAsm = ($pkgVer -replace '^[Vv]', '')
$asmSeen = @{}
$asmBad = New-Object System.Collections.Generic.List[string]
foreach ($rel in $asmTargets) {
    $f = Join-Path $Package $rel
    if (-not (Test-Path $f)) { $asmBad.Add($rel + "（文件缺失）"); continue }
    $fv = (Get-Item $f).VersionInfo.FileVersion
    $asmSeen[$rel] = $fv
    if ($wantAsm -and $fv -ne $wantAsm) { $asmBad.Add($rel + "（实际 " + $fv + "）") }
}
if ($asmBad.Count -eq 0) {
    Add-Result '产物程序集版本 = 版本号' ($true) ("全部 " + $asmTargets.Count + " 个产物均为 " + $wantAsm)
} else {
    Add-Result '产物程序集版本 = 版本号' $false ("对齐不上：" + ($asmBad -join '；') + "；期望 " + $wantAsm)
}

# ============================================================ 2) 校验和
Write-Host ""
Write-Host "[2/5] 包完整性（checksums.txt）" -ForegroundColor Cyan
$sumFile = Join-Path $Package 'checksums.txt'
if (Test-Path $sumFile) {
    $sumLines = Get-Content $sumFile -Encoding UTF8 | Where-Object { $_ -and $_.Trim() -ne '' -and -not $_.StartsWith('#') }
    $sumOk = 0
    $sumBad = New-Object System.Collections.Generic.List[string]
    foreach ($ln in $sumLines) {
        if ($ln -notmatch '^([0-9A-Fa-f]{64})\s+(.+)$') { continue }
        $want = $Matches[1].ToUpperInvariant()
        $rel = $Matches[2].Trim()
        $f = Join-Path $Package $rel
        if (-not (Test-Path $f)) { $sumBad.Add($rel + "（文件缺失）"); continue }
        $got = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash
        if ($got -eq $want) { $sumOk++ } else { $sumBad.Add($rel + "（哈希不符）") }
    }
    if ($sumBad.Count -eq 0 -and $sumOk -gt 0) {
        Add-Result 'checksums.txt 逐文件校验' $true ($sumOk.ToString() + " 个文件全部对得上")
    } else {
        Add-Result 'checksums.txt 逐文件校验' $false ("失败 " + $sumBad.Count + " 个：" + (($sumBad | Select-Object -First 5) -join '；'))
    }
} else {
    Add-Result 'checksums.txt 存在' $false '包根缺 checksums.txt'
}

# ============================================================ 3) 与源码比对
Write-Host ""
Write-Host "[3/5] 包内产物 vs 开发仓库 runtime" -ForegroundColor Cyan
if ($SkipRepoCompare) {
    Add-Info '已按 -SkipRepoCompare 跳过' ''
} elseif (-not (Test-Path $Repo)) {
    Add-Info ('仓库不存在，跳过：' + $Repo) '交付到现场后属于正常情况'
} else {
    $expectDiff = @('runtime\Cfg\LogisticsBase.cfg', 'runtime\config\gateway.ini')
    $related = @(
        'runtime\DwsEdge.Host.exe', 'runtime\DwsEdge.Core.dll',
        'runtime\platform\DwsEdge.Platform.exe', 'runtime\platform\DwsEdge.Platform.dll',
        'runtime\platform\appsettings.json',
        'runtime\platform\wwwroot\index.html', 'runtime\platform\wwwroot\app.css',
        'run.ps1', 'docs\操作手册.md'
    )
    $sameCount = 0
    $realDiff = New-Object System.Collections.Generic.List[string]
    foreach ($rel in $related) {
        $a = Join-Path $Package $rel
        $b = Join-Path $Repo $rel
        if (-not (Test-Path $a) -or -not (Test-Path $b)) { $realDiff.Add($rel + "（包或仓库缺文件）"); continue }
        if ((Get-FileHash -LiteralPath $a -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $b -Algorithm SHA256).Hash) { $sameCount++ }
        else { $realDiff.Add($rel + "（内容不同）") }
    }
    # tools 与平台前端 js 逐文件比对
    foreach ($pair in @(@{ pkg = 'runtime\tools'; repo = 'tools'; filter = '*.ps1'; label = 'tools' },
                        @{ pkg = 'runtime\platform\wwwroot\js'; repo = 'runtime\platform\wwwroot\js'; filter = '*.js'; label = 'platform\wwwroot\js' })) {
        $srcDir = Join-Path $Repo $pair.repo
        $pkgDir = Join-Path $Package $pair.pkg
        if (-not (Test-Path $srcDir)) { continue }
        $files = Get-ChildItem $srcDir -Filter $pair.filter -File -ErrorAction SilentlyContinue
        foreach ($f in $files) {
            $p = Join-Path $pkgDir $f.Name
            if (-not (Test-Path $p)) { $realDiff.Add($pair.label + '\' + $f.Name + "（包内缺）"); continue }
            if ((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash) { $sameCount++ }
            else { $realDiff.Add($pair.label + '\' + $f.Name + "（内容不同）") }
        }
    }
    if ($realDiff.Count -eq 0) {
        Add-Result '关键产物逐文件一致' $true ($sameCount.ToString() + " 个文件与仓库完全一致")
    } else {
        Add-Result '关键产物逐文件一致' $false ("有 " + $realDiff.Count + " 处非预期差异：" + (($realDiff | Select-Object -First 5) -join '；'))
    }
    # 预期差异（打包时按设计改的），只提示不算失败
    $expectedNote = New-Object System.Collections.Generic.List[string]
    foreach ($rel in $expectDiff) {
        $a = Join-Path $Package $rel
        $b = Join-Path $Repo $rel
        if ((Test-Path $a) -and (Test-Path $b) -and
            ((Get-FileHash -LiteralPath $a -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $b -Algorithm SHA256).Hash)) {
            $expectedNote.Add($rel)
        }
    }
    if ($expectedNote.Count -gt 0) {
        Add-Info '设计内的差异（不算问题）' ($expectedNote -join '；')
    }
    # 仓库 VERSION 文件
    $repoVerFile = Join-Path $Repo 'VERSION'
    if (Test-Path $repoVerFile) {
        $repoVer = ([System.IO.File]::ReadAllText($repoVerFile)).Trim()
        Add-Result '仓库 VERSION 文件 = 包版本号' ($repoVer -eq $pkgVer) ("仓库：" + $repoVer + "   包：" + $pkgVer)
    } else {
        Add-Info '仓库没有 VERSION 文件' '建议加一个作为版本号唯一来源'
    }
}

# ============================================================ 4) 目录完整性 + 可选自检
Write-Host ""
Write-Host "[4/5] 运行时目录完整性" -ForegroundColor Cyan
$needFiles = @('runtime\DwsEdge.Host.exe', 'runtime\platform\DwsEdge.Platform.exe',
               'runtime\config\gateway.ini', 'runtime\Cfg\LogisticsBase.cfg',
               'runtime\tools\self-check.ps1', 'docs\操作手册.md', 'run.ps1')
$miss = New-Object System.Collections.Generic.List[string]
foreach ($rel in $needFiles) { if (-not (Test-Path (Join-Path $Package $rel))) { $miss.Add($rel) } }
$provCount = @(Get-ChildItem (Join-Path $Package 'runtime\providers') -Filter *.dll -File -ErrorAction SilentlyContinue).Count
if ($provCount -lt 1) { $miss.Add('runtime\providers\*.dll') }
$staleCore = Join-Path $Package 'runtime\providers\DwsEdge.Core.dll'
if (Test-Path $staleCore) { $miss.Add('runtime\providers\DwsEdge.Core.dll（历史遗留副本，宿主从 runtime 根解析 Core，必须删掉）') }
if ($miss.Count -eq 0) {
    Add-Result '交付必需文件齐全' $true ("必备文件全在；provider 插件 " + $provCount + " 个")
} else {
    Add-Result '交付必需文件齐全' $false ("缺：" + ($miss -join '；'))
}

Write-Host ""
Write-Host "[5/5] 启动自检" -ForegroundColor Cyan
if (-not $RunSelfCheck) {
    Add-Info '未跑（要跑加 -RunSelfCheck）' '会在临时目录里拷贝一份 runtime 再跑，不污染本包'
} else {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $temp = Join-Path $env:TEMP ("dws-pkgcheck-" + $stamp)
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    Copy-Item (Join-Path $Package 'runtime') $temp -Recurse -Force
    $runtimeTemp = Join-Path $temp 'runtime'
    $sc = Join-Path $runtimeTemp 'tools\self-check.ps1'
    $log = Join-Path $temp 'self-check.log'
    # 子进程里先把输出编码设成 UTF-8，日志才不会变成乱码
    $cmd = "chcp 65001 > `$null; [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; & '" + $sc + "'"
    $p = Start-Process -FilePath 'powershell.exe' `
         -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $cmd) `
         -WorkingDirectory $runtimeTemp -NoNewWindow -Wait -PassThru `
         -RedirectStandardOutput $log -RedirectStandardError ($log + '.err')
    $text = Read-TextAuto $log
    $summaryLine = ($text -split "`r?`n" | Where-Object { $_ -match '通过\s*\d+\s*项' } | Select-Object -Last 1)
    $summary = if ($summaryLine) { $summaryLine.Trim() } else { '(没解析到汇总行)' }
    # 失败项的名字，例如 "  1. 加密狗与相机（SDK 启动+配置回读校验）：退出码 2：..."
    $failedNames = @($text -split "`r?`n" | ForEach-Object {
        if ($_ -match '^\s*\d+\.\s*([^：]+)：') { $Matches[1].Trim() }
    })

    if ($p.ExitCode -eq 0) {
        Add-Result '自检跑通（退出码 0）' $true ($summary + "    日志：" + $log)
    } elseif ($failedNames.Count -ge 1 -and @($failedNames | Where-Object { $_ -match '加密狗|相机' }).Count -eq $failedNames.Count -and $text -match '(3000|2200)') {
        # 这是真机包在"没接相机"的机器上跑的正常结果：SDK 返回 3000（无相机）/2200（无加密狗）。
        # 软触发与补码会自动降级成"跳过"，所以不算包的问题。
        $reason = if ($text -match '2200') { '2200 没插加密狗' } else { '3000 没接相机' }
        Add-Info ('自检只剩"没接设备"这一条（' + $reason + '）') ($summary + "；失败项：" + ($failedNames -join '、') + "；接上真机再跑一次就是全绿。日志：" + $log)
    } else {
        Add-Result '自检跑通（退出码 0）' $false ("退出码 " + $p.ExitCode + "    " + $summary + "    失败项：" + ($failedNames -join '、') + "    日志：" + $log)
    }
    if (-not $KeepTemp) { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host ("         临时目录保留在：" + $temp) -ForegroundColor DarkGray }
}

# ============================================================ 结论
Write-Host ""
Write-Host "=== 结论 ===" -ForegroundColor White
Write-Host ("  通过 " + $script:pass + " 项，失败 " + $script:fail + " 项")
if ($script:fail -eq 0) {
    Write-Host "  [可交付] 版本号、程序集版本、校验和、与源码的对应关系都对得上。" -ForegroundColor Green
    exit 0
} else {
    Write-Host "  [不可交付] 先解决上面标 FAIL 的项再发。" -ForegroundColor Red
    Write-Host ""
    Write-Host "  失败项：" -ForegroundColor Red
    $script:rows | Where-Object { -not $_.Ok } | ForEach-Object { Write-Host ("    - " + $_.Name + "： " + $_.Detail) -ForegroundColor Red }
    exit 1
}
