@echo off
REM ============================================================
REM  MicSwitcher 构建脚本
REM  封装本机 .NET SDK 环境（SDK 装在 D:\TOOLS\dotnet-sdk，
REM  且 C 盘用户目录受限，需要把 HOME/NuGet 重定向到 D 盘）。
REM  用法：build.cmd            → 编译整个解决方案 (Debug)
REM        build.cmd Release    → 编译 Release
REM ============================================================
setlocal

set "PATH=D:\TOOLS\dotnet-sdk;%PATH%"
set "DOTNET_CLI_HOME=D:\TOOLS\dotnet-home"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_NOLOGO=1"

set "TMP=D:\TOOLS\dotnet-home\tmp"
set "TEMP=D:\TOOLS\dotnet-home\tmp"
set "APPDATA=D:\TOOLS\dotnet-home\appdata\Roaming"
set "LOCALAPPDATA=D:\TOOLS\dotnet-home\appdata\Local"
set "NUGET_PACKAGES=D:\TOOLS\dotnet-home\nuget-packages"

REM 确保目录存在
if not exist "D:\TOOLS\dotnet-home\tmp"        mkdir "D:\TOOLS\dotnet-home\tmp"
if not exist "D:\TOOLS\dotnet-home\appdata"   mkdir "D:\TOOLS\dotnet-home\appdata"
if not exist "D:\TOOLS\dotnet-home\nuget-packages" mkdir "D:\TOOLS\dotnet-home\nuget-packages"

set "CFG=Debug"
if /I "%~1"=="Release" set "CFG=Release"
if /I "%~1"=="release" set "CFG=Release"

echo [build] configuration=%CFG%
dotnet build -c %CFG% %2 %3 %4 %5
exit /b %ERRORLEVEL%