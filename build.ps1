<#
    编译 DWS Edge Min。

    不依赖 Visual Studio，只用 .NET Framework 自带的 csc.exe（Win10/11 默认就有）。
    输出：
        runtime\DwsEdge.Core.dll
        runtime\DwsEdge.Host.exe
        runtime\providers\DwsEdge.Providers.Dahua.dll

    用法：
        powershell -ExecutionPolicy Bypass -File .\build.ps1
#>
param(
    [string]$RuntimeDir = (Join-Path $PSScriptRoot 'runtime')
)

$ErrorActionPreference = 'Stop'

function Find-Csc {
    $candidates = @(
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
        "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }
    throw "找不到 csc.exe，请确认已安装 .NET Framework 4.x"
}

$csc      = Find-Csc
$srcDir   = Join-Path $PSScriptRoot 'src'
$coreDir  = Join-Path $srcDir 'DwsEdge.Core'
$dahuaDir = Join-Path $srcDir 'DwsEdge.Providers.Dahua'
$simDir   = Join-Path $srcDir 'DwsEdge.Providers.Simulator'
$hostDir  = Join-Path $srcDir 'DwsEdge.Host'

if (!(Test-Path $RuntimeDir)) {
    throw "找不到 runtime 目录：$RuntimeDir`r`n请先把大华 SDK 的 bin\Release\x64 内容拷进 runtime\（详见 README.md）"
}

$providerDir = Join-Path $RuntimeDir 'providers'
New-Item -ItemType Directory -Force -Path $providerDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $RuntimeDir 'config') | Out-Null

Write-Host "使用编译器: $csc"

function Get-SourceFiles {
    param([string]$Root)
    # 排除 obj/bin 里 MSBuild 自动生成的 .cs（例如 .NETFramework,Version=v4.8.AssemblyAttributes.cs）
    return @(Get-ChildItem -Recurse -Path $Root -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\bin\\' } |
        ForEach-Object { $_.FullName })
}

# 1) 厂商无关核心
$coreFiles = Get-SourceFiles -Root $coreDir
& $csc /nologo /target:library /platform:anycpu /out:"$RuntimeDir\DwsEdge.Core.dll" /r:System.dll /r:System.Core.dll $coreFiles
if ($LASTEXITCODE -ne 0) { throw "编译 DwsEdge.Core 失败" }
Write-Host "  [1/4] DwsEdge.Core.dll" -ForegroundColor Green

# 2) 大华 provider（唯一引用厂商 DLL 的程序集）
$sdkDll = Join-Path $RuntimeDir 'LogisticsBaseCSharp.dll'
if (!(Test-Path $sdkDll)) {
    throw "runtime 目录里缺少 LogisticsBaseCSharp.dll（请确认已拷入大华 SDK 的 bin\Release\x64 内容）"
}

$dahuaFiles = Get-SourceFiles -Root $dahuaDir
& $csc /nologo /target:library /platform:x64 /out:"$providerDir\DwsEdge.Providers.Dahua.dll" /r:System.dll /r:System.Core.dll /r:"$sdkDll" /r:"$RuntimeDir\DwsEdge.Core.dll" $dahuaFiles
if ($LASTEXITCODE -ne 0) { throw "编译 DwsEdge.Providers.Dahua 失败" }
Write-Host "  [2/4] providers\DwsEdge.Providers.Dahua.dll" -ForegroundColor Green

# 3) 测试用模拟 provider（不依赖相机/加密狗）
$simFiles = Get-SourceFiles -Root $simDir
& $csc /nologo /target:library /platform:anycpu /out:"$providerDir\DwsEdge.Providers.Simulator.dll" /r:System.dll /r:System.Core.dll /r:"$RuntimeDir\DwsEdge.Core.dll" $simFiles
if ($LASTEXITCODE -ne 0) { throw "编译 DwsEdge.Providers.Simulator 失败" }
Write-Host "  [3/4] providers\DwsEdge.Providers.Simulator.dll" -ForegroundColor Green

# 4) 宿主（只引用 Core，不引用任何厂商程序集）
$hostFiles = Get-SourceFiles -Root $hostDir
& $csc /nologo /target:exe /platform:x64 /out:"$RuntimeDir\DwsEdge.Host.exe" /r:System.dll /r:System.Core.dll /r:"$RuntimeDir\DwsEdge.Core.dll" $hostFiles
if ($LASTEXITCODE -ne 0) { throw "编译 DwsEdge.Host 失败" }
Write-Host "  [4/4] DwsEdge.Host.exe" -ForegroundColor Green

# 4) 拷贝配置
Copy-Item (Join-Path $PSScriptRoot 'config\gateway.ini') (Join-Path $RuntimeDir 'config\gateway.ini') -Force

Write-Host ""
Write-Host "编译完成。" -ForegroundColor Green
Write-Host "  运行： .\run.ps1              （常驻，Ctrl+C 停止）"
Write-Host "  冒烟： .\run.ps1 -Duration 30 （跑 30 秒自动退出）"
Write-Host "  软触发： .\run.ps1 -TriggerOnce -Duration 8"
Write-Host "  模拟器： provider 改成 simulator 后，.\run.ps1 -TriggerOnce -Duration 8"
