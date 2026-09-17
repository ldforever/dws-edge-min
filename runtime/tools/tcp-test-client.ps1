<#
    测试用 TCP 客户端（只给 B5 回归脚本用）。

    连到指定的 TCP 服务端（就是被测的平台），把收到的每一行追加到 -File，
    这样回归脚本能核对"哪个客户端收到了什么、收了几条、有没有重复"。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\tcp-test-client.ps1 -Host 127.0.0.1 -Port 9200 -File .\work\client-a.log
#>
param(
    # 注意：不能叫 -Host，$Host 是 PowerShell 的只读自动变量
    [string]$ServerHost = '127.0.0.1',
    [Parameter(Mandatory = $true)][int]$Port,
    [Parameter(Mandatory = $true)][string]$File
)

$ErrorActionPreference = 'Stop'

$dir = Split-Path -Parent $File
if (![string]::IsNullOrEmpty($dir) -and !(Test-Path $dir)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

$client = New-Object System.Net.Sockets.TcpClient
$client.NoDelay = $true
$client.Connect($ServerHost, $Port)
$stream = $client.GetStream()
$reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
Write-Host ("测试 TCP 客户端已连接 " + $ServerHost + ":" + $Port + " → " + $File)

try {
    while ($true) {
        $line = $reader.ReadLine()
        if ($null -eq $line) {
            break      # 服务端断开
        }
        [System.IO.File]::AppendAllText($File, $line + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
    }
}
catch {
    # 服务端异常断开：退出即可，由回归脚本决定要不要重连
}
finally {
    try { $client.Close() } catch { }
}
