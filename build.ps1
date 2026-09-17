<#
    编译 DWS Edge Min —— 一个解决方案、两个可执行进程

      采集宿主（Edge）  : DwsEdge.Host.exe        net48 / x64   ← 加载大华 SDK
      业务平台（Platform）: DwsEdge.Platform.exe    net10.0       ← API + 实时推送 + 前端
      插件               : providers\*.dll         net48
      契约库             : DwsEdge.Core.dll        net48 + net10.0 两个目标

    依赖：.NET SDK 10（dotnet 命令）+ .NET Framework 4.8 目标包

    用法：
        powershell -ExecutionPolicy Bypass -File .\build.ps1
        powershell -ExecutionPolicy Bypass -File .\build.ps1 -Offline   # 无网络环境
#>
param(
    [string]$Configuration = "Release",
    [string]$RuntimeDir = (Join-Path $PSScriptRoot 'runtime'),
    [switch]$Offline,
    [switch]$ForceConfig
)

$ErrorActionPreference = 'Stop'

function Find-DotNet {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
        'C:\Program Files\dotnet\dotnet.exe'
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "找不到 dotnet，请先安装 .NET SDK 10"
}

function Copy-Files {
    param([string]$From, [string]$To, [string]$Filter = '*')
    if (!(Test-Path $From)) { throw "找不到编译输出目录：$From" }
    if (!(Test-Path $To)) { New-Item -ItemType Directory -Force -Path $To | Out-Null }
    Get-ChildItem -Path $From -Filter $Filter -File | Copy-Item -Destination $To -Force
}

$dotnet = Find-DotNet
$srcDir = Join-Path $PSScriptRoot 'src'

$projCore     = Join-Path $srcDir 'DwsEdge.Core\DwsEdge.Core.csproj'
$projHost     = Join-Path $srcDir 'DwsEdge.Host\DwsEdge.Host.csproj'
$projDahua    = Join-Path $srcDir 'DwsEdge.Providers.Dahua\DwsEdge.Providers.Dahua.csproj'
$projSim      = Join-Path $srcDir 'DwsEdge.Providers.Simulator\DwsEdge.Providers.Simulator.csproj'
$projPlatform = Join-Path $srcDir 'DwsEdge.Platform\DwsEdge.Platform.csproj'

if (!(Test-Path $RuntimeDir)) {
    throw "找不到 runtime 目录：$RuntimeDir`r`n请先把大华 SDK 的 bin\Release\x64 内容拷进 runtime\（详见 README.md）"
}

# 离线模式：清空 NuGet 源、把 CLI 的临时目录放到仓库内，避免访问用户目录
$extraArgs = @()
if ($Offline) {
    $nugetConfig = Join-Path $PSScriptRoot 'NuGet.offline.config'
    if (!(Test-Path $nugetConfig)) {
        Set-Content -Path $nugetConfig -Encoding UTF8 -Value '<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear /></packageSources></configuration>'
    }
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
    New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null
    $extraArgs += @('--configfile', $nugetConfig)
}

function Build-Project {
    param([string]$Project, [string]$Label)
    Write-Host "  编译 $Label" -ForegroundColor DarkGray
    & $dotnet build $Project -c $Configuration --nologo @extraArgs
    if ($LASTEXITCODE -ne 0) { throw "编译失败：$Label" }
}

Write-Host "使用 dotnet: $dotnet"
Build-Project -Project $projCore     -Label "DwsEdge.Core（net48 + net10.0）"
Build-Project -Project $projDahua    -Label "DwsEdge.Providers.Dahua（net48/x64）"
Build-Project -Project $projSim      -Label "DwsEdge.Providers.Simulator（net48）"
Build-Project -Project $projHost     -Label "DwsEdge.Host 采集宿主（net48/x64）"
Build-Project -Project $projPlatform -Label "DwsEdge.Platform 业务平台（net10.0）"

$providerDir = Join-Path $RuntimeDir 'providers'
$platformDir = Join-Path $RuntimeDir 'platform'
New-Item -ItemType Directory -Force -Path $providerDir | Out-Null
New-Item -ItemType Directory -Force -Path $platformDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $RuntimeDir 'config') | Out-Null

Write-Host "  拷贝产物到 runtime" -ForegroundColor DarkGray

# 采集宿主（含 net48 版 Core）
Copy-Files -From (Join-Path $srcDir "DwsEdge.Host\bin\$Configuration\net48") -To $RuntimeDir
Copy-Files -From (Join-Path $srcDir "DwsEdge.Core\bin\$Configuration\net48") -To $RuntimeDir

# 插件
Copy-Files -From (Join-Path $srcDir "DwsEdge.Providers.Dahua\bin\$Configuration\net48") -To $providerDir -Filter 'DwsEdge.Providers.Dahua.*'
Copy-Files -From (Join-Path $srcDir "DwsEdge.Providers.Simulator\bin\$Configuration\net48") -To $providerDir -Filter 'DwsEdge.Providers.Simulator.*'

# 业务平台（含 net10 版 Core，整目录拷贝）
$platformOut = Join-Path $srcDir "DwsEdge.Platform\bin\$Configuration\net10.0"
if (!(Test-Path $platformOut)) { throw "找不到平台输出目录：$platformOut" }
Copy-Item -Path (Join-Path $platformOut '*') -Destination $platformDir -Recurse -Force

# wwwroot 属于「静态 Web 资产」，不会出现在 bin 输出里，必须单独拷贝
$wwwroot = Join-Path $srcDir 'DwsEdge.Platform\wwwroot'
if (Test-Path $wwwroot) {
    Copy-Item -Path $wwwroot -Destination $platformDir -Recurse -Force
}

# 配置：runtime\config\gateway.ini 是"现场配置"（provider、存图策略、软触发开关都在里面），
# 已经被改过时不能默默覆盖，否则一键应用/现场调试的设置会被一次编译冲掉。
$configSource = Join-Path $PSScriptRoot 'config\gateway.ini'
$configTarget = Join-Path $RuntimeDir 'config\gateway.ini'
if ((Test-Path $configTarget) -and !$ForceConfig) {
    Write-Host "  已存在 runtime\config\gateway.ini，保留现场配置（要覆盖请加 -ForceConfig）" -ForegroundColor Yellow
}
else {
    if (Test-Path $configTarget) {
        $configBackup = "$configTarget.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
        Copy-Item -LiteralPath $configTarget -Destination $configBackup -Force
        Write-Host "  原 gateway.ini 已备份：$configBackup" -ForegroundColor DarkGray
    }
    Copy-Item $configSource $configTarget -Force
    Write-Host "  已写入 runtime\config\gateway.ini" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "编译完成。" -ForegroundColor Green
Write-Host "  采集宿主： runtime\DwsEdge.Host.exe"
Write-Host "  业务平台： runtime\platform\DwsEdge.Platform.exe"
Write-Host ""
Write-Host "  只跑采集：   .\run.ps1 -TriggerOnce -Duration 8"
Write-Host "  只跑平台：   .\run.ps1 -Platform"
Write-Host "  两个一起跑： .\run.ps1 -Both -TriggerOnce"
