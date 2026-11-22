@echo off
REM ============================================================
REM  MicSwitcher startup smoke test
REM  Start app -> wait 9s -> check tray init in tray-diag.log -> kill
REM  Use after UI refactors to catch "starts then dies immediately".
REM  Keep this file ASCII-only: cmd reads it as ANSI, and UTF-8
REM  Chinese comments get misparsed into bogus commands.
REM ============================================================
setlocal

set "MICSWITCHER_NO_INTEGRITY_WARN=1"
set "OUT=%~dp0MicSwitcher.App\bin\Debug\net8.0-windows"
set "EXE=%OUT%\MicSwitcher.App.exe"
set "LOG=%OUT%\tray-diag.log"

if not exist "%EXE%" (
    echo [FAIL] exe not found - run build.cmd first
    exit /b 1
)

del /q "%LOG%" 2>nul
start "" "%EXE%"

REM delay 9s without depending on timeout.exe (GNU timeout may shadow it)
ping -n 10 127.0.0.1 >nul

if not exist "%LOG%" (
    echo [FAIL] no tray-diag.log - process died before tray init
    goto fail
)

REM ASCII fragments only: the log is UTF-8 while findstr matches ANSI
findstr /c:"SessionId=" "%LOG%" >nul
if errorlevel 1 (
    echo [FAIL] tray init did not reach environment probe
    goto fail
)

findstr /c:"ForceCreate" "%LOG%" >nul
if errorlevel 1 (
    echo [FAIL] tray icon creation step did not run
    goto fail
)

echo [PASS] startup smoke ok
taskkill /F /IM MicSwitcher.App.exe >nul 2>&1
exit /b 0

:fail
taskkill /F /IM MicSwitcher.App.exe >nul 2>&1
exit /b 1
