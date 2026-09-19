<#
    A4：给现场用的"命令通道"包装脚本。

    直接调 DwsEdge.Host.exe 不方便记忆退出码，这里把三条命令包成开关，并把退出码翻译成人话：

        .\tools\host-command.ps1 -Status                  看当前 provider 与触发模式
        .\tools\host-command.ps1 -SoftTrigger             软触发一次（要求 triggerMode=2）
        .\tools\host-command.ps1 -SoftTrigger -Force      跳过触发模式校验（排查用）
        .\tools\host-command.ps1 -Recode -Code SF123      人工补码

    两条路，脚本自己挑：
      1) 【宿主正在跑】走常驻命令通道（命名管道）—— 不用停宿主、也不跟它抢相机，推荐；
      2) 宿主没在跑时才退回老办法：新起一个 DwsEdge.Host.exe 进程执行命令再退出。
         老办法会自己开一次 SDK，所以**必须宿主没在跑**，否则报 3001（相机被占用）。

    -PreferProcess 可以强制走老办法（排查"通道"本身的问题时用）。

    退出码与宿主一致：0 成功 / 1 参数错误 / 2 启动失败 / 3 未处理异常 /
                       4 命令执行失败 / 5 触发模式不允许 / 6 provider 不支持
#>
param(
    [switch]$Status,
    [switch]$SoftTrigger,
    [switch]$Recode,
    [string]$Code = '',
    [long]$TimeMs = 0,
    [switch]$Force,
    [switch]$PreferProcess,
    [string]$RuntimeDir = ''
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
# 兼容两种布局：开发仓库是 <仓库>\tools\ → <仓库>\runtime；交付包是 <runtime>\tools\ → <runtime>
if ([string]::IsNullOrEmpty($RuntimeDir)) {
    $parentDir = Split-Path -Parent $scriptDir
    foreach ($candidate in @((Join-Path $parentDir 'runtime'), $parentDir)) {
        if (Test-Path (Join-Path $candidate 'DwsEdge.Host.exe')) { $RuntimeDir = $candidate; break }
    }
    if ([string]::IsNullOrEmpty($RuntimeDir)) { $RuntimeDir = Join-Path $parentDir 'runtime' }
}

$exe = Join-Path $RuntimeDir 'DwsEdge.Host.exe'
if (!(Test-Path $exe)) { throw "找不到采集宿主：$exe" }

$args = @()
if ($Status) { $args += '--command-status' }
if ($SoftTrigger) { $args += '--soft-trigger' }
if ($Recode) { $args += '--recode' }
if (![string]::IsNullOrEmpty($Code)) { $args += @('--code', $Code) }
if ($TimeMs -gt 0) { $args += @('--time-ms', $TimeMs.ToString()) }
if ($Force) { $args += '--force' }

if ($args.Count -eq 0) {
    Write-Host "用 法：host-command.ps1 -Status | -SoftTrigger [-Force] | -Recode -Code <条码>" -ForegroundColor Yellow
    exit 1
}
if ($Recode -and [string]::IsNullOrEmpty($Code)) {
    Write-Host "补码要带条码：-Recode -Code SF1234567890" -ForegroundColor Yellow
    exit 1
}

# ---------------------------------------------------------------- 常驻命令通道
# 算法必须与 DwsEdge.Core\Ipc\HostCommandChannel.PipeNameFor 完全一致：
#   dws-edge-host-<runtime 路径规范化后 SHA1 的前 8 位十六进制>
function Get-HostPipeName {
    param([string]$Dir)
    $normalized = [System.IO.Path]::GetFullPath($Dir).Replace('/', '\').TrimEnd('\').ToLowerInvariant()
    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    try {
        $hash = $sha1.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized))
    }
    finally {
        $sha1.Dispose()
    }
    $hex = -join ($hash | ForEach-Object { $_.ToString('x2') })
    return 'dws-edge-host-' + $hex.Substring(0, 8)
}

# 返回 $null 表示"通道不可用"（宿主没在跑），返回对象表示拿到了宿主的应答
function Invoke-HostChannel {
    param([string]$Dir, [string]$Request, [int]$TimeoutMs = 20000)

    $pipe = $null
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', (Get-HostPipeName -Dir $Dir), [System.IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect($TimeoutMs)

        $utf8 = New-Object System.Text.UTF8Encoding($false)
        $payload = $utf8.GetBytes($Request.Trim() + "`n")
        $pipe.Write($payload, 0, $payload.Length)
        $pipe.Flush()

        $reader = New-Object System.IO.StreamReader($pipe, $utf8, $false, 4096, $true)
        $first = $reader.ReadLine()
        $rest = $reader.ReadToEnd()

        $exit = 4
        if (![string]::IsNullOrWhiteSpace($first)) { [void][int]::TryParse($first.Trim(), [ref]$exit) }
        return [pscustomobject]@{ ExitCode = $exit; Message = ($rest -as [string]) }
    }
    catch {
        return $null
    }
    finally {
        if ($pipe) { try { $pipe.Dispose() } catch { } }
    }
}

$requestLine = ''
if ($Status) { $requestLine = 'status' }
elseif ($SoftTrigger) { $requestLine = if ($Force) { 'soft-trigger --force' } else { 'soft-trigger' } }
elseif ($Recode) {
    $requestLine = 'recode ' + $Code.Trim()
    if ($TimeMs -gt 0) { $requestLine += ' ' + $TimeMs.ToString() }
}

$usedChannel = $false
$code = -1

if (!$PreferProcess -and ![string]::IsNullOrEmpty($requestLine)) {
    $channel = Invoke-HostChannel -Dir $RuntimeDir -Request $requestLine
    if ($channel) {
        $usedChannel = $true
        $code = $channel.ExitCode
        Write-Host ("[命令通道] 宿主在跑 → 走常驻通道（" + (Get-HostPipeName -Dir $RuntimeDir) + "）") -ForegroundColor DarkGray
        Write-Host ("[命令] " + $requestLine) -ForegroundColor DarkGray
        if (![string]::IsNullOrWhiteSpace($channel.Message)) { Write-Host $channel.Message }
    }
    else {
        Write-Host "[命令通道] 连不上（采集宿主没在跑）→ 退回【新起一个宿主进程】执行" -ForegroundColor DarkGray
    }
}

if (!$usedChannel) {
    Push-Location $RuntimeDir
    try {
        & $exe @args
        $code = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}

$conclusion = switch ($code) {
    0 { '成功' }
    1 { '参数错误（看上面的提示）' }
    2 { '采集宿主启动失败（加密狗 / 相机 / SDK 配置）' }
    3 { '未处理异常（看 logs\host-*.log）' }
    4 { '命令执行失败（provider 返回非 0，看日志）' }
    5 { '触发模式不允许：当前不是软触发模式（用 tools\set-trigger-mode.ps1 -Mode soft 改好，或加 -Force）' }
    6 { '当前 provider 不支持这条命令' }
    default { '未知退出码' }
}

$color = if ($code -eq 0) { 'Green' } elseif ($code -eq 5) { 'Yellow' } else { 'Red' }
Write-Host ""
Write-Host ("[命令结论] 退出码 " + $code + " —— " + $conclusion) -ForegroundColor $color
exit $code
