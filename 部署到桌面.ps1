# MicSwitcher 一键部署到桌面
# 原因：exe 若从带 Low 完整性标签的目录启动，Windows 会阻止其注册托盘图标。
# 本脚本把程序复制到桌面（普通目录）并确保完整性级别为 Medium。

$ErrorActionPreference = 'Continue'
$src = Join-Path $PSScriptRoot 'MicSwitcher.App\bin\Debug\net8.0-windows'
$dst = Join-Path $env:USERPROFILE 'Desktop\MicSwitcher'

Write-Host '== MicSwitcher 部署 ==' -ForegroundColor Cyan
Write-Host ('源: ' + $src)

if (-not (Test-Path (Join-Path $src 'MicSwitcher.App.exe'))) {
    Write-Host '源目录里没有 MicSwitcher.App.exe，请先运行 build.cmd' -ForegroundColor Red
    Read-Host '按回车退出'
    exit 1
}

Write-Host ''
Write-Host '[1/3] 停止已在运行的实例...' -ForegroundColor Yellow
Stop-Process -Name MicSwitcher.App -Force -ErrorAction SilentlyContinue

Write-Host '[2/3] 复制到桌面...' -ForegroundColor Yellow
robocopy $src $dst /E /NFL /NDL /NJH /NJS | Out-Null

Write-Host '[3/3] 设置完整性级别为 Medium...' -ForegroundColor Yellow
icacls $dst /setintegritylevel Medium /T /C | Out-Null

Write-Host ''
Write-Host '启动程序...' -ForegroundColor Green
Start-Process (Join-Path $dst 'MicSwitcher.App.exe')

Write-Host '完成！请查看右下角托盘（含 ^ 展开区）。' -ForegroundColor Green
Start-Sleep -Seconds 3
