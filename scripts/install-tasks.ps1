<#
.SYNOPSIS
  在 Windows 上注册 BSTOAD 定时任务。

.DESCRIPTION
  创建两个计划任务:
    - BSTOAD-Sync     : 每小时跑一次增量同步 (sync --apply)
    - BSTOAD-FullSync : 每天 02:00 跑一次全量同步 + 离职差集 (sync --apply --full)

  必须以管理员身份运行 PowerShell。

.PARAMETER InstallDir
  Bstoad.Worker.exe 所在目录，默认 C:\bstoad

.PARAMETER ServiceAccount
  运行身份，格式 DOMAIN\username。留空则用 SYSTEM。
  建议用一个独立的 AD 服务账号（同 appsettings.json 里 ActiveDirectory.ServiceAccount）。

.PARAMETER HourlyAt
  增量任务每小时在第几分钟跑，默认 7（避开整点高峰）

.PARAMETER FullAt
  全量任务在每日几点跑，默认 02:00

.EXAMPLE
  # 以 SYSTEM 身份跑（最简单，但 LDAP 用 Negotiate 时可能要域账号）
  .\scripts\install-tasks.ps1

.EXAMPLE
  # 指定服务账号跑（推荐）
  .\scripts\install-tasks.ps1 -InstallDir C:\bstoad -ServiceAccount "EXAMPLE\bstoad-svc"
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\bstoad",
    [string]$ServiceAccount = "",
    [ValidateRange(0, 59)][int]$HourlyAt = 7,
    [string]$FullAt = "02:00"
)

$ErrorActionPreference = "Stop"

# 必须管理员
$me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "需要以管理员身份运行此脚本（右键 PowerShell → 以管理员身份运行）"
    exit 1
}

$exe = Join-Path $InstallDir "Bstoad.Worker.exe"
if (-not (Test-Path $exe)) {
    Write-Error "找不到 $exe。请先把打包好的目录拷到 $InstallDir，或用 -InstallDir 指定正确位置。"
    exit 1
}

$cfg = Join-Path $InstallDir "appsettings.json"
if (-not (Test-Path $cfg)) {
    Write-Warning "找不到 $cfg —— exe 会启动失败。请把 appsettings.json 放到同目录。"
}

# 准备密码（如果指定了服务账号）
$securePwd = $null
if ($ServiceAccount) {
    $securePwd = Read-Host -AsSecureString "请输入 $ServiceAccount 的密码"
    if ($securePwd.Length -eq 0) {
        Write-Error "密码不能为空。"
        exit 1
    }
}

function Register-BstoadTask {
    param(
        [string]$Name,
        [string]$Description,
        [string]$Args,
        [Microsoft.Management.Infrastructure.CimInstance]$Trigger
    )

    # 已存在则先删
    $existing = Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "  移除已有任务 $Name" -ForegroundColor Yellow
        Unregister-ScheduledTask -TaskName $Name -Confirm:$false
    }

    $action = New-ScheduledTaskAction `
        -Execute $exe `
        -Argument $Args `
        -WorkingDirectory $InstallDir

    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
        -MultipleInstances IgnoreNew

    if ($ServiceAccount) {
        $pwdPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePwd))
        Register-ScheduledTask `
            -TaskName $Name `
            -Description $Description `
            -Action $action `
            -Trigger $Trigger `
            -Settings $settings `
            -User $ServiceAccount `
            -Password $pwdPlain `
            -RunLevel Highest | Out-Null
        $pwdPlain = $null
    } else {
        Register-ScheduledTask `
            -TaskName $Name `
            -Description $Description `
            -Action $action `
            -Trigger $Trigger `
            -Settings $settings `
            -User "SYSTEM" `
            -RunLevel Highest | Out-Null
    }

    Write-Host "  ✓ 已注册 $Name" -ForegroundColor Green
}

Write-Host "==> 注册定时任务" -ForegroundColor Cyan
Write-Host "    安装目录:   $InstallDir"
Write-Host "    运行身份:   $(if ($ServiceAccount) { $ServiceAccount } else { 'SYSTEM' })"
Write-Host "    增量任务:   每小时 :$('{0:D2}' -f $HourlyAt) → sync --apply"
Write-Host "    全量任务:   每日 $FullAt → sync --apply --full"
Write-Host ""

# 增量：每小时一次（从今天 00:HourlyAt 开始，每隔 1 小时）
$startToday = [DateTime]::Today.AddMinutes($HourlyAt)
$hourlyTrigger = New-ScheduledTaskTrigger `
    -Once `
    -At $startToday `
    -RepetitionInterval (New-TimeSpan -Hours 1) `
    -RepetitionDuration ([TimeSpan]::FromDays(3650))

Register-BstoadTask `
    -Name "BSTOAD-Sync" `
    -Description "北森 → AD 每小时增量同步" `
    -Args "sync --apply" `
    -Trigger $hourlyTrigger

# 全量：每日 FullAt
$dailyTrigger = New-ScheduledTaskTrigger -Daily -At $FullAt

Register-BstoadTask `
    -Name "BSTOAD-FullSync" `
    -Description "北森 → AD 每日全量同步 + 离职差集" `
    -Args "sync --apply --full" `
    -Trigger $dailyTrigger

Write-Host ""
Write-Host "==> 完成" -ForegroundColor Green
Write-Host ""
Write-Host "查看任务: " -NoNewline; Write-Host "Get-ScheduledTask BSTOAD-*" -ForegroundColor Cyan
Write-Host "立即试跑: " -NoNewline; Write-Host "Start-ScheduledTask -TaskName BSTOAD-Sync" -ForegroundColor Cyan
Write-Host "查看日志: " -NoNewline; Write-Host "Get-Content $InstallDir\logs\bstoad-$(Get-Date -Format yyyyMMdd).log -Wait" -ForegroundColor Cyan
Write-Host "卸载任务: " -NoNewline; Write-Host ".\scripts\uninstall-tasks.ps1" -ForegroundColor Cyan
