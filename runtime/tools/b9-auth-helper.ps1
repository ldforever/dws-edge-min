<#
    B9 测试辅助：让回归脚本的 HTTP 调用带上服务令牌（X-Api-Key）。

    背景：平台启用接口鉴权后，管理接口（配置 / 规则 / 下游 / 监控阈值 / 账号）必须带凭据。
    回归脚本走的是"脚本 / 上位机"这条路，所以直接用 auth.json 里自动生成的服务令牌，
    而不是模拟浏览器登录 —— 顺便也就把"服务令牌能不能用"这条验证了。

    用法（在第一次 Start-Platform 之后调用一次即可，之后脚本里所有
    Invoke-RestMethod / Invoke-WebRequest 都会自动带上这个头）：

        . (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'b9-auth-helper.ps1')
        Enable-TestAuth -WorkDir $WorkDir
#>
function Enable-TestAuth {
    param(
        [Parameter(Mandatory = $true)][string]$WorkDir,
        [string]$ApiKey = ''
    )

    if ([string]::IsNullOrEmpty($ApiKey)) {
        $authFile = Join-Path $WorkDir 'config\auth.json'
        if (!(Test-Path $authFile)) {
            Write-Host "  （没找到 $authFile：平台没启用鉴权，脚本按匿名访问继续）" -ForegroundColor Yellow
            return $false
        }
        $cfg = Get-Content -LiteralPath $authFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $ApiKey = $cfg.serviceKey
    }

    if ([string]::IsNullOrEmpty($ApiKey)) {
        Write-Host "  （auth.json 里没有服务令牌，脚本按匿名访问继续）" -ForegroundColor Yellow
        return $false
    }

    $headers = @{ 'X-Api-Key' = $ApiKey }
    $global:PSDefaultParameterValues['Invoke-RestMethod:Headers'] = $headers
    $global:PSDefaultParameterValues['Invoke-WebRequest:Headers'] = $headers
    return $true
}
