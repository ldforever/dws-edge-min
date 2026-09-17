<#
    测试用 HTTP 接收端（只给 B6 回归脚本用）。

    为什么不用 System.Net.HttpListener：在某些受限环境下它的构造函数会直接报
    "该平台上不支持此操作"。所以这里用 TcpListener + 自己解析最小的 HTTP/1.1 请求，
    只认 POST、Content-Length 与 Idempotency-Key，够测试用了。

    每收到一个 POST，就把「结果|幂等键|请求体」追加到 -File：
        OK|B6-1|B6-1|SF000000000001|500
        FAIL(1)|B6-2|B6-2|SF000000000002|501

    -FailKey / -FailTimes：对指定幂等键的前 N 次请求返回 500（故障注入，
    用来验证"下游返回错误时自动重试并最终成功"）。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\http-test-server.ps1 -Port 9300 -File .\work\http-recv.log
#>
param(
    [Parameter(Mandatory = $true)][int]$Port,
    [Parameter(Mandatory = $true)][string]$File,
    [string]$FailKey = '',
    [int]$FailTimes = 0
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
Write-Host ("测试 HTTP 接收端已启动：http://127.0.0.1:" + $Port + "/ → " + $File)

$utf8 = New-Object System.Text.UTF8Encoding($false)
$failSeen = 0

while ($true) {
    $client = $listener.AcceptTcpClient()
    $stream = $client.GetStream()
    try {
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)

        $requestLine = $reader.ReadLine()
        if ([string]::IsNullOrEmpty($requestLine)) { continue }

        $contentLength = 0
        $key = '(none)'
        while ($true) {
            $line = $reader.ReadLine()
            if ([string]::IsNullOrEmpty($line)) { break }
            if ($line -match '^(?i)Content-Length:\s*(\d+)') { $contentLength = [int]$Matches[1] }
            if ($line -match '^(?i)Idempotency-Key:\s*(.+)$') { $key = $Matches[1].Trim() }
        }

        $body = ''
        if ($contentLength -gt 0) {
            $buffer = New-Object char[] $contentLength
            $read = 0
            while ($read -lt $contentLength) {
                $n = $reader.Read($buffer, $read, $contentLength - $read)
                if ($n -le 0) { break }
                $read += $n
            }
            $body = -join $buffer[0..([Math]::Max(0, $read - 1))]
        }

        $status = 'OK'
        $code = 200
        if ($FailTimes -gt 0 -and $FailKey.Length -gt 0 -and $key -eq $FailKey -and $failSeen -lt $FailTimes) {
            $failSeen++
            $status = 'FAIL(' + $failSeen + ')'
            $code = 500
        }

        [System.IO.File]::AppendAllText($File, ($status + '|' + $key + '|' + $body + "`r`n"), $utf8)

        $reason = if ($code -eq 200) { 'OK' } else { 'Internal Server Error' }
        $payload = if ($code -eq 200) { 'ok' } else { 'injected failure' }
        $head = "HTTP/1.1 $code $reason`r`nContent-Type: text/plain`r`nContent-Length: " +
                $payload.Length + "`r`nConnection: close`r`n`r`n" + $payload
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($head)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()
    }
    catch {
        # 单个请求出错不影响继续接收
    }
    finally {
        try { $client.Close() } catch { }
    }
}
