<#
    前端结构体检（静态，2 秒跑完，不需要起进程）。

    为什么要有它：P0 重构时丢了一个 panel、并少了一个 </section>，结果 stats.js 初始化时
    找不到 5 个元素直接抛异常 —— bootstrap 中断，页面看着"正常"但登录框不弹、按钮全不响应。
    那次的教训是：光看 HTML 有没有 id 不够，必须把"js 引用的 id"和"页面实际有的 id"对一遍。

    检查项：
      1) 页签齐全（9 个）且没有遗留的 page-config；
      2) <section> 开闭配平（少一个 </section> 会把后一个页签嵌进前一页里）；
      3) js 里 $("id") 引用的元素，在 index.html 里都存在（缺一个就会让 JS 初始化崩掉）；
      4) 登录遮罩与登录按钮在页面上（登录框由 JS 显示，标记没了就永远不弹）；
      5) 交付目录里不该有 *.new / *.orig 这类开发中间文件。

    用法：
        powershell -ExecutionPolicy Bypass -File .\tools\test-ui-structure.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\test-ui-structure.ps1 -Runtime D:\dws\runtime
#>
param(
    [string]$Runtime = ''
)

$ErrorActionPreference = 'Stop'

# 兼容两种布局：开发仓库是 <仓库>\tools\ → <仓库>\runtime；交付包里是 <runtime>\tools\ → <runtime>
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$parentDir = Split-Path -Parent $scriptDir
if ([string]::IsNullOrEmpty($Runtime)) {
    foreach ($candidate in @((Join-Path $parentDir 'runtime'), $parentDir)) {
        if (Test-Path (Join-Path $candidate 'platform\wwwroot\index.html')) { $Runtime = $candidate; break }
    }
    if ([string]::IsNullOrEmpty($Runtime)) { $Runtime = Join-Path $parentDir 'runtime' }
}
if (![System.IO.Path]::IsPathRooted($Runtime)) { $Runtime = [System.IO.Path]::GetFullPath($Runtime) }

$webRoot = Join-Path $Runtime 'platform\wwwroot'
if (!(Test-Path $webRoot)) { throw "找不到前端目录：$webRoot" }

$results = New-Object System.Collections.Generic.List[object]
function Add-Check {
    param([string]$Name, $Expected, $Actual)
    $ok = ("$Expected" -eq "$Actual")
    $results.Add([pscustomobject]@{ 检查项 = $Name; 期望 = "$Expected"; 实际 = "$Actual"; 结果 = $(if ($ok) { 'PASS' } else { 'FAIL' }) }) | Out-Null
}

function Read-Utf8([string]$Path) {
    return [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($Path))
}

Write-Host "前端目录：$webRoot" -ForegroundColor Cyan
$html = Read-Utf8 (Join-Path $webRoot 'index.html')

# 1) 页签
$tabs = @('tab-realtime','tab-devices','tab-history','tab-stats','tab-diag','tab-cameras','tab-output','tab-rules','tab-system')
$missingTabs = @($tabs | Where-Object { -not $html.Contains('id="' + $_ + '"') })
Add-Check '九个页签齐全' 0 $missingTabs.Count
Add-Check '没有遗留的 tab-config' 'False' $html.Contains('id="tab-config"')
Add-Check '没有遗留的 page-config' 'False' $html.Contains('id="page-config"')
Add-Check '全局应用条在页头之后' 'True' (($html.IndexOf('id="applyBar"') -gt $html.IndexOf('</header>')) -and ($html.IndexOf('id="applyBar"') -lt $html.IndexOf('<main>')))

# 2) section 配平
$open = ([regex]::Matches($html, '<section\b')).Count
$close = ([regex]::Matches($html, '</section>')).Count
Add-Check 'section 开闭配平' $open $close

# 3) js 引用的 id 必须都存在
$ids = New-Object System.Collections.Generic.HashSet[string]
foreach ($m in [regex]::Matches($html, 'id="([A-Za-z0-9_\-]+)"')) { [void]$ids.Add($m.Groups[1].Value) }

$missing = New-Object System.Collections.Generic.List[string]
$jsFiles = Get-ChildItem (Join-Path $webRoot 'js') -Filter '*.js' -File -ErrorAction SilentlyContinue
foreach ($js in $jsFiles) {
    $text = Read-Utf8 $js.FullName
    foreach ($m in [regex]::Matches($text, '\$<[^>]*>\(\s*"([A-Za-z0-9_\-]+)"\s*\)|\$\(\s*"([A-Za-z0-9_\-]+)"\s*\)')) {
        $name = if ($m.Groups[1].Success) { $m.Groups[1].Value } else { $m.Groups[2].Value }
        if (-not $ids.Contains($name)) {
            $entry = $name + ' ← ' + $js.Name
            if (-not $missing.Contains($entry)) { $missing.Add($entry) }
        }
    }
}
Add-Check 'js 引用的元素都存在（缺一个就会让 JS 初始化崩掉）' 0 $missing.Count
if ($missing.Count -gt 0) { $missing | ForEach-Object { Write-Host ("    缺：" + $_) -ForegroundColor Red } }
# 模块数量别写死（加一个模块就会变）：只做"数量合理"的下限检查，实际值打在表里
$moduleCountOk = $jsFiles.Count -ge 15
$results.Add([pscustomobject]@{ 检查项 = '前端模块数量合理（≥15）'; 期望 = 'True'; 实际 = "$moduleCountOk（实际 $($jsFiles.Count) 个）"; 结果 = $(if ($moduleCountOk) { 'PASS' } else { 'FAIL' }) }) | Out-Null

# 3b) 模块引用要能解析：import 的目标文件不存在（比如少拷了 applybar.js）同样会让 JS 起不来
$badImport = New-Object System.Collections.Generic.List[string]
foreach ($js in $jsFiles) {
    $text = Read-Utf8 $js.FullName
    foreach ($m in [regex]::Matches($text, 'from\s+"\./([A-Za-z0-9_\-\.]+)(?:\?v=[^"]*)?"')) {
        $dep = Join-Path (Join-Path $webRoot 'js') $m.Groups[1].Value
        if (!(Test-Path $dep)) { $badImport.Add($js.Name + ' → ' + $m.Groups[1].Value) }
    }
}
Add-Check 'js 模块 import 都能解析（含版本号后缀）' 0 $badImport.Count
if ($badImport.Count -gt 0) { $badImport | ForEach-Object { Write-Host ("    断链：" + $_) -ForegroundColor Red } }

# 3c) index.html 引用的静态资源要存在
$badAsset = New-Object System.Collections.Generic.List[string]
foreach ($m in [regex]::Matches($html, '(?:src|href)="\./([^"\?]+)(?:\?v=[^"]*)?"')) {
    $rel = $m.Groups[1].Value
    if (!(Test-Path (Join-Path $webRoot ($rel -replace '/', '\')))) { $badAsset.Add($rel) }
}
Add-Check 'index.html 引用的资源都存在' 0 $badAsset.Count

# 4) 登录遮罩（登录框由 JS 显示，标记丢了就永远不弹）
Add-Check '登录遮罩标记存在' 'True' ($html.Contains('id="loginMask"') -and $html.Contains('id="btnLogin"') -and $html.Contains('id="loginUser"') -and $html.Contains('id="loginPass"'))

# 5) 开发中间文件
$junk = @(Get-ChildItem $Runtime -Recurse -File -Include '*.new', '*.orig', '*.rej' -ErrorAction SilentlyContinue)
Add-Check '没有 *.new / *.orig 这类中间文件' 0 $junk.Count
if ($junk.Count -gt 0) { $junk | ForEach-Object { Write-Host ("    多余文件：" + $_.FullName) -ForegroundColor Yellow } }

Write-Host ''
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.结果 -eq 'FAIL' })
if ($failed.Count -eq 0) {
    Write-Host ('前端结构体检通过：' + $results.Count + ' 项全部 PASS') -ForegroundColor Green
    exit 0
}
Write-Host ('前端结构体检失败：' + $failed.Count + ' 项 FAIL（共 ' + $results.Count + ' 项）') -ForegroundColor Red
exit 1
