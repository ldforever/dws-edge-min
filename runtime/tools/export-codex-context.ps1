<#
    export-codex-context.ps1 —— 换机器时，把本机 Codex 的会话上下文打包带走。

    背景：
      Codex 的**桌面对话（thread）默认只存在本机的 %USERPROFILE%\.codex\ 下**
      （sessions\rollout-*.jsonl + thread_history_1.sqlite + state_5.sqlite + session_index.jsonl），
      它不跟随账号自动同步。要在另一台机器上继续这段对话，就得把这些文件搬过去。

      注意：**默认不打包 auth.json**（那是登录凭据）——建议在新机器上重新登录账号，更安全。
      确实想连登录态一起搬，再显式加 -IncludeAuth。

    用法（**先完全退出 Codex**，否则 sqlite 有 -wal，拷出来可能不一致）：
        powershell -ExecutionPolicy Bypass -File .\tools\export-codex-context.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\export-codex-context.ps1 -IncludeAuth
        powershell -ExecutionPolicy Bypass -File .\tools\export-codex-context.ps1 -OutDir D:\迁移

    产出一个 zip，里面是 .codex\ 的相对路径结构；新机器上解压覆盖到 %USERPROFILE%\.codex\ 即可
    （**覆盖前先把新机器原来的 .codex 备份**）。
#>
param(
    [string]$OutDir = '',
    [switch]$IncludeAuth,
    [switch]$SkipRunningCheck
)

$ErrorActionPreference = 'Stop'

$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
if (!(Test-Path $codexHome)) { throw "找不到 Codex 数据目录：$codexHome" }

# 1) 尽量确认 Codex 已退出（写 sqlite 的时候拷会拿到半截数据）
if (!$SkipRunningCheck) {
    $running = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -match '^(Codex|codex)$' })
    if ($running.Count -gt 0) {
        Write-Host '检测到 Codex 还在运行：' -ForegroundColor Yellow
        $running | ForEach-Object { Write-Host ('  PID ' + $_.Id + '  ' + $_.ProcessName) }
        Write-Host '请先完全退出 Codex（含托盘图标），再重跑本脚本；确实要现在拷就加 -SkipRunningCheck。' -ForegroundColor Yellow
        throw 'Codex 正在运行，已中止（避免拷出不一致的会话数据）'
    }
}

if ([string]::IsNullOrEmpty($OutDir)) {
    $OutDir = Join-Path ([Environment]::GetFolderPath('Desktop')) 'codex-context-export'
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path $OutDir ("codex-context-" + $stamp)
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# 2) 要带走的东西：会话与索引（不含凭据）
$items = @(
    'sessions',                    # 对话正文（rollout-*.jsonl）
    'archived_sessions',           # 归档过的对话
    'thread_history_1.sqlite',     # 线程历史库
    'thread_history_1.sqlite-wal',
    'thread_history_1.sqlite-shm',
    'state_5.sqlite',
    'state_5.sqlite-wal',
    'state_5.sqlite-shm',
    'session_index.jsonl',
    'config.toml',                 # 本机设置（可选，建议一起带走）
    'memories_1.sqlite',           # 记忆库（如果有）
    'goals_1.sqlite'
)
if ($IncludeAuth) { $items += 'auth.json' }

$copied = New-Object System.Collections.Generic.List[string]
foreach ($item in $items) {
    $src = Join-Path $codexHome $item
    if (!(Test-Path $src)) { continue }
    $dst = Join-Path $stage $item
    if ((Get-Item $src).PSIsContainer) {
        Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
    }
    else {
        Copy-Item -LiteralPath $src -Destination $dst -Force
    }
    $copied.Add($item)
}

# 3) 附一份说明，免得在新机器上不知道该怎么用
$readme = @"
Codex 会话迁移包（$stamp）
============================================================
来源机器：$env:COMPUTERNAME
来源目录：$codexHome
包含内容：$($copied -join ', ')
$(if ($IncludeAuth) { "含 auth.json（登录凭据）—— 请当密码文件保管！" } else { "未含 auth.json —— 到新机器上重新登录账号即可。" })

在新机器上恢复：
  1) 先完全退出 Codex；
  2) 把新机器原来的 %USERPROFILE%\.codex 改名备份（例如 .codex.bak-<日期>）；
  3) 把本包里的 sessions\ 等文件按同样结构拷进 %USERPROFILE%\.codex\；
  4) 启动 Codex，历史对话就在了。

提示：真正的"项目上下文"建议放在代码仓库里（见仓库根 HANDOFF.md）——
      代码 + 文档 + 提交记录才是换人换机器都不会丢的上下文。
"@
[System.IO.File]::WriteAllText((Join-Path $stage 'README-迁移说明.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))

# 4) 打包
$zip = Join-Path $OutDir ("codex-context-" + $stamp + '.zip')
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ''
Write-Host ('已导出：' + $zip + '（' + $sizeMb + ' MB）') -ForegroundColor Green
Write-Host ('  包含：' + ($copied -join ', '))
Write-Host ('  临时目录（可删）：' + $stage) -ForegroundColor DarkGray
