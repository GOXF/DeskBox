# GOXF fork：拉取最新测试代码，本地编译并运行 DeskBox（Debug）
# 用法：在仓库根目录右键「使用 PowerShell 运行」，或：
#   powershell -ExecutionPolicy Bypass -File .\scripts\fork-dev-update.ps1 [-Branch dev]
param([string]$Branch = "dev")
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

Write-Host "==> 关闭正在运行的 DeskBox" -ForegroundColor Cyan
Get-Process DeskBox -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

Write-Host "==> 拉取 origin/$Branch" -ForegroundColor Cyan
git fetch origin $Branch
git checkout -B $Branch "origin/$Branch"
git log -1 --format="当前提交：%h %s (%cr)"

$props = @("-p:Platform=x64", "-p:RuntimeIdentifier=win-x64", "-p:WindowsAppSDKSelfContained=true")

if (-not (dotnet nuget list source | Select-String "nuget.org")) {
    Write-Host "==> 添加 nuget.org 包源" -ForegroundColor Cyan
    dotnet nuget add source "https://api.nuget.org/v3/index.json" -n nuget.org
}
Write-Host "==> 还原依赖" -ForegroundColor Cyan
dotnet restore .\DeskBox.sln @props
if ($LASTEXITCODE -ne 0) { throw "restore 失败" }

Write-Host "==> 编译 Debug" -ForegroundColor Cyan
dotnet build .\src\DeskBox\DeskBox.csproj -c Debug --no-restore @props -v:minimal
if ($LASTEXITCODE -ne 0) { throw "编译失败" }

$exe = Get-ChildItem .\src\DeskBox\bin -Recurse -Filter DeskBox.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw "没找到 DeskBox.exe" }
Write-Host "==> 启动 $($exe.FullName)" -ForegroundColor Green
Start-Process $exe.FullName
