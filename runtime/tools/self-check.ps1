<#
    DWS 现场一键自检。

    把"装机/交付前该看的东西"串成一条命令：环境完整性 → 采集侧能不能触发 → 事件有没有产出
    → 平台侧健康 → 磁盘与下游 → 相机在线情况，最后给一张"通过/不通过 + 该做什么"的清单。

    设计原则：
      * 能验就验，不能验就明确说"跳过"（比如没有真机时不假装验过加密狗）；
      * 只读为主：唯一的"写"是让采集宿主软触发一次 / 补一次码（各产生一条测试数据）；
      * 退出码：0 = 没有失败项（可能有跳过项）；1 = 有失败项。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\self-check.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\self-check.ps1 -RuntimeDir D:\dws\runtime -Port 8090
        powershell -ExecutionPolicy Bypass -File .\tools\self-check.ps1 -SkipTrigger     # 不动采集侧
#>
param(
    [string]$RuntimeDir = '',
    [int]$Port = 8090,
    [string]$BaseUrl = '',
    [switch]$SkipTrigger
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($RuntimeDir)) {
    # 兼容两种布局：
    #   开发仓库：<仓库>\tools\self-check.ps1        → <仓库>\runtime
    #   交付包　：<runtime>\tools\self-check.ps1     → <runtime>（就在上一层，别再拼一层 runtime）
    $parentDir = Split-Path -Parent $scriptDir
    foreach ($candidate in @((Join-Path $parentDir 'runtime'), $parentDir)) {
        if (Test-Path (Join-Path $candidate 'DwsEdge.Host.exe')) { $RuntimeDir = $candidate; break }
    }
    if ([string]::IsNullOrEmpty($RuntimeDir)) { $RuntimeDir = Join-Path $parentDir 'runtime' }
}
if (![System.IO.Path]::IsPathRooted($RuntimeDir)) { $RuntimeDir = [System.IO.Path]::GetFullPath($RuntimeDir) }
if ([string]::IsNullOrEmpty($BaseUrl)) { $BaseUrl = 'http://127.0.0.1:' + $Port }

$results = New-Object System.Collections.Generic.List[object]
$todo = New-Object System.Collections.Generic.List[string]

function Add-Result {
    param([string]$Name, [string]$Result, [string]$Detail, [string]$Action = '')
    $results.Add([pscustomobject]@{ 检查项 = $Name; 结果 = $Result; 说明 = $Detail }) | Out-Null
    if ($Result -eq 'FAIL' -and $Action) { $todo.Add($Action) | Out-Null }
}

function Invoke-HostCommand {
    param([string[]]$Arguments, [int]$TimeoutMs = 60000)
    $exe = Join-Path $RuntimeDir 'DwsEdge.Host.exe'
    if (!(Test-Path $exe)) { return @{ exitCode = -1; output = '找不到 DwsEdge.Host.exe' } }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.WorkingDirectory = $RuntimeDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    # 宿主把自己的控制台输出设成了 UTF-8；这里也按 UTF-8 读，避免中文变乱码
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $psi.CreateNoWindow = $true
    $psi.Arguments = (($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -join ' ')

    $process = [System.Diagnostics.Process]::Start($psi)
    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit($TimeoutMs)) {
        try { $process.Kill() } catch { }
        return @{ exitCode = -999; output = '命令超时（宿主没有按命令模式退出）' }
    }
    return @{ exitCode = $process.ExitCode; output = ($outTask.Result + $errTask.Result) }
}

<#
    从宿主输出里挑"最有用的那一行"给现场看：
      优先 SDK 返回码提示（2200/3000/3001/1000/1001）、其次 [cmd] 行、最后退回到最后一行非空内容。
    只取一行并截断，避免把一整段输出塞进表格（既难读，终端编码不同还会显示成乱码）。
#>
function Get-KeyLine {
    param([string]$Text, [int]$MaxLength = 120)
    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }
    $lines = @($Text -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { ($_ -replace '\s+', ' ').Trim() })
    $picked = ''
    foreach ($pattern in @('未检测到加密狗', '相机数与配置不符', '相机被占用', '找不到配置', '配置解析失败', '\[cmd\]')) {
        $hit = $lines | Where-Object { $_ -match $pattern } | Select-Object -First 1
        if ($hit) { $picked = $hit; break }
    }
    if (!$picked -and $lines.Count -gt 0) { $picked = $lines[$lines.Count - 1] }
    if ($picked.Length -gt $MaxLength) { $picked = $picked.Substring(0, $MaxLength) + '…' }
    return $picked
}

function Get-Api {
    param([string]$Path)
    try {
        $response = Invoke-WebRequest -Uri ($BaseUrl + $Path) -UseBasicParsing -TimeoutSec 10
        $parsed = $null
        try { if ($response.Content) { $parsed = $response.Content | ConvertFrom-Json } } catch { }
        return @{ ok = $true; status = [int]$response.StatusCode; body = $parsed }
    }
    catch {
        return @{ ok = $false; status = 0; body = $null }
    }
}

function Get-ParcelEventCount {
    $today = (Get-Date).ToString('yyyyMMdd')
    $file = Join-Path $RuntimeDir ('spool\events-' + $today + '.jsonl')
    if (!(Test-Path $file)) { return 0 }
    $count = 0
    $stream = New-Object System.IO.FileStream($file, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $reader = New-Object System.IO.StreamReader($stream)
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            if ($line -match '"type":"parcel"') { $count++ }
        }
    }
    finally { $reader.Close(); $stream.Close() }
    return $count
}

Write-Host "============================================================"
Write-Host " DWS 一键自检"
Write-Host " 运行时目录：$RuntimeDir"
Write-Host " 平台地址　：$BaseUrl"
Write-Host "============================================================"

# ---------------------------------------------------------------- 1) 环境完整性
$required = @(
    @{ path = 'config\gateway.ini'; name = '采集宿主配置'; action = '缺少 gateway.ini：确认 runtime 目录完整' },
    @{ path = 'Cfg\LogisticsBase.cfg'; name = 'SDK 配置文件'; action = '缺少 Cfg\LogisticsBase.cfg：从大华 SDK 拷一份或跑 make-camera-cfg.ps1' },
    @{ path = 'DwsEdge.Host.exe'; name = '采集宿主'; action = '缺少 DwsEdge.Host.exe：先跑 build.ps1' },
    @{ path = 'platform\DwsEdge.Platform.exe'; name = '业务平台'; action = '缺少平台 exe：先跑 build.ps1' }
)
$missing = @()
foreach ($item in $required) {
    if (!(Test-Path (Join-Path $RuntimeDir $item.path))) { $missing += $item }
}
if ($missing.Count -eq 0) {
    Add-Result '运行时目录完整性' 'PASS' '配置 / 宿主 / 平台文件都在'
} else {
    foreach ($item in $missing) { Add-Result ('缺失：' + $item.name) 'FAIL' $item.path $item.action }
}

$providers = @()
if (Test-Path (Join-Path $RuntimeDir 'providers')) {
    $providers = @(Get-ChildItem (Join-Path $RuntimeDir 'providers') -Filter 'DwsEdge.Providers.*.dll')
}
if ($providers.Count -gt 0) {
    Add-Result 'provider 插件' 'PASS' ('找到 ' + $providers.Count + ' 个插件：' + (($providers | ForEach-Object { $_.Name }) -join ', '))
} else {
    Add-Result 'provider 插件' 'FAIL' 'providers 目录下没有插件 dll' '先跑 build.ps1'
}

# ---------------------------------------------------------------- 2) 采集侧：provider / 触发模式
$status = Invoke-HostCommand @('--command-status') 30000
$isSimulator = $false
if ($status.exitCode -eq 0) {
    # 只取"值"，不要把宿主整行输出搬进表格（终端编码不同时会显示成乱码）
    $providerValue = [regex]::Match($status.output, 'provider=([^\s]+)').Groups[1].Value
    $modeValue = [regex]::Match($status.output, '触发模式=([^\r\n]+)').Groups[1].Value.Trim()
    if ([string]::IsNullOrEmpty($providerValue)) { $providerValue = '未知' }
    if ([string]::IsNullOrEmpty($modeValue)) { $modeValue = '未知' }
    Add-Result '采集宿主能启动并自报状态' 'PASS' ('provider=' + $providerValue + '　触发模式：' + $modeValue)
    $isSimulator = $status.output -match 'provider=simulator'
} else {
    Add-Result '采集宿主能启动并自报状态' 'FAIL' ('退出码 ' + $status.exitCode + '：' + (Get-KeyLine -Text $status.output)) `
        '采集宿主起不来：先看 logs\host-*.log（加密狗 2200 / 相机数 3000 / 被占用 3001）'
}

if ($isSimulator) {
    Add-Result '加密狗与相机' 'SKIP' '当前 provider=simulator（模拟器不需要加密狗和相机）；接真机时把 gateway.ini 的 provider 改回 dahua-dws 再跑一次'
} elseif ($status.exitCode -eq 0) {
    # 真机模式：真正启动一次 SDK 并回读校验（这就是 A8 的校验路径），比只看状态可靠
    Write-Host '   （真机模式：正在启动 SDK 做一次回读校验，最长 3 分钟…）' -ForegroundColor DarkGray
    $verify = Invoke-HostCommand @('--verify-config') 180000
    if ($verify.exitCode -eq 0) {
        Add-Result '加密狗与相机（SDK 启动+配置回读校验）' 'PASS' '退出码 0：SDK 启动成功，配置参数与 cfg 一致'
    } elseif ($verify.exitCode -eq 2) {
        Add-Result '加密狗与相机（SDK 启动+配置回读校验）' 'FAIL' ('退出码 2：' + (Get-KeyLine -Text $verify.output)) `
            '按提示查：2200 加密狗、3000 相机数与清单不符、3001 相机被占用；确认本机与相机同网段'
    } else {
        Add-Result '加密狗与相机（SDK 启动+配置回读校验）' 'FAIL' ('退出码 ' + $verify.exitCode) '看 logs\host-*.log 的 [verify] 输出'
    }
}

# ---------------------------------------------------------------- 3) 采集侧：软触发 + 补码
if ($SkipTrigger) {
    Add-Result '软触发' 'SKIP' '按参数要求跳过（-SkipTrigger）'
} elseif ($status.exitCode -ne 0) {
    Add-Result '软触发' 'SKIP' '采集宿主起不来，跳过触发检查'
} else {
    $before = Get-ParcelEventCount
    $trigger = Invoke-HostCommand @('--soft-trigger') 60000
    # 宿主压根起不来（退出码 2 = 启动失败 / -999 = 超时）时，软触发与补码都没法验，
    # 真正要修的只有"宿主为什么起不来"这一条 —— 后面两项标成跳过，别让根因被噪音埋掉
    $hostStartFailed = ($trigger.exitCode -eq 2 -or $trigger.exitCode -eq -999)
    if ($trigger.exitCode -eq 0) {
        Start-Sleep -Seconds 2
        $after = Get-ParcelEventCount
        if ($after -gt $before) {
            Add-Result '软触发' 'PASS' ('退出码 0，且产出包裹事件（' + $before + ' → ' + $after + '）')
        } else {
            Add-Result '软触发' 'FAIL' '命令返回 0，但 spool 里没有新增包裹事件' '触发没出码：看 logs\host-*.log 里 SDK 的返回；确认对焦/曝光与光电状态'
        }
    } elseif ($trigger.exitCode -eq 5) {
        Add-Result '软触发' 'FAIL' '退出码 5：当前不是软触发模式' '用 tools\set-trigger-mode.ps1 -Mode soft 改触发模式并重启采集宿主'
    } elseif ($hostStartFailed) {
        Add-Result '软触发' 'SKIP' '采集宿主启动失败，无法验证触发（先解决上面的"加密狗与相机"）'
    } else {
        Add-Result '软触发' 'FAIL' ('退出码 ' + $trigger.exitCode + '：' + (Get-KeyLine -Text $trigger.output)) '看 logs\host-*.log 里 [cmd] 那几行'
    }

    $code = 'SELFCHECK' + (Get-Date).ToString('HHmmss')
    if ($hostStartFailed) {
        Add-Result '人工补码' 'SKIP' '采集宿主启动失败，无法验证补码（同"软触发"）'
    } else {
        $recode = Invoke-HostCommand @('--recode', '--code', $code) 60000
        if ($recode.exitCode -eq 0) {
            Add-Result '人工补码' 'PASS' ('退出码 0，补码 ' + $code)
        } else {
            Add-Result '人工补码' 'FAIL' ('退出码 ' + $recode.exitCode + '：' + (Get-KeyLine -Text $recode.output)) `
                '看 logs\host-*.log 里 [cmd] 补码那行；真机上补码要 SDK 接受该条码'
        }
    }
}

# ---------------------------------------------------------------- 4) 平台侧
$health = Get-Api '/api/health'
if (!$health.ok) {
    Add-Result '平台健康检查' 'SKIP' ('连不上 ' + $BaseUrl + '：平台没启动（.\run.ps1 -Platform）或改了端口')
} else {
    Add-Result '平台健康检查' 'PASS' 'HTTP 200'

    $stats = (Get-Api '/api/stats').body
    if ($stats) {
        $statsResult = if ($stats.parseErrors -eq 0) { 'PASS' } else { 'FAIL' }
        Add-Result '平台统计' $statsResult `
            ('包裹 ' + $stats.parcels + ' / 无码 ' + $stats.noread + ' / 解析失败 ' + $stats.parseErrors + ' / 重复丢弃 ' + $stats.duplicateEvents) `
            '有解析失败：spool 事件格式不对，用诊断页或 tools\check-traceids.ps1 看具体行'

        $storage = (Get-Api '/api/config/storage').body
        $limit = 0
        if ($storage -and $storage.options) { $limit = [int]$storage.options.maxDiskPercent }
        if ($limit -gt 0) {
            if ($stats.diskUsedPercent -ge $limit) {
                Add-Result '磁盘水位' 'FAIL' ('已用 ' + $stats.diskUsedPercent + '% ≥ 存图策略上限 ' + $limit + '%（会开始清最旧的图）') `
                    '清理磁盘或调大存图上限：相机页 → 存图策略'
            } else {
                Add-Result '磁盘水位' 'PASS' ('已用 ' + $stats.diskUsedPercent + '%，上限 ' + $limit + '%')
            }
        } else {
            Add-Result '磁盘水位' 'SKIP' '存图策略里没启用磁盘水位保护（maxDiskPercent=0）'
        }
    }

    $cameras = (Get-Api '/api/cameras').body
    if ($cameras) {
        $list = @($cameras)
        $online = @($list | Where-Object { $_.online -eq $true }).Count
        $notFound = @($list | Where-Object { $_.discovered -eq $false }).Count
        if ($list.Count -eq 0) {
            Add-Result '相机在线情况' 'SKIP' '平台还没收到相机状态（采集宿主启动后会上报快照）'
        } elseif ($online -eq $list.Count) {
            Add-Result '相机在线情况' 'PASS' ($online.ToString() + ' / ' + $list.Count + ' 台在线')
        } else {
            Add-Result '相机在线情况' 'FAIL' ($online.ToString() + ' / ' + $list.Count + ' 台在线（未发现 ' + $notFound + ' 台）') `
                '查离线相机：上电、网线、网段，或被 MVViewer 之类占用；设备信息页有每台掉线记录'
        }
    }

    $downstream = (Get-Api '/api/downstream').body
    if ($downstream -and $downstream.config) {
        $cfg = $downstream.config
        if (!$cfg.enabled) {
            Add-Result '下游输出' 'SKIP' '没启用下游输出（输出对接页 → 下游输出）'
        } elseif ($cfg.protocol -eq 'tcp-server') {
            $listening = $downstream.stats.listening
            if ($listening) {
                Add-Result '下游输出（TCP 服务端）' 'PASS' ('监听 ' + $downstream.stats.listenTarget + '，已接入客户端 ' + $downstream.stats.clientCount)
            } else {
                Add-Result '下游输出（TCP 服务端）' 'FAIL' '平台没在监听' '看端口是否被占用（输出对接页 → 下游输出 → 测试连接）'
            }
        } else {
            if ($downstream.stats.connected) {
                Add-Result '下游输出（连接下游）' 'PASS' ('目标 ' + $downstream.stats.target + '，待发 ' + $downstream.stats.queueDepth)
            } else {
                Add-Result '下游输出（连接下游）' 'FAIL' ('目标 ' + $downstream.stats.target + ' 连不上，待发 ' + $downstream.stats.queueDepth) `
                    '确认下游地址/端口与网络可达（输出对接页 → 测试连接）；断线期间包裹会排队不丢'
            }
        }
    }
}

# ---------------------------------------------------------------- 汇总
Write-Host ''
$results | Format-Table -AutoSize

$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
$skipped = @($results | Where-Object { $_.结果 -eq 'SKIP' })
$passed = @($results | Where-Object { $_.结果 -eq 'PASS' })
Write-Host ("通过 " + $passed.Count + " 项，失败 " + $failed.Count + " 项，跳过 " + $skipped.Count + " 项") -ForegroundColor Cyan

if ($failed.Count -eq 0) {
    Write-Host '[自检结论] 没有发现失败项' -ForegroundColor Green
    if ($skipped.Count -gt 0) {
        Write-Host '跳过项（通常是没接真机 / 没启动平台）不影响结论，接上真机后再跑一次：' -ForegroundColor Yellow
        foreach ($item in $skipped) { Write-Host ('  - ' + $item.检查项 + '：' + $item.说明) }
    }
    exit 0
}

Write-Host '[自检结论] 有失败项，按下面顺序处理：' -ForegroundColor Red
$i = 0
foreach ($item in $failed) {
    Write-Host ('  ' + ($i + 1) + '. ' + $item.检查项 + '：' + $item.说明) -ForegroundColor Red
    if ($i -lt $todo.Count) { Write-Host ('     → ' + $todo[$i]) -ForegroundColor Yellow }
    $i++
}
exit 1
