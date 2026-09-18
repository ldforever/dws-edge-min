<#
    C5 回归测试：配置页（简版）—— 相机清单 / 输出对接参数 / 存图策略 / 条码规则的图形化配置。

    验收点（需求原文）：保存后生效且可回滚；非法参数有校验提示。

    这个脚本盯的是"这次新补的三块"（输出对接参数=B4/B5/B6、条码规则=B2 已有各自回归）：
      1) 存图策略：读 → 改 → 保存后立刻能在文件与接口里看到；注释与其他段不被冲掉；
      2) 非法参数：被拒（400）且文件保持原样（不能"改一半"）；
      3) 相机清单：表格编辑最终产出的还是同一份清单文本，走同一个 apply 接口（这里用 -SkipVerify 只写 cfg）；
      4) 备份与回滚：备份能列出来、能一键还原、还原前会把当前内容另存、非法文件名被拒。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-c5-config.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\test-c5-config.ps1 -KeepRunning
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = '',
    [int]$Port = 8100,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-c5-test' }
if (![System.IO.Path]::IsPathRooted($SourceRuntime)) { $SourceRuntime = [System.IO.Path]::GetFullPath($SourceRuntime) }
if (![System.IO.Path]::IsPathRooted($WorkDir)) { $WorkDir = [System.IO.Path]::GetFullPath($WorkDir) }

$platformExe = Join-Path $SourceRuntime 'platform\DwsEdge.Platform.exe'
if (!(Test-Path $platformExe)) { throw "找不到平台可执行文件：$platformExe（先跑一次 build.ps1）" }

$baseUrl = 'http://127.0.0.1:' + $Port
$results = New-Object System.Collections.Generic.List[object]
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Add-Check {
    param([string]$Name, $Expected, $Actual)
    $ok = ("$Expected" -eq "$Actual")
    $results.Add([pscustomobject]@{
        检查项 = $Name; 期望 = "$Expected"; 实际 = "$Actual"; 结果 = $(if ($ok) { 'PASS' } else { 'FAIL' })
    }) | Out-Null
}

function Stop-Platform {
    Get-Process -Name 'DwsEdge.Platform' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like ($WorkDir + '*') } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

function Start-Platform {
    $exe = Join-Path $WorkDir 'platform\DwsEdge.Platform.exe'
    $log = Join-Path $WorkDir 'logs\c5-test-platform.log'
    Start-Process -FilePath $exe -ArgumentList @('--urls', $baseUrl) `
        -WorkingDirectory (Join-Path $WorkDir 'platform') -WindowStyle Hidden `
        -RedirectStandardOutput $log -RedirectStandardError ($log + '.err') | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        try { $null = Invoke-RestMethod -Uri ($baseUrl + '/api/health') -TimeoutSec 3; return } catch { }
    }
    throw "平台在 60 秒内没有就绪：$baseUrl"
}

function Call {
    param([string]$Path, [string]$Method = 'GET', $Body = $null, [string]$ApiKey = '')
    $headers = @{}
    if ($ApiKey) { $headers['X-Api-Key'] = $ApiKey }
    $params = @{ Uri = $baseUrl + $Path; Method = $Method; UseBasicParsing = $true; TimeoutSec = 60 }
    if ($headers.Count -gt 0) { $params.Headers = $headers }
    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 6 -Compress)
        $params.ContentType = 'application/json; charset=utf-8'
    }
    try {
        $response = Invoke-WebRequest @params
        $parsed = $null
        try { if ($response.Content) { $parsed = $response.Content | ConvertFrom-Json } } catch { }
        return @{ status = [int]$response.StatusCode; body = $parsed; raw = $response.Content }
    }
    catch {
        $status = 0; $content = ''
        if ($_.Exception.Response) {
            $resp = $_.Exception.Response
            $status = [int]$resp.StatusCode
            try {
                if ($null -ne $resp.Content -and $resp.Content.ReadAsStringAsync) {
                    $content = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                }
            }
            catch { }
            if ([string]::IsNullOrEmpty($content)) {
                try { $content = (New-Object System.IO.StreamReader($resp.GetResponseStream())).ReadToEnd() } catch { }
            }
        }
        $parsed = $null
        try { if ($content) { $parsed = $content | ConvertFrom-Json } } catch { }
        return @{ status = $status; body = $parsed; raw = $content }
    }
}

Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
Stop-Platform
if (Test-Path $WorkDir) {
    Move-Item -LiteralPath $WorkDir -Destination ("$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force
}
robocopy $SourceRuntime $WorkDir /E /XD spool data images logs cache /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $WorkDir 'spool'), (Join-Path $WorkDir 'data'), (Join-Path $WorkDir 'images'), (Join-Path $WorkDir 'logs') | Out-Null

$gateway = Join-Path $WorkDir 'config\gateway.ini'
$sdkCfg = Join-Path $WorkDir 'Cfg\LogisticsBase.cfg'

try {
    Start-Platform
    . (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'b9-auth-helper.ps1')
    $null = Enable-TestAuth -WorkDir $WorkDir
    $authCfg = Get-Content -LiteralPath (Join-Path $WorkDir 'config\auth.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $key = $authCfg.serviceKey

    # ---------------------------------------------------------------- 1) 读存图策略
    Write-Host "`n=== 1) 读存图策略（默认值）===" -ForegroundColor Cyan
    $storage = Call -Path '/api/config/storage'
    Add-Check '读存图策略 200' 200 $storage.status
    Add-Check '默认图片保存 7 天' 7 $storage.body.options.retentionDays
    Add-Check '默认磁盘水位 85%' 85 $storage.body.options.maxDiskPercent
    Add-Check '默认保存原图' 'True' $storage.body.options.saveOriginal
    Add-Check '带当前 provider 段名' 'True' ([string]::IsNullOrEmpty($storage.body.providerSection) -eq $false)
    Add-Check '存图开关写到相机 provider 段（dahua-dws）' 'dahua-dws' $storage.body.storageSection
    Add-Check '带图片根目录' 'True' ($storage.body.imageRoot -like '*images*')
    Add-Check '带取值范围说明（界面提示用）' 'True' ([string]::IsNullOrEmpty($storage.body.limits.retentionDays) -eq $false)

    # ---------------------------------------------------------------- 2) 保存合法值 -> 立即生效
    Write-Host "`n=== 2) 保存合法值：文件与接口都要变 ===" -ForegroundColor Cyan
    $before = [System.IO.File]::ReadAllText($gateway)
    $options = $storage.body.options
    $options.retentionDays = 14
    $options.maxDiskPercent = 80
    $options.cleanupIntervalMinutes = 15
    $options.spoolRetentionDays = 3
    $options.savePerCamera = $true          # 顺带验证组合校验：开了它必须同时开 attachAll
    $options.attachAllCameraCodeInfo = $true
    $save = Call -Path '/api/config/storage' -Method POST -Body $options
    Add-Check '保存 200' 200 $save.status
    Add-Check '报告改了几项' 'True' ($save.body.changedCount -ge 4)
    Add-Check '自动备份已生成' 'True' ($save.body.backup -and (Test-Path $save.body.backup))
    Add-Check '提示需要重启采集宿主' 'True' $save.body.needRestartHost
    Add-Check '保存响应说明写到哪个段' 'dahua-dws' $save.body.storageSection

    $after = [System.IO.File]::ReadAllText($gateway)
    Add-Check '文件里 retentionDays=14' 'True' ($after -match 'retentionDays=14')
    Add-Check '文件里 maxDiskPercent=80' 'True' ($after -match 'maxDiskPercent=80')
    Add-Check '存图开关没被重复插入（只出现一次）' 1 ([regex]::Matches($after, '(?m)^saveOriginal=')).Count
    Add-Check '没把没人读的键插到 [simulator] 段' 'False' ($after -match '(?s)\[simulator\].*?saveOriginal=')
    Add-Check '注释没被冲掉' 'True' ($after -match '# ---- 存图保留策略')
    Add-Check '其他段没被冲掉（[test]）' 'True' (($after -match '\[test\]') -and ($after -match 'enableSoftTrigger'))
    Add-Check '文件没被改乱（行数与原来接近）' 'True' ([Math]::Abs((($after -split "`n").Count) - (($before -split "`n").Count)) -le 2)

    $again = Call -Path '/api/config/storage'
    Add-Check '重新读取已是 14' 14 $again.body.options.retentionDays
    Add-Check '重新读取已是 80' 80 $again.body.options.maxDiskPercent
    Add-Check '每台相机图已开' 'True' $again.body.options.savePerCamera

    # ---------------------------------------------------------------- 3) 非法参数：拒绝且文件不变
    Write-Host "`n=== 3) 非法参数：400 + 文件保持原样 ===" -ForegroundColor Cyan
    $snapshot = [System.IO.File]::ReadAllText($gateway)

    $bad = $again.body.options
    $bad.retentionDays = -1
    $r1 = Call -Path '/api/config/storage' -Method POST -Body $bad
    Add-Check '天数 -1 被拒' 400 $r1.status
    Add-Check '拒绝原因可读' 'True' ($r1.body.error -match '0-3650')

    $bad = $again.body.options
    $bad.maxDiskPercent = 101
    $r2 = Call -Path '/api/config/storage' -Method POST -Body $bad
    Add-Check '水位 101 被拒' 400 $r2.status

    $bad = $again.body.options
    $bad.cleanupIntervalMinutes = 0
    $r3 = Call -Path '/api/config/storage' -Method POST -Body $bad
    Add-Check '清理间隔 0 被拒' 400 $r3.status

    $bad = $again.body.options
    $bad.imageDir = '..\..\evil'
    $r4 = Call -Path '/api/config/storage' -Method POST -Body $bad
    Add-Check '目录带 .. 被拒' 400 $r4.status
    Add-Check '越权提示清楚' 'True' ($r4.body.error -match '不能包含')

    $bad = $again.body.options
    $bad.savePerCamera = $true
    $bad.attachAllCameraCodeInfo = $false
    $r5 = Call -Path '/api/config/storage' -Method POST -Body $bad
    Add-Check '组合校验：每台相机图需配套回传码信息' 400 $r5.status

    Add-Check '被拒后文件一个字没变' 'True' ($snapshot -eq [System.IO.File]::ReadAllText($gateway))

    # ---------------------------------------------------------------- 4) 相机清单（图形化最终产出的还是同一份清单文本）
    Write-Host "`n=== 4) 相机清单：校验 + 写入 cfg ===" -ForegroundColor Cyan
    $badApply = Call -Path '/api/config/apply' -Method POST -ApiKey $key -Body @{
        cameras = @('foo=172.20.10.11'); skipVerify = $true; stopHost = $false; restartHost = $false
    }
    Add-Check '非法清单被拒' 400 $badApply.status
    Add-Check '提示到具体第几行' 'True' ($badApply.body.error -match '第 1 行')

    $cfgBefore = [System.IO.File]::ReadAllText($sdkCfg)
    $goodApply = Call -Path '/api/config/apply' -Method POST -ApiKey $key -Body @{
        cameras = @('ip=172.20.10.11,pos=top', 'ip=172.20.10.12,pos=bottom', 'ip=172.20.10.13,pos=left')
        skipVerify = $true; stopHost = $false; restartHost = $false
    }
    Add-Check '合法清单应用成功' 'True' ($goodApply.body.ok)
    $cfgAfter = [System.IO.File]::ReadAllText($sdkCfg)
    Add-Check 'cfg 里写进了新相机' 'True' ($cfgAfter -match '172\.20\.10\.11')
    Add-Check 'cfg 里能看到 3 台' 'True' (([regex]::Matches($cfgAfter, '172\.20\.10\.1[123]')).Count -ge 3)
    Add-Check 'cfg 被改动过（原来是空的或不同）' 'True' ($cfgAfter -ne $cfgBefore)

    $cfgRead = Call -Path '/api/config'
    Add-Check '接口读回 3 台相机' 3 @($cfgRead.body.cameras).Count
    Add-Check '相机带方位（表格里的那一列）' 'top' @($cfgRead.body.cameras)[0].position
    Add-Check '相机带"行文本"（文本模式用）' 'True' (@($cfgRead.body.cameras)[0].line -match 'ip=172\.20\.10\.11')

    # ---------------------------------------------------------------- 5) 备份与回滚
    Write-Host "`n=== 5) 配置备份与回滚 ===" -ForegroundColor Cyan
    $backups = Call -Path '/api/config/backups'
    Add-Check '备份列表 200' 200 $backups.status
    $list = @($backups.body)
    Add-Check '备份列表非空' 'True' ($list.Count -ge 1)
    $gwBackup = @($list | Where-Object { $_.fileName -like 'gateway.ini.bak-*' })[0]
    Add-Check '能找到 gateway.ini 的备份' 'True' ($gwBackup -ne $null)
    Add-Check '备份带"生效方式"说明' 'True' ($gwBackup.effect -match '重启采集宿主')
    Add-Check '备份带时间与大小' 'True' (([string]::IsNullOrEmpty($gwBackup.modified) -eq $false) -and ([string]::IsNullOrEmpty($gwBackup.sizeText) -eq $false))
    $cfgBackup = @($list | Where-Object { $_.fileName -like 'LogisticsBase.cfg.bak-*' })[0]
    Add-Check '也能看到 Cfg 的备份（一键应用产生的）' 'True' ($cfgBackup -ne $null)

    $rb = Call -Path '/api/config/rollback' -Method POST -ApiKey $key -Body @{ file = $gwBackup.fileName }
    Add-Check '回滚 200' 200 $rb.status
    Add-Check '回滚目标是 gateway.ini' 'gateway.ini' $rb.body.target
    Add-Check '回滚前的内容被另存（不怕点错）' 'True' ($rb.body.backupOfCurrent -and (Test-Path $rb.body.backupOfCurrent))
    $restored = Call -Path '/api/config/storage'
    Add-Check '回滚后保存天数回到 7' 7 $restored.body.options.retentionDays
    Add-Check '回滚后水位回到 85' 85 $restored.body.options.maxDiskPercent

    $badName = Call -Path '/api/config/rollback' -Method POST -ApiKey $key -Body @{ file = '..\config\gateway.ini.bak-20260101-000000' }
    Add-Check '带路径的备份名被拒' 400 $badName.status
    $noMark = Call -Path '/api/config/rollback' -Method POST -ApiKey $key -Body @{ file = 'gateway.ini' }
    Add-Check '不是备份名的被拒' 400 $noMark.status

    # ---------------------------------------------------------------- 6) 前端产物里确实有这三块
    Write-Host "`n=== 6) 部署产物里带 C5 的三块界面 ===" -ForegroundColor Cyan
    $html = [System.IO.File]::ReadAllText((Join-Path $WorkDir 'platform\wwwroot\index.html'))
    Add-Check '有存图策略面板' 'True' ($html -match 'id="btnStorageSave"')
    Add-Check '有备份与回滚面板' 'True' (($html -match 'id="backupRows"') -and ($html -match 'id="btnBackupReload"'))
    Add-Check '有相机清单表格编辑' 'True' (($html -match 'id="camEditRows"') -and ($html -match 'id="btnCamApply"'))
    Add-Check '文本模式仍在（高级入口）' 'True' ($html -match 'id="cfgCameras"')

    $cfgJs = [System.IO.File]::ReadAllText((Join-Path $WorkDir 'platform\wwwroot\js\config.js'))
    $apiJs = [System.IO.File]::ReadAllText((Join-Path $WorkDir 'platform\wwwroot\js\api.js'))
    Add-Check 'api.js 有存图策略接口' 'True' ($apiJs -match 'api/config/storage')
    Add-Check 'api.js 有备份与回滚接口' 'True' (($apiJs -match 'api/config/backups') -and ($apiJs -match 'api/config/rollback'))
    Add-Check 'config.js 调存图策略与备份' 'True' (($cfgJs -match 'refreshStorage') -and ($cfgJs -match 'refreshBackups'))
    Add-Check 'config.js 有清单表格校验' 'True' ($cfgJs -match 'checkCameraEditor|cameraEditorRows')
}
finally {
    if (!$KeepRunning) { Stop-Platform }
}

Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('C5 回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    if ($KeepRunning) { Write-Host ('平台还在跑：' + $baseUrl + '　打开"配置"页看 C5 三块') -ForegroundColor Yellow }
    exit 0
}
Write-Host ('C5 回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
