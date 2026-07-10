<#
.SYNOPSIS
  打包 BSTOAD 成单文件 exe。

.DESCRIPTION
  调用 dotnet publish 出 self-contained + single-file 包到 publish/bstoad/。
  目标机器无需安装 .NET runtime。

.PARAMETER OutputDir
  发布输出目录，默认 publish/bstoad

.PARAMETER Configuration
  Release / Debug，默认 Release

.PARAMETER Runtime
  目标运行时，默认 win-x64（DC 一般是 x64）

.EXAMPLE
  .\scripts\build.ps1
  .\scripts\build.ps1 -OutputDir D:\dist
#>
[CmdletBinding()]
param(
    [string]$OutputDir = "publish/bstoad",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Write-Host "==> 检查 dotnet SDK" -ForegroundColor Cyan
$sdk = (& dotnet --version) 2>$null
if (-not $sdk) {
    Write-Error "未检测到 dotnet SDK。请先安装 .NET 8 SDK: https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0"
    exit 1
}
Write-Host "    SDK 版本: $sdk"

Write-Host "==> 还原依赖" -ForegroundColor Cyan
& dotnet restore
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet restore 失败"; exit 1 }

Write-Host "==> 清理旧产物 $OutputDir" -ForegroundColor Cyan
if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }

Write-Host "==> 发布单文件 (self-contained, runtime=$Runtime)" -ForegroundColor Cyan
& dotnet publish src/Bstoad.Worker `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    -o $OutputDir
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish 失败"; exit 1 }

# 清理不需要的 pdb / 调试文件（可选）
Get-ChildItem -Path $OutputDir -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

# 确保 logs 目录存在
$logsDir = Join-Path $OutputDir "logs"
if (-not (Test-Path $logsDir)) { New-Item -ItemType Directory -Path $logsDir | Out-Null }

$exe = Join-Path $OutputDir "Bstoad.Worker.exe"
$cfg = Join-Path $OutputDir "appsettings.json"
$exeSize = if (Test-Path $exe) { "{0:N1} MB" -f ((Get-Item $exe).Length / 1MB) } else { "N/A" }

Write-Host ""
Write-Host "================ 打包完成 ================" -ForegroundColor Green
Write-Host "输出目录: $((Resolve-Path $OutputDir).Path)" -ForegroundColor Green
Write-Host "  Bstoad.Worker.exe  ($exeSize)" -ForegroundColor Green
Write-Host "  appsettings.json" -ForegroundColor Green
Write-Host "  logs\" -ForegroundColor Green
Write-Host ""
Write-Host "下一步:" -ForegroundColor Yellow
Write-Host "  1. 把整个 $OutputDir 目录拷到目标服务器（建议 C:\bstoad）"
Write-Host "  2. 编辑 appsettings.json，填上 4 个占位符：AppKey / AppSecret / ServiceAccount / ServicePassword"
Write-Host "  3. 跑 .\Bstoad.Worker.exe sync 干跑验证字段映射"
Write-Host "  4. 跑 .\scripts\install-tasks.ps1 -InstallDir C:\bstoad 注册定时任务"
