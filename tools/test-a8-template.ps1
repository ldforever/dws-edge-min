<#
    A8-3 回归测试：配置模板（另存 / 列表 / 差异对比 / 套用 / 删除 / 导出）。

    验收点（需求 A8 原文）："按模板生成或改写 SDK 配置文件" —— 这里验"按模板生成"这一半：
      1) 把当前配置（相机清单 + 触发模式 + 存图策略）另存成模板；
      2) 改乱当前配置后，"对比"能逐项列出会改什么（相机多出/缺少/方位不同、触发模式、存图各字段）；
      3) "套用"能把它改回来：相机清单/触发模式走一键应用（写 cfg → 校验 → 失败回滚），存图策略写 gateway.ini；
      4) 模板名称校验、重复检测、越权名拒绝、删除、导出下载；
      5) 部署产物里确实带这套界面。

    用 simulator 跑（不需要相机和加密狗）；相机清单的写入用 apply-config.ps1 -SkipVerify。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-a8-template.ps1
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = '',
    [int]$Port = 8105
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-a8tpl-test' }
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
        Where-Object { $_.Path -like ($WorkDir + '*') } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

function Start-Platform {
    $log = Join-Path $WorkDir 'logs\a8tpl-platform.log'
    Start-Process -FilePath (Join-Path $WorkDir 'platform\DwsEdge.Platform.exe') -ArgumentList @('--urls', $baseUrl) `
        -WorkingDirectory (Join-Path $WorkDir 'platform') -WindowStyle Hidden `
        -RedirectStandardOutput $log -RedirectStandardError ($log + '.err') | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        try { $null = Invoke-RestMethod -Uri ($baseUrl + '/api/health') -TimeoutSec 3; return } catch { }
    }
    throw "平台在 60 秒内没有就绪：$baseUrl"
}

function Call {
    param([string]$Path, [string]$Method = 'GET', $Body = $null, [string]$ApiKey = '', [switch]$Anonymous)
    $headers = @{}
    if ($ApiKey) { $headers['X-Api-Key'] = $ApiKey }
    $params = @{ Uri = $baseUrl + $Path; Method = $Method; UseBasicParsing = $true; TimeoutSec = 180 }
    if ($headers.Count -gt 0) { $params.Headers = $headers }
    elseif ($Anonymous) { $params.Headers = @{} }
    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 8 -Compress)
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

function Set-TriggerMode {
    param([string]$Mode)
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptDir 'set-trigger-mode.ps1') `
        -Mode $Mode -RuntimeDir $WorkDir | Out-Null
}

function Set-Cameras {
    param([string[]]$Lines, [string]$Name)
    $file = Join-Path $WorkDir ('config\' + $Name)
    [System.IO.File]::WriteAllLines($file, $Lines, $utf8NoBom)
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptDir 'apply-config.ps1') `
        -RuntimeDir $WorkDir -CameraList $file -SkipVerify | Out-Null
}

Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
Stop-Platform
if (Test-Path $WorkDir) {
    Move-Item -LiteralPath $WorkDir -Destination ("$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force
}
robocopy $SourceRuntime $WorkDir /E /XD spool data images logs cache /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $WorkDir 'spool'), (Join-Path $WorkDir 'data'), `
    (Join-Path $WorkDir 'images'), (Join-Path $WorkDir 'logs') | Out-Null
[System.IO.File]::WriteAllText((Join-Path $WorkDir 'config\auth.json'),
    '{"enabled":true,"protectRead":false,"allowServiceKey":true,"serviceKey":"a8tpl","serviceKeyRole":"admin","maxFailures":5,"lockMinutes":15,"failureWindowMinutes":10,"sessionMinutes":480}',
    $utf8NoBom)

# 把 provider 改成 simulator（模板功能与厂商无关，用模拟器跑不需要设备）
$gateway = Join-Path $WorkDir 'config\gateway.ini'
$text = [System.IO.File]::ReadAllText($gateway)
$text = [regex]::Replace($text, '(?m)^provider=.*$', 'provider=simulator')
[System.IO.File]::WriteAllText($gateway, $text, $utf8NoBom)

# 先配一个"已知状态"：3 台相机 + 软触发 + 存图 7 天
Set-Cameras -Name 'cameras-3.txt' -Lines @('ip=172.20.10.11,pos=top', 'ip=172.20.10.12,pos=bottom', 'ip=172.20.10.13,pos=left')
Set-TriggerMode -Mode 'soft'

try {
    Start-Platform
    . (Join-Path $scriptDir 'b9-auth-helper.ps1')
    $null = Enable-TestAuth -WorkDir $WorkDir
    $key = (Get-Content -LiteralPath (Join-Path $WorkDir 'config\auth.json') -Raw -Encoding UTF8 | ConvertFrom-Json).serviceKey
    $cfgPath = Join-Path $WorkDir 'Cfg\LogisticsBase.cfg'

    # ---------------------------------------------------------------- 1) 另存模板
    Write-Host "`n=== 1) 把当前配置另存为模板 ===" -ForegroundColor Cyan
    $save = Call -Path '/api/config/templates' -Method POST -Body @{ name = 'T1'; note = '回归用-17台六面扫' }
    Add-Check '另存模板 200' 200 $save.status
    Add-Check '模板记下 3 台相机' 3 $save.body.cameraCount
    $tplFile = Join-Path $WorkDir 'config\templates\T1.json'
    Add-Check '模板文件已生成' 'True' (Test-Path $tplFile)
    $tplJson = Get-Content $tplFile -Raw -Encoding UTF8 | ConvertFrom-Json
    Add-Check '模板里带触发模式' '2' $tplJson.triggerMode
    Add-Check '模板里带相机清单' 3 @($tplJson.cameras).Count
    Add-Check '模板里带存图策略' 7 $tplJson.storage.retentionDays

    $list = Call -Path '/api/config/templates'
    Add-Check '模板列表里有 T1' 1 @($list.body.templates).Count
    Add-Check '列表显示相机数' 3 @($list.body.templates)[0].cameraCount
    Add-Check '列表显示触发模式名' '软触发' @($list.body.templates)[0].triggerName
    Add-Check '列表带模板目录' 'True' ($list.body.directory -like '*templates*')

    # ---------------------------------------------------------------- 2) 与当前一致时对比
    Write-Host "`n=== 2) 对比（当前与模板一致）===" -ForegroundColor Cyan
    $diff0 = Call -Path '/api/config/templates/diff?name=T1'
    Add-Check '对比 200' 200 $diff0.status
    Add-Check '一致时 same=true' 'True' $diff0.body.same
    Add-Check '一致时改动数 0' 0 $diff0.body.changeCount

    # ---------------------------------------------------------------- 3) 改乱当前配置后再对比
    Write-Host "`n=== 3) 改乱当前配置：看差异能不能列清楚 ===" -ForegroundColor Cyan
    Set-Cameras -Name 'cameras-4.txt' -Lines @('ip=172.20.10.11,pos=left', 'ip=172.20.10.12,pos=bottom', 'ip=172.20.10.13,pos=left', 'ip=172.20.10.99,pos=spare')
    Set-TriggerMode -Mode 'hard'
    $storage = (Call -Path '/api/config/storage').body.options
    $storage.retentionDays = 30
    $null = Call -Path '/api/config/storage' -Method POST -Body $storage

    $diff1 = Call -Path '/api/config/templates/diff?name=T1'
    Add-Check '有差异时 same=false' 'False' $diff1.body.same
    Add-Check '改动数 >= 3（方位/多出/触发/存图）' 'True' ($diff1.body.changeCount -ge 3)
    $kinds = @($diff1.body.changes | ForEach-Object { $_.kind + '|' + $_.area })
    Write-Host ("   实际差异明细：" + ($kinds -join ' ; ')) -ForegroundColor DarkGray
    # 把每条差异拍平成一个字符串再匹配：比 Where-Object 逐属性比较稳（也不受 JSON 层级影响）
    $flat = ($diff1.body.changes | ForEach-Object {
        "[$($_.area)] $($_.kind) $($_.item) 模板=$($_.template) 当前=$($_.current)"
    }) -join "`n"
    Write-Host ("   第一条明细：" + (@($flat -split "`n")[0])) -ForegroundColor DarkGray
    Add-Check '能指出相机多出 1 台' 'True' ($flat -match '多出.*172\.20\.10\.99')
    Add-Check '能指出方位不同' 'True' ($flat -match '方位不同.*172\.20\.10\.11')
    Add-Check '能指出触发模式不同' 'True' ($flat -match '\[触发模式\]')
    Add-Check '能指出存图天数不同' 'True' ($flat -match '图片保存天数')

    # ---------------------------------------------------------------- 4) 套用模板（相机 + 触发）
    Write-Host "`n=== 4) 套用模板：相机清单 + 触发模式 ===" -ForegroundColor Cyan
    $apply = Call -Path '/api/config/templates/apply' -Method POST -Body @{
        name = 'T1'; applyCameras = $true; applyTrigger = $true; applyStorage = $false
        skipVerify = $true; stopHost = $false; restartHost = $false
    }
    Add-Check '套用 200' 200 $apply.status
    Add-Check '套用了 3 台相机' 3 $apply.body.applied.cameras
    Add-Check '一键应用成功（ok=true）' 'True' $apply.body.apply.ok
    $cfgText = [System.IO.File]::ReadAllText($cfgPath)
    Add-Check 'cfg 里不再有第 4 台' 'False' ($cfgText -match '172\.20\.10\.99')
    Add-Check 'cfg 里 3 台都在' 'True' ((([regex]::Matches($cfgText, '172\.20\.10\.1[123]')).Count) -ge 3)
    $mode = (Call -Path '/api/config').body.triggerMode
    Add-Check '触发模式回到软触发（2）' '2' $mode

    $diff2 = Call -Path '/api/config/templates/diff?name=T1'
    Add-Check '套用后只剩存图差异' 'True' (($diff2.body.changes | Where-Object { $_.area -ne '存图策略' }).Count -eq 0)

    # ---------------------------------------------------------------- 5) 套用存图策略
    Write-Host "`n=== 5) 套用模板：存图策略 ===" -ForegroundColor Cyan
    $apply2 = Call -Path '/api/config/templates/apply' -Method POST -Body @{
        name = 'T1'; applyCameras = $false; applyTrigger = $false; applyStorage = $true
        skipVerify = $true; stopHost = $false; restartHost = $false
    }
    Add-Check '套用存图策略 200' 200 $apply2.status
    Add-Check '存图策略已写入（ok=true）' 'True' $apply2.body.storage.ok
    Add-Check '存图天数回到模板值 7' 7 (Call -Path '/api/config/storage').body.options.retentionDays
    Add-Check '存图策略改动也自动备份' 'True' ($apply2.body.storage.backup -and (Test-Path $apply2.body.storage.backup))

    $diff3 = Call -Path '/api/config/templates/diff?name=T1'
    Add-Check '全部套用后与模板完全一致' 'True' $diff3.body.same

    # ---------------------------------------------------------------- 6) 名称与参数校验
    Write-Host "`n=== 6) 名称与参数校验 ===" -ForegroundColor Cyan
    $empty = Call -Path '/api/config/templates' -Method POST -Body @{ name = ''; note = '' }
    Add-Check '空名字被拒' 400 $empty.status
    $dup = Call -Path '/api/config/templates' -Method POST -Body @{ name = 'T1'; note = '' }
    Add-Check '重名被拒' 400 $dup.status
    Add-Check '重名提示清楚' 'True' ($dup.body.error -match '已存在')
    $escape = Call -Path '/api/config/templates' -Method POST -Body @{ name = '..\evil'; note = '' }
    Add-Check '带路径的名字被拒' 400 $escape.status
    $noTpl = Call -Path '/api/config/templates/apply' -Method POST -Body @{ name = 'NOPE'; applyCameras = $true; applyTrigger = $true; applyStorage = $false }
    Add-Check '套用不存在的模板被拒' 400 $noTpl.status
    $nothing = Call -Path '/api/config/templates/apply' -Method POST -Body @{ name = 'T1'; applyCameras = $false; applyTrigger = $false; applyStorage = $false }
    Add-Check '一项都不勾被拒' 400 $nothing.status

    # ---------------------------------------------------------------- 7) 导出与删除
    Write-Host "`n=== 7) 导出与删除 ===" -ForegroundColor Cyan
    $download = Call -Path '/api/config/templates/download?name=T1'
    Add-Check '导出模板 200' 200 $download.status
    Add-Check '导出的内容是可用的模板 json' 'True' (($download.raw -match '"cameras"') -and ($download.raw -match '"triggerMode"'))

    $delete = Call -Path '/api/config/templates/delete' -Method POST -Body @{ name = 'T1' }
    Add-Check '删除模板 200' 200 $delete.status
    Add-Check '删除后文件没了' 'False' (Test-Path $tplFile)
    Add-Check '删除后列表为空' 0 @((Call -Path '/api/config/templates').body.templates).Count
    $deleteAgain = Call -Path '/api/config/templates/delete' -Method POST -Body @{ name = 'T1' }
    Add-Check '重复删除被拒' 400 $deleteAgain.status

    # ---------------------------------------------------------------- 8) 前端产物
    Write-Host "`n=== 8) 部署产物里带配置模板界面 ===" -ForegroundColor Cyan
    $html = [System.IO.File]::ReadAllText((Join-Path $WorkDir 'platform\wwwroot\index.html'))
    Add-Check '有配置模板面板' 'True' ($html -match 'id="btnTplSave"')
    Add-Check '有模板列表' 'True' ($html -match 'id="tplRows"')
    Add-Check '有套用选项' 'True' (($html -match 'id="tplApplyCameras"') -and ($html -match 'id="tplApplyStorage"'))
    Add-Check '有差异对比输出' 'True' ($html -match 'id="tplDiff"')

    $cfgJs = [System.IO.File]::ReadAllText((Join-Path $WorkDir 'platform\wwwroot\js\config.js'))
    Add-Check 'config.js 有模板逻辑' 'True' (($cfgJs -match 'refreshTemplates') -and ($cfgJs -match 'applyTemplate'))
    $apiJs = [System.IO.File]::ReadAllText((Join-Path $WorkDir 'platform\wwwroot\js\api.js'))
    Add-Check 'api.js 有模板接口' 'True' (($apiJs -match 'api/config/templates') -and ($apiJs -match 'templates/apply'))
}
finally {
    Stop-Platform
}

Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('A8-3 回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    exit 0
}
Write-Host ('A8-3 回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
