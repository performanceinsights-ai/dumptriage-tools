@echo off
rem DumpTriage agent: double-click to install (#80). Runs installer\install.ps1 as administrator with the PowerShell
rem execution policy bypassed for that one process only, so no setting on this machine changes; a Group Policy or
rem Smart App Control still wins over it. Expects the release zip's layout: agent\, tray\ and installer\ beside it.
setlocal
set "DT_ROOT=%~dp0"
set "DT_SELF=%~f0"

net session >nul 2>&1
if errorlevel 1 (
    echo Asking Windows for administrator rights...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath $env:DT_SELF -Verb RunAs"
    if errorlevel 1 pause
    exit /b
)

if not exist "%DT_ROOT%installer\install.ps1" (
    echo installer\install.ps1 is not next to Install.cmd. Extract the whole zip first ^(right-click, Extract All^),
    echo then run Install.cmd from the extracted folder.
    pause
    exit /b 1
)

rem The zip's files carry the downloaded-from-the-internet mark; clear it on the zip's own folders only.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath (Join-Path $env:DT_ROOT 'installer'), (Join-Path $env:DT_ROOT 'agent'), (Join-Path $env:DT_ROOT 'tray') -Recurse -File | Unblock-File"

powershell -NoProfile -ExecutionPolicy Bypass -File "%DT_ROOT%installer\install.ps1" -AgentPublishDir "%DT_ROOT%agent" -TrayPublishDir "%DT_ROOT%tray"
set "DT_EXIT=%errorlevel%"

echo.
if "%DT_EXIT%"=="0" (
    echo DumpTriage agent installed.
) else (
    echo The install failed ^(exit code %DT_EXIT%^). If the message above says running scripts is disabled or
    echo blocked by policy, a Group Policy or Smart App Control on this PC refuses unsigned scripts: install through
    echo your IT department, or see README.md.
)
pause
exit /b %DT_EXIT%
