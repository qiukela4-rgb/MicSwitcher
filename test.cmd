@echo off
REM ============================================================
REM  MicSwitcher 测试脚本
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

dotnet test MicSwitcher.Tests -c Debug --nologo %*
exit /b %ERRORLEVEL%