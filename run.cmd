@echo off
chcp 65001 >nul
REM ============================================================
REM  MicSwitcher 运行脚本：构建后启动（弹设置窗口），cmd 自动退出
REM ============================================================
setlocal

set "PATH=D:\TOOLS\dotnet-sdk;%PATH%"
set "DOTNET_CLI_HOME=D:\TOOLS\dotnet-home"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "TMP=D:\TOOLS\dotnet-home\tmp"
set "TEMP=D:\TOOLS\dotnet-home\tmp"
set "APPDATA=D:\TOOLS\dotnet-home\appdata\Roaming"
set "LOCALAPPDATA=D:\TOOLS\dotnet-home\appdata\Local"
set "NUGET_PACKAGES=D:\TOOLS\dotnet-home\nuget-packages"

if not exist "D:\TOOLS\dotnet-home\tmp" mkdir "D:\TOOLS\dotnet-home\tmp"
if not exist "D:\TOOLS\dotnet-home\appdata" mkdir "D:\TOOLS\dotnet-home\appdata"
if not exist "D:\TOOLS\dotnet-home\nuget-packages" mkdir "D:\TOOLS\dotnet-home\nuget-packages"

echo [构建] 正在编译 MicSwitcher...
dotnet build MicSwitcher.App -c Debug --nologo -v q
if errorlevel 1 (
    echo [错误] 构建失败，按任意键退出...
    pause >nul
    exit /b 1
)

echo [启动] 即将弹出设置窗口，托盘图标在右下角。
start "" "%~dp0MicSwitcher.App\bin\Debug\net8.0-windows\MicSwitcher.App.exe"
exit /b 0
