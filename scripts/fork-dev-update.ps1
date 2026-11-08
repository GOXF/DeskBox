# GOXF fork：拉取最新测试代码，本地编译并运行 DeskBox（Debug）
# 用法：在仓库根目录右键「使用 PowerShell 运行」，或：
#   powershell -ExecutionPolicy Bypass -File .\scripts\fork-dev-update.ps1 [-Branch dev]
# DeskBox 本地测试版的「更新并重启」也会调用本脚本（带 -GraceSeconds，先等 DeskBox 自己退出）。
# 出错时窗口不会自动关闭，按回车后关闭。
param(
    [string]$Branch = "dev",
    [int]$GraceSeconds = 0
)
$ErrorActionPreference = "Stop"

try {
    Set-Location (Split-Path $PSScriptRoot -Parent)

    Write-Host "==> 关闭正在运行的 DeskBox" -ForegroundColor Cyan
    if ($GraceSeconds -gt 0) {
        Wait-Process -Name DeskBox -Timeout $GraceSeconds -ErrorAction SilentlyContinue
    }
    Get-Process DeskBox -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1

    Write-Host "==> 拉取 origin/$Branch" -ForegroundColor Cyan
    git fetch origin $Branch
    if ($LASTEXITCODE -ne 0) { throw "git fetch 失败" }
    git checkout -B $Branch "origin/$Branch"
    if ($LASTEXITCODE -ne 0) { throw "git checkout 失败（本地是否有未提交的改动？）" }
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

    # 桌面快捷方式「DeskBox 开发版.lnk」→ 刚编译的 DeskBox.exe（失败不影响启动）
    try {
        $desktop = [Environment]::GetFolderPath('Desktop')
        $lnkPath = Join-Path $desktop "DeskBox 开发版.lnk"
        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut($lnkPath)
        $lnk.TargetPath = $exe.FullName
        $lnk.WorkingDirectory = $exe.DirectoryName
        $lnk.IconLocation = "$($exe.FullName),0"
        $lnk.Description = "DeskBox 本地开发版（dev 分支 Debug 构建）"
        $lnk.Save()
        Write-Host "==> 已更新桌面快捷方式：$lnkPath" -ForegroundColor Cyan
    }
    catch {
        Write-Host "!! 创建桌面快捷方式失败（不影响运行）：$($_.Exception.Message)" -ForegroundColor Yellow
    }

    Write-Host "==> 启动 $($exe.FullName)" -ForegroundColor Green
    Start-Process $exe.FullName
}
catch {
    Write-Host ""
    Write-Host "!! 更新失败：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host "   修复后可重新运行：powershell -ExecutionPolicy Bypass -File .\scripts\fork-dev-update.ps1" -ForegroundColor Yellow
    Read-Host "按回车关闭窗口"
    exit 1
}
