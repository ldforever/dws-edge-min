<#
    测试用 TCP 服务端（只给 B4 回归脚本用，别在现场跑）。

    它监听一个端口，把收到的每一行（换行分隔）追加到 -File 指定的文件里，
    这样回归脚本能核对"下游到底收到了什么、收了几条、有没有重复"。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\tcp-test-server.ps1 -Port 9100 -File .\work\recv.log
#>
param(
    [Parameter(Mandatory = $true)][int]$Port,
    [Parameter(Mandatory = $true)][string]$File
)

$ErrorActionPreference = 'Stop'

$dir = Split-Path -Parent $File
if (![string]::IsNullOrEmpty($dir) -and !(Test-Path $dir)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $Port)
$listener.Server.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::Socket,
    [System.Net.Sockets.SocketOptionName]::ReuseAddress, $true)
$listener.Start()
Write-Host ("测试 TCP 服务端已启动：127.0.0.1:" + $Port + " → " + $File)

while ($true) {
    $client = $listener.AcceptTcpClient()
    $stream = $client.GetStream()
    $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
    try {
        while ($true) {
            $line = $reader.ReadLine()
            if ($null -eq $line) {
                break      # 客户端断开
            }
            [System.IO.File]::AppendAllText($File, $line + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
        }
    }
    catch {
        # 客户端异常断开：忽略，继续 accept 下一个
    }
    finally {
        try { $client.Close() } catch { }
    }
}
