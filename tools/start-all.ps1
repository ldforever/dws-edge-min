<#
    隐藏启动采集宿主与业务平台（不弹黑窗口），并把日志重定向到 logs\ 下。

    什么时候用它：
      * 现场不想看到两个黑窗口，又不想马上做 Windows 服务；
      * 开机自启（install-autostart.ps1 默认就是调它）；
      * 远程/无人值守：启动后窗口不会因为你关掉远程桌面而受影响。

    配套：tools\stop-all.ps1 停止；状态看 logs\pids.json 与 logs\host-*.log、logs\platform-*.log。

    用法：
        powershell -ExecutionPolicy Bypass -File .\runtime\tools\start-all.ps1
        powershell -ExecutionPolicy Bypass -File .\runtime\tools\start-all.ps1 -NoPlatform     # 只起采集宿主
        powershell -ExecutionPolicy Bypass -File .\runtime\tools\start-all.ps1 -ShowWindow     # 想看着日志跑
#>
param(
    [string]$RuntimeDir = '',
    [switch]$NoHost,
    [switch]$NoPlatform,
    [switch]$ShowWindow          # 保留黑窗口（调试用），默认隐藏
)

$ErrorActionPreference = 'Stop'

# 兼容两种布局：<仓库>\tools\ → <仓库>\runtime；交付包 <runtime>\tools\ → <runtime>
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($RuntimeDir)) {
    $parentDir = Split-Path -Parent $scriptDir
    foreach ($candidate in @((Join-Path $parentDir 'runtime'), $parentDir)) {
        if (Test-Path (Join-Path $candidate 'DwsEdge.Host.exe')) { $RuntimeDir = $candidate; break }
    }
    if ([string]::IsNullOrEmpty($RuntimeDir)) { $RuntimeDir = Join-Path $parentDir 'runtime' }
}
$RuntimeDir = [System.IO.Path]::GetFullPath($RuntimeDir)

$hostExe = Join-Path $RuntimeDir 'DwsEdge.Host.exe'
$platformExe = Join-Path $RuntimeDir 'platform\DwsEdge.Platform.exe'
$logsDir = Join-Path $RuntimeDir 'logs'
New-Item -ItemType Directory -Force -Path $logsDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Test-AlreadyRunning {
    param([string]$ExePath)
    # 只认采集宿主与业务平台这两个进程！
    #   以前这里写的是 ProcessName -like 'DwsEdge*'，把"界面外壳 DwsEdge.Shell"也算成了在跑，
    #   于是外壳来拉宿主+平台时，本脚本认为"已经有人跑了"直接退出 —— 外壳就只能干等到超时。
    $found = Get-Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Path -and ($_.Path -like ($RuntimeDir + '*')) -and
            ($_.ProcessName -eq 'DwsEdge.Host' -or $_.ProcessName -eq 'DwsEdge.Platform')
        }
    return @($found)
}

$running = Test-AlreadyRunning -ExePath $RuntimeDir
if ($running.Count -gt 0) {
    Write-Host "已有 DWS 进程在跑（先停止再启动）：" -ForegroundColor Yellow
    $running | ForEach-Object { Write-Host ("  PID " + $_.Id + "  " + $_.ProcessName) }
    Write-Host "  停止请用：tools\stop-all.ps1" -ForegroundColor DarkGray
    exit 1
}

$started = New-Object System.Collections.Generic.List[object]
$windowStyle = if ($ShowWindow) { 'Normal' } else { 'Hidden' }

<#
    启动方式：直接启动 exe，三个标准流全部重定向：
      * stdout/stderr 进 logs\*.log；
      * stdin 接到 \\.\NUL（Windows 空设备）—— 这一步很关键：
        否则子进程会继承调用方的控制台/stdin 句柄，用管道调用本脚本时会被"吊住"；
      * -WindowStyle Hidden：不弹黑窗口。
#>
function Start-One {
    param([string]$ExePath, [string]$WorkingDirectory, [string]$LogPath)
    # stdin 必须重定向到一个"真实存在的空文件"：
    #   * Start-Process 不接受 \\.\NUL 这类设备路径（会报 FileNotFound）；
    #   * 不重定向的话，子进程会继承调用方的控制台句柄，用管道调用本脚本时会被吊住。
    $stdinFile = Join-Path $logsDir 'stdin-empty.txt'
    if (!(Test-Path $stdinFile)) { [System.IO.File]::WriteAllText($stdinFile, '') }
    $proc = Start-Process -FilePath $ExePath -WorkingDirectory $WorkingDirectory -WindowStyle $windowStyle `
        -RedirectStandardOutput $LogPath -RedirectStandardError ($LogPath + '.err') `
        -RedirectStandardInput $stdinFile -PassThru
    return $proc.Id
}

if (!$NoHost) {
    if (!(Test-Path $hostExe)) { throw "找不到采集宿主：$hostExe" }
    $log = Join-Path $logsDir ('host-console-' + $stamp + '.log')
    $pidValue = Start-One -ExePath $hostExe -WorkingDirectory $RuntimeDir -LogPath $log
    $started.Add([pscustomobject]@{ name = 'host'; pid = $pidValue; exe = $hostExe; log = $log })
    Write-Host ("[采集宿主] 已启动 PID " + $pidValue + "　日志：" + $log) -ForegroundColor Green
}

if (!$NoPlatform) {
    if (!(Test-Path $platformExe)) { throw "找不到业务平台：$platformExe" }
    $log = Join-Path $logsDir ('platform-console-' + $stamp + '.log')
    $pidValue = Start-One -ExePath $platformExe -WorkingDirectory (Split-Path $platformExe) -LogPath $log
    $started.Add([pscustomobject]@{ name = 'platform'; pid = $pidValue; exe = $platformExe; log = $log })
    Write-Host ("[业务平台] 已启动 PID " + $pidValue + "　日志：" + $log) -ForegroundColor Green
}

$stateFile = Join-Path $logsDir 'pids.json'
$state = [pscustomobject]@{
    startedAt = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    runtimeDir = $RuntimeDir
    processes = $started
}
[System.IO.File]::WriteAllText($stateFile, ($state | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))

Start-Sleep -Seconds 2
$platformOk = $false
if (!$NoPlatform) {
    for ($i = 0; $i -lt 10; $i++) {
        try { $null = Invoke-RestMethod -Uri 'http://127.0.0.1:8090/api/health' -TimeoutSec 2; $platformOk = $true; break } catch { Start-Sleep -Seconds 1 }
    }
}

Write-Host ""
Write-Host ("两个进程已隐藏启动（不会弹黑窗口）。平台健康检查：" + $(if ($NoPlatform) { '（未启动平台）' } elseif ($platformOk) { '通过，浏览器打开 http://127.0.0.1:8090' } else { '还没就绪，稍等几秒或看 logs\platform-console-*.log' })) -ForegroundColor Cyan
Write-Host "停止：tools\stop-all.ps1　　进程状态记录：logs\pids.json" -ForegroundColor DarkGray
