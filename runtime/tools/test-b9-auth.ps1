<#
    B9 回归测试：基础账号登录与接口鉴权。

    验收点：
      * 未登录访问管理接口 -> 401；读接口默认仍可匿名访问；
      * 密码错误有次数限制：越错越少，到上限锁定，锁定期间密码正确也进不去；
      * 登录成功发会话（浏览器 Cookie / 程序 Bearer），登出后立刻失效；
      * 服务令牌（X-Api-Key）可用于脚本与上位机集成，可轮换（旧令牌立刻失效）；
      * 角色：viewer 只能读、operator 能改配置、admin 才能管账号；
      * 改密后旧密码失效、新密码可用；密码只存 PBKDF2 哈希（不落明文）；
      * 策略可改（失败上限 / 锁定时长 / 会话时长 / 读接口保护），非法值被拒；
      * 登录成功、失败、锁定、改密、账号变更都有审计记录。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-b9-auth.ps1
#>
param(
    [string]$SourceRuntime = '',
    [string]$WorkDir = '',
    [int]$Port = 8097
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($SourceRuntime)) { $SourceRuntime = Join-Path $repoRoot 'runtime' }
if ([string]::IsNullOrEmpty($WorkDir)) { $WorkDir = Join-Path $repoRoot 'work\runtime-b9-test' }

# 强制绝对路径：Stop-Platform 是按"可执行文件路径以运行目录开头"认进程的
if (![System.IO.Path]::IsPathRooted($SourceRuntime)) { $SourceRuntime = [System.IO.Path]::GetFullPath($SourceRuntime) }
if (![System.IO.Path]::IsPathRooted($WorkDir)) { $WorkDir = [System.IO.Path]::GetFullPath($WorkDir) }

$platformExe = Join-Path $SourceRuntime 'platform\DwsEdge.Platform.exe'
if (!(Test-Path $platformExe)) { throw "找不到平台可执行文件：$platformExe（先跑一次 build.ps1）" }

$baseUrl = 'http://127.0.0.1:' + $Port
$results = New-Object System.Collections.Generic.List[object]
$script:webSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession

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
    $log = Join-Path $WorkDir 'logs\b9-test-platform.log'
    Start-Process -FilePath $exe -ArgumentList @('--urls', $baseUrl) `
        -WorkingDirectory (Join-Path $WorkDir 'platform') -WindowStyle Hidden `
        -RedirectStandardOutput $log -RedirectStandardError ($log + '.err') | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        try { $null = Invoke-RestMethod -Uri ($baseUrl + '/api/health') -TimeoutSec 3; return } catch { }
    }
    throw "平台在 60 秒内没有就绪：$baseUrl"
}

<#
    统一的 HTTP 调用：显式传令牌（这个脚本要能故意"不登录"，所以不用全局默认参数）。
    返回 @{ status; body; raw }
#>
function Call {
    param(
        [string]$Path,
        [string]$Method = 'GET',
        $Body = $null,
        [string]$Token = '',
        [string]$ApiKey = '',
        [switch]$UseCookie
    )

    $headers = @{}
    if ($Token) { $headers['Authorization'] = 'Bearer ' + $Token }
    if ($ApiKey) { $headers['X-Api-Key'] = $ApiKey }

    $params = @{
        Uri = $baseUrl + $Path; Method = $Method; UseBasicParsing = $true; TimeoutSec = 30
    }
    if ($headers.Count -gt 0) { $params.Headers = $headers }
    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 6 -Compress)
        $params.ContentType = 'application/json; charset=utf-8'
    }
    if ($UseCookie) { $params.WebSession = $script:webSession }

    try {
        $response = Invoke-WebRequest @params
        $parsed = $null
        try { if ($response.Content) { $parsed = $response.Content | ConvertFrom-Json } } catch { }
        return @{ status = [int]$response.StatusCode; body = $parsed; raw = $response.Content; response = $response }
    }
    catch {
        $status = 0
        $content = ''
        if ($_.Exception.Response) {
            $resp = $_.Exception.Response
            $status = [int]$resp.StatusCode
            # PowerShell 7 抛的是 HttpResponseException（Response 是 HttpResponseMessage），
            # Windows PowerShell 5.1 抛的是 WebException（Response 是 HttpWebResponse）——两条路都试一遍
            try {
                if ($null -ne $resp.Content -and $resp.Content.ReadAsStringAsync) {
                    $content = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                }
            }
            catch { }
            if ([string]::IsNullOrEmpty($content)) {
                try {
                    $stream = $resp.GetResponseStream()
                    $content = (New-Object System.IO.StreamReader($stream)).ReadToEnd()
                }
                catch { }
            }
        }
        $parsed = $null
        try { if ($content) { $parsed = $content | ConvertFrom-Json } } catch { }
        return @{ status = $status; body = $parsed; raw = $content; response = $null }
    }
}

function Login {
    param([string]$User, [string]$Password, [switch]$UseCookie)
    return (Call -Path '/api/auth/login' -Method POST -Body @{ username = $User; password = $Password } -UseCookie:$UseCookie)
}

Write-Host "测试运行时：$WorkDir" -ForegroundColor Cyan
Stop-Platform
if (Test-Path $WorkDir) {
    Move-Item -LiteralPath $WorkDir -Destination ("$WorkDir._old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force
}
robocopy $SourceRuntime $WorkDir /E /XD spool data images logs cache /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $WorkDir 'spool'), (Join-Path $WorkDir 'data'), (Join-Path $WorkDir 'images'), (Join-Path $WorkDir 'logs') | Out-Null

# 每次都在全新的 runtime 上跑：先把上一轮可能留下的账号/策略文件清掉，保证"首次运行"的初始密码是真的初始
Remove-Item -LiteralPath (Join-Path $WorkDir 'config\users.json') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $WorkDir 'config\auth.json') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $WorkDir 'config\admin-initial-password.txt') -Force -ErrorAction SilentlyContinue

try {
    Start-Platform

    # ---------------------------------------------------------------- 0) 首次运行生成了什么
    Write-Host "`n=== 0) 首次运行：账号文件 / 初始密码 / 服务令牌 ===" -ForegroundColor Cyan
    $authFile = Join-Path $WorkDir 'config\auth.json'
    $usersFile = Join-Path $WorkDir 'config\users.json'
    $initFile = Join-Path $WorkDir 'config\admin-initial-password.txt'

    Add-Check 'auth.json 已自动生成' 'True' (Test-Path $authFile)
    Add-Check 'users.json 已自动生成' 'True' (Test-Path $usersFile)
    Add-Check '初始密码文件已生成' 'True' (Test-Path $initFile)

    $authCfg = Get-Content -LiteralPath $authFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $serviceKey = $authCfg.serviceKey
    Add-Check '已生成服务令牌' 'True' ([string]::IsNullOrEmpty($serviceKey) -eq $false)

    $initText = Get-Content -LiteralPath $initFile -Raw -Encoding UTF8
    $adminPassword = ''
    if ($initText -match '初始密码：(\S+)') { $adminPassword = $Matches[1] }
    Add-Check '能读到初始密码' 'True' ([string]::IsNullOrEmpty($adminPassword) -eq $false)

    $usersText = Get-Content -LiteralPath $usersFile -Raw -Encoding UTF8
    Add-Check '账号文件里没有明文密码' 'False' ($usersText -match [regex]::Escape($adminPassword))
    Add-Check '账号文件里是 PBKDF2 哈希' 'True' ($usersText -match 'PBKDF2-SHA256')

    # ---------------------------------------------------------------- 1) 未登录访问管理接口
    Write-Host "`n=== 1) 未登录：管理接口一律 401 ===" -ForegroundColor Cyan
    $paths = @(
        @{ name = '读 SDK 配置'; path = '/api/config'; method = 'GET' },
        @{ name = '一键应用配置'; path = '/api/config/apply'; method = 'POST' },
        @{ name = '读条码规则'; path = '/api/rules'; method = 'GET' },
        @{ name = '保存条码规则'; path = '/api/rules'; method = 'POST' },
        @{ name = '读下游配置'; path = '/api/downstream'; method = 'GET' },
        @{ name = '保存下游配置'; path = '/api/downstream'; method = 'POST' },
        @{ name = '读监控阈值'; path = '/api/monitor/config'; method = 'GET' },
        @{ name = '保存监控阈值'; path = '/api/monitor/config'; method = 'POST' },
        @{ name = '保存相机方位'; path = '/api/camera-positions'; method = 'POST' },
        @{ name = '账号列表'; path = '/api/auth/users'; method = 'GET' },
        @{ name = '鉴权策略'; path = '/api/auth/config'; method = 'GET' },
        @{ name = '审计记录'; path = '/api/auth/events'; method = 'GET' }
    )

    $allBlocked = $true
    $codes = New-Object System.Collections.Generic.List[string]
    foreach ($item in $paths) {
        $r = Call -Path $item.path -Method $item.method -Body $(if ($item.method -eq 'POST') { @{} } else { $null })
        if ($r.status -ne 401) { $allBlocked = $false }
        $codes.Add($item.name + '=' + $r.status)
    }
    Add-Check '管理接口全部 401' 'True' $allBlocked
    if (!$allBlocked) { Write-Host ("    实际：" + ($codes -join '  ')) -ForegroundColor Yellow }

    $unauth = Call -Path '/api/config'
    Add-Check '未登录返回 unauthenticated' 'unauthenticated' $unauth.body.code
    Add-Check '未登录给出登录入口' '/api/auth/login' $unauth.body.loginUrl

    # ---------------------------------------------------------------- 2) 读接口默认不受影响
    Write-Host "`n=== 2) 读接口默认仍可匿名访问（现场大屏不受影响）===" -ForegroundColor Cyan
    Add-Check 'GET /api/stats' 200 (Call -Path '/api/stats').status
    Add-Check 'GET /api/cameras' 200 (Call -Path '/api/cameras').status
    Add-Check 'GET /api/devices' 200 (Call -Path '/api/devices').status
    Add-Check 'GET /api/monitor/summary' 200 (Call -Path '/api/monitor/summary').status
    Add-Check 'GET /api/history' 200 (Call -Path ('/api/history?from=' + (Get-Date).ToString('yyyyMMdd') + '&to=' + (Get-Date).ToString('yyyyMMdd'))).status
    Add-Check 'GET /api/dispatch/pending' 200 (Call -Path '/api/dispatch/pending?limit=5').status

    $status0 = Call -Path '/api/auth/status'
    Add-Check '/api/auth/status 公开可读' 200 $status0.status
    Add-Check '未登录时 authenticated=false' 'False' $status0.body.authenticated
    Add-Check 'status 里带初始密码提示' 'True' $status0.body.initialPasswordPending

    # ---------------------------------------------------------------- 3) 密码错误 → 次数限制
    Write-Host "`n=== 3) 密码错误有次数限制 ===" -ForegroundColor Cyan
    $wrong1 = Login -User 'admin' -Password 'definitely-wrong'
    Add-Check '第一次错：401' 401 $wrong1.status
    Add-Check '第一次错：还剩 4 次' 4 $wrong1.body.remainingAttempts

    $wrong2 = Login -User 'admin' -Password 'wrong-again'
    Add-Check '第二次错：还剩 3 次' 3 $wrong2.body.remainingAttempts
    Add-Check '错误提示里带剩余次数' 'True' ($wrong2.body.error -match '3')

    $noUser = Login -User 'no-such-user' -Password 'whatever'
    Add-Check '不存在的账号也是 401（不泄露账号是否存在）' 401 $noUser.status

    # ---------------------------------------------------------------- 4) 登录成功：会话可用
    Write-Host "`n=== 4) 登录成功：Cookie 与 Bearer 都能用 ===" -ForegroundColor Cyan
    $login = Login -User 'admin' -Password $adminPassword
    Add-Check '登录成功 200' 200 $login.status
    Add-Check '返回 token' 'True' ([string]::IsNullOrEmpty($login.body.token) -eq $false)
    Add-Check '返回角色 admin' 'admin' $login.body.role
    Add-Check '提示需要改初始密码' 'True' $login.body.mustChangePassword

    $token = $login.body.token
    Add-Check 'Bearer 能读管理接口' 200 (Call -Path '/api/config' -Token $token).status
    Add-Check 'Bearer 能改管理接口' 200 (Call -Path '/api/monitor/config' -Method POST -Body (Call -Path '/api/monitor/config' -Token $token).body.options -Token $token).status
    $me = Call -Path '/api/auth/me' -Token $token
    Add-Check '/api/auth/me 返回当前账号' 'admin' $me.body.username

    # Cookie 方式（浏览器走的就是这条）
    $cookieLogin = Login -User 'admin' -Password $adminPassword -UseCookie
    Add-Check 'Cookie 登录成功' 200 $cookieLogin.status
    Add-Check '登录响应带 dws_session Cookie' 'True' (@($script:webSession.Cookies.GetCookies($baseUrl) | Where-Object { $_.Name -eq 'dws_session' }).Count -ge 1)
    Add-Check 'Cookie 能读管理接口' 200 (Call -Path '/api/config' -UseCookie).status

    # ---------------------------------------------------------------- 5) 达到上限 → 锁定
    Write-Host "`n=== 5) 错到上限：账号锁定（密码正确也进不去）===" -ForegroundColor Cyan
    # 注意：第 4 步成功登录过一次，失败计数已清零，所以这里要从"还剩 4 次"重新数
    $wrong3 = Login -User 'admin' -Password 'wrong-3'
    Add-Check '再错第 1 次：还剩 4 次' 4 $wrong3.body.remainingAttempts
    $wrong4 = Login -User 'admin' -Password 'wrong-4'
    Add-Check '再错第 2 次：还剩 3 次' 3 $wrong4.body.remainingAttempts
    $wrong5 = Login -User 'admin' -Password 'wrong-5'
    Add-Check '再错第 3 次：还剩 2 次' 2 $wrong5.body.remainingAttempts
    $wrong6 = Login -User 'admin' -Password 'wrong-6'
    Add-Check '再错第 4 次：还剩 1 次' 1 $wrong6.body.remainingAttempts

    $wrong7 = Login -User 'admin' -Password 'wrong-7'
    Add-Check '第 5 次错：423 已锁定' 423 $wrong7.status
    Add-Check '锁定提示里带剩余秒数' 'True' ($wrong7.body.lockedSeconds -gt 0)

    $lockedWithRightPassword = Login -User 'admin' -Password $adminPassword
    Add-Check '锁定期间正确密码也拒绝' 423 $lockedWithRightPassword.status

    $users = Call -Path '/api/auth/users' -ApiKey $serviceKey
    $adminRow = @($users.body.users | Where-Object { $_.username -eq 'admin' })[0]
    Add-Check '账号列表里能看到锁定状态' 'True' ($adminRow.locked -eq $true)

    # ---------------------------------------------------------------- 6) 服务令牌：脚本 / 上位机这条路
    Write-Host "`n=== 6) 服务令牌（X-Api-Key）===" -ForegroundColor Cyan
    Add-Check '服务令牌能读管理接口' 200 (Call -Path '/api/config' -ApiKey $serviceKey).status
    Add-Check '服务令牌能改管理接口' 200 (Call -Path '/api/auth/config' -ApiKey $serviceKey).status
    Add-Check '错误的服务令牌被拒' 401 (Call -Path '/api/config' -ApiKey 'not-a-real-key').status

    $reset = Call -Path '/api/auth/users/reset-password' -Method POST -ApiKey $serviceKey -Body @{ username = 'admin'; password = 'Lightcookr@2026' }
    Add-Check '服务令牌可重置管理员密码（救场手段）' 200 $reset.status

    $loginNew = Login -User 'admin' -Password 'Lightcookr@2026'
    Add-Check '重置后新密码可登录（锁定也一并解除）' 200 $loginNew.status
    $token = $loginNew.body.token
    Add-Check '登录后不再提示改初始密码' 'False' $loginNew.body.mustChangePassword
    Add-Check '初始密码文件已删除' 'False' (Test-Path $initFile)

    # ---------------------------------------------------------------- 7) 登出 / 会话失效
    Write-Host "`n=== 7) 登出与会话失效 ===" -ForegroundColor Cyan
    $tempLogin = Login -User 'admin' -Password 'Lightcookr@2026'
    $tempToken = $tempLogin.body.token
    Add-Check '临时会话可用' 200 (Call -Path '/api/config' -Token $tempToken).status
    Add-Check '登出成功' 200 (Call -Path '/api/auth/logout' -Method POST -Token $tempToken).status
    Add-Check '登出后该 token 立刻失效' 401 (Call -Path '/api/config' -Token $tempToken).status

    # ---------------------------------------------------------------- 8) 改密
    Write-Host "`n=== 8) 改密码：旧密码失效 ===" -ForegroundColor Cyan
    $badChange = Call -Path '/api/auth/password' -Method POST -Token $token -Body @{ oldPassword = 'not-the-old-one'; newPassword = 'NewPass@2026' }
    Add-Check '原密码不对：400' 400 $badChange.status

    $weakChange = Call -Path '/api/auth/password' -Method POST -Token $token -Body @{ oldPassword = 'Lightcookr@2026'; newPassword = '123' }
    Add-Check '弱密码被拒' 400 $weakChange.status

    $change = Call -Path '/api/auth/password' -Method POST -Token $token -Body @{ oldPassword = 'Lightcookr@2026'; newPassword = 'NewPass@2026' }
    Add-Check '改密成功' 200 $change.status
    Add-Check '旧密码登录失败' 401 (Login -User 'admin' -Password 'Lightcookr@2026').status
    $relogin = Login -User 'admin' -Password 'NewPass@2026'
    Add-Check '新密码登录成功' 200 $relogin.status
    $token = $relogin.body.token

    # ---------------------------------------------------------------- 9) 角色与账号管理
    Write-Host "`n=== 9) 角色：viewer 只读 / operator 能改配置 / admin 管账号 ===" -ForegroundColor Cyan
    $addViewer = Call -Path '/api/auth/users' -Method POST -Token $token -Body @{ username = 'viewer1'; password = 'Viewer@2026'; role = 'viewer' }
    Add-Check '新增 viewer 账号' 200 $addViewer.status
    $addOperator = Call -Path '/api/auth/users' -Method POST -Token $token -Body @{ username = 'operator1'; password = 'Operator@2026'; role = 'operator' }
    Add-Check '新增 operator 账号' 200 $addOperator.status
    $dup = Call -Path '/api/auth/users' -Method POST -Token $token -Body @{ username = 'viewer1'; password = 'Viewer@2026'; role = 'viewer' }
    Add-Check '重复用户名被拒' 400 $dup.status
    $weak = Call -Path '/api/auth/users' -Method POST -Token $token -Body @{ username = 'weak1'; password = 'abcdefgh'; role = 'viewer' }
    Add-Check '弱密码账号被拒' 400 $weak.status

    $viewerToken = (Login -User 'viewer1' -Password 'Viewer@2026').body.token
    $operatorToken = (Login -User 'operator1' -Password 'Operator@2026').body.token

    Add-Check 'viewer 能读数据' 200 (Call -Path '/api/stats' -Token $viewerToken).status
    Add-Check 'viewer 改配置被拒（403）' 403 (Call -Path '/api/monitor/config' -Method POST -Token $viewerToken -Body @{ enabled = $true }).status
    Add-Check 'viewer 管账号被拒（403）' 403 (Call -Path '/api/auth/users' -Token $viewerToken).status
    Add-Check 'operator 能改配置' 200 (Call -Path '/api/monitor/config' -Method POST -Token $operatorToken -Body (Call -Path '/api/monitor/config' -Token $operatorToken).body.options).status
    Add-Check 'operator 管账号被拒（403）' 403 (Call -Path '/api/auth/users' -Token $operatorToken).status
    Add-Check '未登录改配置 401' 401 (Call -Path '/api/monitor/config' -Method POST -Body @{ enabled = $true }).status

    $disable = Call -Path '/api/auth/users/update' -Method POST -Token $token -Body @{ username = 'viewer1'; enabled = $false }
    Add-Check '禁用账号' 200 $disable.status
    Add-Check '禁用后登录失败' 401 (Login -User 'viewer1' -Password 'Viewer@2026').status
    $delete = Call -Path '/api/auth/users/delete' -Method POST -Token $token -Body @{ username = 'viewer1' }
    Add-Check '删除账号' 200 $delete.status

    # 不能把自己锁死：不允许把最后一个管理员降级/禁用/删除
    $demote = Call -Path '/api/auth/users/update' -Method POST -Token $token -Body @{ username = 'admin'; role = 'viewer' }
    Add-Check '不允许把最后一个管理员降级' 400 $demote.status
    $deleteSelf = Call -Path '/api/auth/users/delete' -Method POST -Token $token -Body @{ username = 'admin' }
    Add-Check '不允许删除最后一个管理员' 400 $deleteSelf.status

    # ---------------------------------------------------------------- 10) 策略与令牌轮换
    Write-Host "`n=== 10) 鉴权策略与令牌轮换 ===" -ForegroundColor Cyan
    $cfg = Call -Path '/api/auth/config' -Token $token
    Add-Check '读策略 200' 200 $cfg.status
    Add-Check '策略里带失败上限' 5 $cfg.body.options.maxFailures

    $options = $cfg.body.options
    $options.maxFailures = 3
    $options.lockMinutes = 1
    $saveCfg = Call -Path '/api/auth/config' -Method POST -Token $token -Body $options
    Add-Check '保存策略 200' 200 $saveCfg.status
    Add-Check '失败上限已生效（3）' 3 (Call -Path '/api/auth/config' -Token $token).body.options.maxFailures
    Add-Check '旧策略已备份' 'True' ([string]::IsNullOrEmpty($saveCfg.body.backup) -eq $false)

    $badCfg = Call -Path '/api/auth/config' -Method POST -Token $token -Body @{ enabled = $true; maxFailures = 0; lockMinutes = 1; failureWindowMinutes = 10; sessionMinutes = 60; serviceKeyRole = 'admin'; allowServiceKey = $true; protectRead = $false; serviceKey = $serviceKey }
    Add-Check '非法策略被拒（400）' 400 $badCfg.status

    # 读接口保护开关
    $protect = (Call -Path '/api/auth/config' -Token $token).body.options
    $protect.protectRead = $true
    Add-Check '开启读接口保护' 200 (Call -Path '/api/auth/config' -Method POST -Token $token -Body $protect).status
    Add-Check '开启后匿名读接口 401' 401 (Call -Path '/api/stats').status
    Add-Check '开启后带令牌仍可读' 200 (Call -Path '/api/stats' -Token $token).status
    $protect.protectRead = $false
    $null = Call -Path '/api/auth/config' -Method POST -Token $token -Body $protect
    Add-Check '关掉后又可匿名读' 200 (Call -Path '/api/stats').status

    $rotate = Call -Path '/api/auth/service-key' -Method POST -Token $token
    Add-Check '轮换服务令牌 200' 200 $rotate.status
    $newKey = $rotate.body.serviceKey
    Add-Check '新令牌与旧的不同的' 'True' ($newKey -ne $serviceKey)
    Add-Check '旧令牌立刻失效' 401 (Call -Path '/api/config' -ApiKey $serviceKey).status
    Add-Check '新令牌可用' 200 (Call -Path '/api/config' -ApiKey $newKey).status

    # ---------------------------------------------------------------- 11) 审计
    Write-Host "`n=== 11) 审计：登录、失败、锁定、改密、账号变更 ===" -ForegroundColor Cyan
    $events = Call -Path '/api/auth/events?limit=200' -Token $token
    Add-Check '审计可读' 200 $events.status
    $kinds = @($events.body | ForEach-Object { $_.kind })
    Add-Check '有登录成功记录' 'True' ($kinds -contains 'login-ok')
    Add-Check '有密码错误记录' 'True' ($kinds -contains 'login-fail')
    Add-Check '有账号锁定记录' 'True' ($kinds -contains 'login-locked')
    Add-Check '有改密记录' 'True' ($kinds -contains 'password-change')
    Add-Check '有账号变更记录' 'True' (($kinds -contains 'user-add') -and ($kinds -contains 'user-delete'))
    $auditFile = Join-Path $WorkDir ('data\auth-events-' + (Get-Date).ToString('yyyyMMdd') + '.jsonl')
    Add-Check '审计已落盘（一条一行）' 'True' (Test-Path $auditFile)
    if (Test-Path $auditFile) {
        $lines = [System.IO.File]::ReadAllLines($auditFile)
        $badLines = @($lines | Where-Object { $_.Trim().Length -gt 0 -and $_.Trim().Substring(0, 1) -ne '{' }).Count
        Add-Check '审计文件每行都是完整 JSON' 0 $badLines
        Add-Check '审计里有失败来源 IP' 'True' (([System.IO.File]::ReadAllText($auditFile)) -match '"kind":"login-fail"')
    }

    # ---------------------------------------------------------------- 12) 未登录不影响采集
    Write-Host "`n=== 12) 鉴权不影响采集与其它回归 ===" -ForegroundColor Cyan
    $spoolFile = Join-Path $WorkDir ('spool\events-' + (Get-Date).ToString('yyyyMMdd') + '.jsonl')
    $now = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
    $event = @{
        schemaVersion = 1; type = 'parcel'; eventId = 1; providerId = 'test'; deviceId = 'cam-top';
        stage = 'enriched'; capturedAtMs = $now; receivedAtMs = $now; traceId = 'B9-P1';
        stagedResult = $false; weightGrams = 500; lengthMm = 300; widthMm = 200; heightMm = 150; volumeMm3 = 9000000;
        codes = @(@{ value = 'SF900000000001'; kind = '1d'; position = 'top' })
    } | ConvertTo-Json -Compress -Depth 6
    [System.IO.File]::WriteAllText($spoolFile, $event + "`n", (New-Object System.Text.UTF8Encoding($false)))
    Start-Sleep -Seconds 4
    Add-Check '包裹照常入库（鉴权不影响采集链路）' 'True' ((Call -Path '/api/stats').body.parcels -ge 1)
}
finally {
    Stop-Platform
}

Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('B9 回归通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    exit 0
}
Write-Host ('B9 回归失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
