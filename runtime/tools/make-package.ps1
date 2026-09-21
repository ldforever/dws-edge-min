<#
    出厂打包：产出一个"干净、可交付"的形态 A 版本（控制台进程 + 计划任务开机自启）。

    做四件事：
      1) 复制 runtime（排除测试数据与缓存）；
      2) **洗净**：删掉测试产生的账号/密码文件/平台配置/历史/日志/spool/图片/模板/备份，
         只留下"出厂状态"（现场首次启动会自己生成账号与随机初始密码）；
      3) 按参数决定 gateway.ini 的 provider（真机 dahua-dws / 演示 simulator）；
      4) 补齐交付物：docs、VERSION.txt、CHANGELOG.md、交付记录.md、checksums.txt、
         tools\install-autostart.ps1 / uninstall-autostart.ps1，以及（如果能找到）.NET 运行时安装包。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\make-package.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\make-package.ps1 -Provider simulator   # 做演示包
        powershell -ExecutionPolicy Bypass -File .\tools\make-package.ps1 -OutputDir D:\pkg\DWS
#>
param(
    [string]$SourceRuntime = '',
    [string]$OutputDir = '',
    [string]$Version = '',              # 留空则读仓库根 VERSION 文件（单一来源）
    [ValidateSet('dahua-dws', 'simulator')][string]$Provider = 'dahua-dws',
    [switch]$KeepCameras,               # 保留源 runtime 里的相机清单（默认换成 1 台示例，避免泄露上一台设备的 IP/序列号）
    [string]$DotnetInstaller = ''      # 可指定 .NET 运行时安装包路径；留空则自动找
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if (![System.IO.Path]::IsPathRooted($SourceRuntime)) { $SourceRuntime = [System.IO.Path]::GetFullPath($SourceRuntime) }

# 版本号：优先命令行参数，其次仓库根 VERSION 文件
if ([string]::IsNullOrEmpty($Version)) {
    $versionFile = Join-Path $repoRoot 'VERSION'
    if (Test-Path $versionFile) { $Version = (Get-Content $versionFile -Raw -Encoding UTF8).Trim() }
}
if ([string]::IsNullOrEmpty($Version)) { $Version = 'V1.0.0' }
if ($Version -notmatch '^[Vv]') { $Version = 'V' + $Version }

$stamp = Get-Date -Format 'yyyyMMdd'
if ([string]::IsNullOrEmpty($OutputDir)) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $OutputDir = Join-Path $desktop ("DWS-Edge-Min_" + $Version + "_" + $stamp)
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

if (!(Test-Path (Join-Path $SourceRuntime 'DwsEdge.Host.exe'))) {
    throw "源 runtime 不完整：找不到 DwsEdge.Host.exe（$SourceRuntime）"
}

Write-Host "============================================================"
Write-Host " DWS Edge Min 出厂打包（形态 A：控制台进程 + 计划任务）"
Write-Host " 源运行时：$SourceRuntime"
Write-Host " 输出目录：$OutputDir"
Write-Host " provider：$Provider"
Write-Host "============================================================"

if (Test-Path $OutputDir) {
    $old = "$OutputDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Write-Host "  已存在的同目录先挪走：$old" -ForegroundColor Yellow
    Move-Item -LiteralPath $OutputDir -Destination $old -Force
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# ---------------------------------------------------------------- 1) 复制 runtime
$dstRuntime = Join-Path $OutputDir 'runtime'
Write-Host "`n[1/6] 复制 runtime（排除测试数据/缓存）…" -ForegroundColor Cyan
robocopy $SourceRuntime $dstRuntime /E /XD spool data images logs Log cache bin obj .vs .packages .dotnet-home work `
    /XF *.pdb /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $dstRuntime 'spool'), (Join-Path $dstRuntime 'data'), `
    (Join-Path $dstRuntime 'images'), (Join-Path $dstRuntime 'logs'), (Join-Path $dstRuntime 'Log') | Out-Null

# ---------------------------------------------------------------- 2) 洗净（关键）
Write-Host "[2/6] 洗净：删掉测试痕迹，只留出厂状态…" -ForegroundColor Cyan
$removed = New-Object System.Collections.Generic.List[string]

# 2.1 平台侧配置：全部回到"首次启动自动生成"
$configDir = Join-Path $dstRuntime 'config'
$platformConfigs = @(
    'auth.json', 'auth.json.bak-*', 'users.json', 'users.json.bak-*', 'admin-initial-password.txt',
    'monitor.json', 'monitor.json.bak-*', 'downstream.json', 'downstream.json.bak-*',
    'barcode-rules.json', 'barcode-rules.json.bak-*', 'shifts.json', 'shifts.json.bak-*',
    'camera-positions.ini.bak-*', 'cameras-applied.txt',
    # 相机 IP↔Key 对照表：现场首次启动会自动从 SDK 日志重建，出厂包不带走上一台设备的 IP/序列号
    'camera-identity.ini', 'camera-identity.ini.bak-*'
)
foreach ($pattern in $platformConfigs) {
    Get-ChildItem -Path $configDir -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
        $removed.Add('config\' + $_.Name)
    }
}
$templateDir = Join-Path $configDir 'templates'
if (Test-Path $templateDir) {
    Get-ChildItem $templateDir -File | ForEach-Object { $removed.Add('config\templates\' + $_.Name) }
    Remove-Item -LiteralPath $templateDir -Recurse -Force
}

# 2.2 SDK 配置的历史备份（现场不需要上百个备份文件）
Get-ChildItem -Path (Join-Path $dstRuntime 'Cfg') -Include '*.bak-*', '*.rollback-*' -File -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force; $removed.Add('Cfg\' + $_.Name) }

# 2.2b providers 目录里不允许留历史版本的 DwsEdge.Core.dll：
#      宿主是从 runtime 根目录解析 Core 的，providers 里再放一份旧副本，
#      现场会冒出"程序集版本对不上"的假故障（实际用的是根目录那份）。
$staleCore = Join-Path $dstRuntime 'providers\DwsEdge.Core.dll'
if (Test-Path $staleCore) {
    Remove-Item -LiteralPath $staleCore -Force
    $removed.Add('providers\DwsEdge.Core.dll（历史遗留副本，宿主从 runtime 根解析 Core）')
}

# 2.2c 开发过程留下的中间文件（*.new / *.orig / *.rej 之类）不该进交付包：
#      它们是重构/对比时的临时产物，留在包里既不生效又容易让人误以为"页面有两个版本"。
Get-ChildItem -Path $dstRuntime -Recurse -File -Include '*.new', '*.orig', '*.rej' -ErrorAction SilentlyContinue |
    ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
        $removed.Add($_.FullName.Substring($dstRuntime.Length).TrimStart('\'))
    }

# 2.3 运行期数据（历史/去重/审计/spool/图片/日志/缩略图缓存）
foreach ($sub in @('data', 'spool', 'images', 'logs', 'Log', 'cache')) {
    $dir = Join-Path $dstRuntime $sub
    if (!(Test-Path $dir)) { continue }
    Get-ChildItem $dir -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
        $removed.Add($sub + '\' + $_.Name)
    }
}
Write-Host ("  已清理 " + $removed.Count + " 个测试/运行期文件") -ForegroundColor DarkGray

# ---------------------------------------------------------------- 3) provider 与出厂默认
Write-Host "[3/6] 设置出厂配置（provider=$Provider）…" -ForegroundColor Cyan
$gateway = Join-Path $dstRuntime 'config\gateway.ini'
$gatewayText = [System.IO.File]::ReadAllText($gateway)
$gatewayText = [regex]::Replace($gatewayText, '(?m)^provider=.*$', ('provider=' + $Provider))
[System.IO.File]::WriteAllText($gateway, $gatewayText, (New-Object System.Text.UTF8Encoding($false)))

# 平台出厂默认：轮询 100ms（满足 C1 的 300ms 延迟要求），端口 8090
$appsettings = Join-Path $dstRuntime 'platform\appsettings.json'
if (Test-Path $appsettings) {
    # 只在真的需要改的时候写回，并且**保持原来的 BOM 状态** ——
    # 否则包里的 appsettings.json 会因为少了个 BOM 而和源码产物"看起来不一致"（内容其实一样）。
    $bytes = [System.IO.File]::ReadAllBytes($appsettings)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $settings = [System.IO.File]::ReadAllText($appsettings)
    $updated = [regex]::Replace($settings, '"PollIntervalMs":\s*\d+', '"PollIntervalMs": 100')
    if ($updated -ne $settings) {
        [System.IO.File]::WriteAllText($appsettings, $updated, (New-Object System.Text.UTF8Encoding($hasBom)))
    }
}

# 相机清单示例化：交付包不该带"上一台设备"的相机 IP / 序列号
if (!$KeepCameras) {
    $makeCfg = Join-Path $dstRuntime 'tools\make-camera-cfg.ps1'
    if (Test-Path $makeCfg) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $makeCfg -Cameras 'ip=172.20.10.11' `
            -RuntimeDir $dstRuntime -Mode keep | Out-Null
        # make-camera-cfg 自己会写备份，这里再清一次，保持出厂干净
        Get-ChildItem -Path (Join-Path $dstRuntime 'Cfg') -Include '*.bak-*', '*.rollback-*' -File -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
        Write-Host "  相机清单已示例化为 1 台（ip=172.20.10.11）：交付时用配置页或 apply-config.ps1 改成现场清单" -ForegroundColor DarkGray
    }
}

# ---------------------------------------------------------------- 4) docs 与运维脚本
Write-Host "[4/6] 补齐文档与运维脚本…" -ForegroundColor Cyan
$docsOut = Join-Path $OutputDir 'docs'
New-Item -ItemType Directory -Force -Path $docsOut | Out-Null
$docsSrc = Join-Path $repoRoot 'docs'
if (Test-Path $docsSrc) {
    Copy-Item -Path (Join-Path $docsSrc '*.md') -Destination $docsOut -Force
}
$runtimeDocs = Join-Path $dstRuntime 'docs'
New-Item -ItemType Directory -Force -Path $runtimeDocs | Out-Null
if (Test-Path $docsSrc) {
    Copy-Item -Path (Join-Path $docsSrc '*.md') -Destination $runtimeDocs -Force
}
$toolsDst = Join-Path $dstRuntime 'tools'
New-Item -ItemType Directory -Force -Path $toolsDst | Out-Null
Copy-Item -Path (Join-Path $repoRoot 'tools\*.ps1') -Destination $toolsDst -Force
Copy-Item -Path (Join-Path $repoRoot 'config\cameras-17.example.txt') -Destination (Join-Path $dstRuntime 'config') -Force -ErrorAction SilentlyContinue
# run.ps1 放到包根：手册里的 .\run.ps1 用法在交付包里也要能用
Copy-Item -Path (Join-Path $repoRoot 'run.ps1') -Destination $OutputDir -Force

# ---------------------------------------------------------------- 5) 交付资料
Write-Host "[5/6] 生成 VERSION / CHANGELOG / 交付记录 / 校验和…" -ForegroundColor Cyan
$buildTime = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'

# 程序集版本 / 源码提交：从刚拷进包里的产物现读，保证"包里写的 = 包里实际的"
$hostExe = Join-Path $OutputDir 'runtime\DwsEdge.Host.exe'
$asmVersion = if (Test-Path $hostExe) { (Get-Item $hostExe).VersionInfo.FileVersion } else { '(未取到)' }
$srcCommit = '(未取到)'
if (Test-Path (Join-Path $repoRoot '.git')) {
    try {
        $c = (& git -C $repoRoot rev-parse --short HEAD 2>$null | Select-Object -First 1)
        if (-not [string]::IsNullOrWhiteSpace($c)) { $srcCommit = $c }
    } catch { }
}

$versionText = @"
DWS Edge Min 交付版本
============================================================
版本号      : $Version
程序集版本  : $asmVersion   （右键 exe → 属性 → 详细信息 可见）
源码提交    : $srcCommit
构建时间    : $buildTime
交付形态    : A —— 控制台进程 + 计划任务开机自启
provider    : $Provider$(if ($Provider -eq 'dahua-dws') { '（真机：大华智能相机）' } else { '（仿真：不需要相机和加密狗，仅演示用）' })

平台访问    : http://<本机IP>:8090   （本机可 http://127.0.0.1:8090）
运行依赖    : 采集宿主 .NET Framework 4.8（Win10/11 自带）
              业务平台 .NET 10 运行时（见本目录 dotnet* 安装包或 README-运行时.txt）

覆盖的需求项 : A1-A9 / B1-B9 / C1-C6（共 24 项）
              A5 里的"面单抠图"按需求方决定不做

现场自检     : runtime\tools\self-check.ps1        （交付包内直接跑，不用带参数）
包核验       : runtime\tools\check-package.ps1     （版本号 ↔ 程序集版本 ↔ 校验和 ↔ 源码对账）
配置命令     : runtime\tools\apply-config.ps1 / set-trigger-mode.ps1 / host-command.ps1
自启脚本     : runtime\tools\install-autostart.ps1 / uninstall-autostart.ps1
回归脚本     : runtime\tools\test-*.ps1（16 个，**出厂前在开发仓库里跑**；在交付包里跑要显式传
              -SourceRuntime <runtime 路径> -WorkDir <临时目录>，否则会把测试运行时写到包里）
操作手册     : docs\操作手册.md（runtime\docs\ 下有同一份）
============================================================
"@
[System.IO.File]::WriteAllText((Join-Path $OutputDir 'VERSION.txt'), $versionText, (New-Object System.Text.UTF8Encoding($true)))

$changelog = @"
# 变更记录

## $Version（$buildTime）

首个交付版本，覆盖 24 项需求（A1-A9 / B1-B9 / C1-C6）：

**采集侧（A）**：多相机接入（台数不写死）、断线重连与状态上报、硬触发读码、软触发与补码命令通道、
原图/面单图落盘与保留策略、厂商无关规范事件、本地事件缓冲、一键应用配置（写配置→重启校验→失败回滚）、
配置模板（另存/对比/套用）、相机清单与设备信息。

**平台侧（B）**：包裹两次回调合并与去重、条码过滤规则、历史库与 CSV 导出、
下游输出三种模式（TCP 客户端 / TCP 服务端 / HTTP 推送，含失败重传与幂等）、
图片按需访问与缩略图、相机状态监控与告警、账号登录与接口鉴权（密码失败次数限制、服务令牌、审计）。

**界面（C）**：实时过包卡片墙（缩略图/状态/实时刷新）、相机状态墙（在线率/心跳/出码计数/异常高亮）、
历史查询与导出、统计看板（按相机/班次/小时/日期）、配置页简版（相机清单/存图策略/下游/规则/阈值/备份回滚）、
日志查看与一键打包（采集日志/SDK 日志/事件缓冲/审计）。

**其它**：现场操作手册、一键自检脚本、出厂打包脚本、A4 形态的开机自启脚本。

已知边界（交付时请与客户对齐）：
* 进程为控制台模式，开机后由计划任务拉起（未做 Windows 服务）；
* 平台为内网 HTTP，未启用 TLS；
* "面单抠图"未实现（按需求方决定）；多图包裹仅保留第一张图路径。
"@
[System.IO.File]::WriteAllText((Join-Path $OutputDir 'CHANGELOG.md'), $changelog, (New-Object System.Text.UTF8Encoding($true)))

$record = @"
# 交付记录（每台设备一份，交付时填写）

## 一、设备与软件

| 项目 | 内容 |
|---|---|
| 设备编号 / 场地 | （填写） |
| 上位机型号 / 序列号 | （填写） |
| 操作系统 | （填写） |
| 软件版本 | $Version（$buildTime 构建） |
| 程序集版本 | $asmVersion（应等于软件版本去掉 V） |
| 源码提交 | $srcCommit |
| 交付形态 | A：控制台进程 + 计划任务开机自启 |
| 安装路径 | （填写，例如 D:\dws） |

## 二、网络与端口（务必填写，后续远程支持要用）

| 项目 | 内容 |
|---|---|
| 板卡（管理网）IP | （填写） |
| 相机网段 | （填写，例如 172.20.10.0/24） |
| 平台访问地址 | http://（管理网 IP）:8090 |
| 下游对接方式 | TCP 客户端 / TCP 服务端 / HTTP（选一） |
| 下游地址 : 端口 | （填写） |
| 平台是否做服务端 | 是 / 否（填端口） |

## 三、相机与配置

| 项目 | 内容 |
|---|---|
| 相机台数 / 型号 | （填写，例如 17 台 / 大华 智能相机） |
| 触发方式 | 硬触发（光电）/ 软触发 / 自由拉流 |
| 相机清单文件 | runtime\Cfg\LogisticsBase.cfg（已按现场改好） |
| 图片目录与保留天数 | （填写，例如 D:\dws-images / 15 天） |
| 磁盘水位上限 | （填写，建议 80-85） |
| 是否配置条码规则 | 否 / 是（简述） |
| 班次设置 | 默认两班 / 自定义（填） |

## 四、账号与服务令牌

| 项目 | 内容 |
|---|---|
| 管理员账号 | admin（初始密码见 config\admin-initial-password.txt，**已改密后请删除该文件**） |
| 现场账号 | （填写：operator / viewer 各几个） |
| 服务令牌 | 保存在 runtime\config\auth.json 的 serviceKey（集成方需要时提供） |

## 五、交付验收（逐项打勾）

- [ ] runtime\tools\self-check.ps1 无失败项
- [ ] 实时页能看到过包卡片、缩略图能点开原图
- [ ] 相机状态墙在线数与实际一致；拔网线能在阈值内看到告警
- [ ] 历史查询与 CSV 导出正常（Excel 不乱码）
- [ ] 下游对接收到了真实包裹（测试连接 + 实包验证）
- [ ] 计划任务已安装，重启设备后两个进程自动拉起
- [ ] 初密码已修改；现场账号已建立
- [ ] 操作手册已交付并简单讲解

## 六、签字

| 角色 | 姓名 | 日期 |
|---|---|---|
| 交付人 | | |
| 现场负责人 | | |
"@
[System.IO.File]::WriteAllText((Join-Path $OutputDir '交付记录.md'), $record, (New-Object System.Text.UTF8Encoding($true)))

# .NET 运行时安装包
$dotnetNote = Join-Path $OutputDir 'README-运行时.txt'
$installer = ''
if (![string]::IsNullOrEmpty($DotnetInstaller) -and (Test-Path $DotnetInstaller)) {
    $installer = Join-Path $OutputDir (Split-Path -Leaf $DotnetInstaller)
    Copy-Item $DotnetInstaller $installer -Force
} else {
    $candidates = @()
    $desktop = [Environment]::GetFolderPath('Desktop')
    $candidates += (Get-ChildItem -Path $desktop -Filter 'dotnet-runtime-*win-x64*.exe' -ErrorAction SilentlyContinue)
    $candidates += (Get-ChildItem -Path (Join-Path $repoRoot 'packages') -Filter 'dotnet-runtime-*win-x64*.exe' -ErrorAction SilentlyContinue)
    if ($candidates.Count -gt 0) {
        $installer = Join-Path $OutputDir $candidates[0].Name
        Copy-Item $candidates[0].FullName $installer -Force
    }
}
$runtimeNote = @"
运行环境说明
============================================================
采集宿主：.NET Framework 4.8 —— Windows 10/11 自带，不用装。

业务平台：需要 .NET 10 运行时（ASP.NET Core Runtime 10）。
$(if ($installer) { "  本包已附带安装包：" + (Split-Path -Leaf $installer) + "（双击安装即可）" }
  else { "  本包未附带安装包。请在目标机器安装 ASP.NET Core Runtime 10（x64），" + "`r`n" + "  或用离线安装包：dotnet-runtime-10.x.x-win-x64.exe（微软官网下载）。`r`n" + "  检查是否已装：命令行执行 dotnet --list-runtimes，看到 Microsoft.AspNetCore.App 10.x 即可。" })

启动方式（形态 A）：
  1) 先跑一次 runtime\tools\self-check.ps1 自检；
  2) 双击或用计划任务启动 runtime\DwsEdge.Host.exe（采集宿主）
     与 runtime\platform\DwsEdge.Platform.exe（业务平台）；
  3) 浏览器打开 http://<本机IP>:8090（本机可用 http://127.0.0.1:8090）；
  4) 首次运行的初始密码在 runtime\config\admin-initial-password.txt。

开机自启：用管理员 PowerShell 执行
  powershell -ExecutionPolicy Bypass -File runtime\tools\install-autostart.ps1
卸载自启：
  powershell -ExecutionPolicy Bypass -File runtime\tools\uninstall-autostart.ps1

界面外壳（可选，套壳/kiosk）：
  runtime\DwsEdge.Shell.exe            双击即用：自动拉起宿主+平台，再用 Edge 应用模式打开界面（无地址栏）
  runtime\DwsEdge.Shell.exe -Kiosk     全屏 kiosk（大屏/一体机）
  runtime\DwsEdge.Shell.exe -SelfTest  自检（探平台、找 Edge、打印将执行的命令行，不开窗口）
  套壳一体机自启：install-autostart.ps1 -Shell
  外壳与平台都不需要额外运行时：宿主/外壳用系统自带的 .NET Framework 4.8，页面用系统自带的 Edge。
============================================================
"@
[System.IO.File]::WriteAllText($dotnetNote, $runtimeNote, (New-Object System.Text.UTF8Encoding($true)))

# 校验和（只对"我们自己产出的"关键文件）
$keyFiles = @(
    'runtime\DwsEdge.Host.exe', 'runtime\DwsEdge.Shell.exe', 'runtime\DwsEdge.Core.dll',
    'runtime\platform\DwsEdge.Platform.exe', 'runtime\platform\DwsEdge.Platform.dll',
    'runtime\platform\appsettings.json', 'runtime\platform\wwwroot\index.html',
    'runtime\Cfg\LogisticsBase.cfg', 'runtime\config\gateway.ini',
    'VERSION.txt'
)
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# DWS Edge Min $Version 关键文件校验和（SHA256）")
$lines.Add("# 生成时间：$buildTime")
foreach ($rel in $keyFiles) {
    $full = Join-Path $OutputDir $rel
    if (Test-Path $full) {
        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
        $lines.Add(($hash + "  " + $rel))
    }
}
Get-ChildItem (Join-Path $dstRuntime 'tools') -Filter '*.ps1' -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object {
    $lines.Add(((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + "  runtime\tools\" + $_.Name))
}
[System.IO.File]::WriteAllText((Join-Path $OutputDir 'checksums.txt'), ($lines -join "`r`n"), (New-Object System.Text.UTF8Encoding($true)))

# ---------------------------------------------------------------- 6) 汇总
Write-Host "`n[6/6] 交付包汇总" -ForegroundColor Cyan
$sizeFiles = Get-ChildItem $dstRuntime -Recurse -File -ErrorAction SilentlyContinue
$sizeMb = [math]::Round((($sizeFiles | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
$leftovers = @()
foreach ($bad in @('config\auth.json', 'config\users.json', 'config\admin-initial-password.txt',
    'config\monitor.json', 'config\downstream.json', 'config\barcode-rules.json', 'config\shifts.json')) {
    if (Test-Path (Join-Path $dstRuntime $bad)) { $leftovers += $bad }
}

Write-Host ("  交付目录   : " + $OutputDir)
Write-Host ("  runtime 文件: " + $sizeFiles.Count + " 个 / " + $sizeMb + " MB")
Write-Host ("  已清理文件 : " + $removed.Count + " 个")
Write-Host ("  provider   : " + $Provider)
if ($leftovers.Count -eq 0) {
    Write-Host "  洁净度检查 : PASS（没有残留账号 / 平台配置 / 测试数据）" -ForegroundColor Green
} else {
    Write-Host ("  洁净度检查 : 仍有残留 " + ($leftovers -join ', ')) -ForegroundColor Red
}
Write-Host ""
Write-Host "  包内结构：" -ForegroundColor DarkGray
Write-Host "    runtime\            采集宿主 + 平台 + tools + docs"
Write-Host "    docs\               操作手册等文档"
Write-Host "    VERSION.txt         版本与访问方式"
Write-Host "    CHANGELOG.md        本版变更"
Write-Host "    交付记录.md          每台一份，交付时填写并签字"
Write-Host "    checksums.txt       关键文件校验和"
Write-Host "    README-运行时.txt    .NET 运行时要求与启动步骤"
