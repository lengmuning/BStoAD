<#
.SYNOPSIS
  卸载 BSTOAD 定时任务。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "需要以管理员身份运行此脚本"
    exit 1
}

foreach ($name in @("BSTOAD-Sync", "BSTOAD-FullSync")) {
    $t = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if ($t) {
        Unregister-ScheduledTask -TaskName $name -Confirm:$false
        Write-Host "  ✓ 移除 $name" -ForegroundColor Green
    } else {
        Write-Host "  - $name 不存在，跳过" -ForegroundColor Yellow
    }
}
Write-Host "==> 完成" -ForegroundColor Green
