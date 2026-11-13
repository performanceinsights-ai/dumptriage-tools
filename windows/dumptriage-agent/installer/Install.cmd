@echo off
rem DumpTriage agent: double-click to install (#80). Runs installer\install.ps1 as administrator with the PowerShell
rem execution policy bypassed for that one process only, so no setting on this machine changes; a Group Policy or
rem Smart App Control still wins over it. Expects the release zip's layout: agent\, tray\ and installer\ beside it.
setlocal
set "DT_ROOT=%~dp0"
set "DT_SELF=%~f0"

rem fltmc needs administrator rights and, unlike `net session`, not the Server service.
fltmc >nul 2>&1
if not errorlevel 1 goto :elevated
if /i "%~1"=="--elevated" (
    echo Still not running as administrator after the elevation prompt. Right-click Install.cmd and choose
    echo Run as administrator.
    pause
    exit /b 1
)
echo Asking Windows for administrator rights...
rem cmd.exe itself with the path in doubled quotes ([char]34), so a folder named "x (1)" or "a & b" survives /c, and
rem this window waits so a run that dies before its own pause (a mapped drive the elevated session can't see) shows.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$q = [char]34; $p = Start-Process -FilePath $env:ComSpec -ArgumentList ('/c ' + $q + $q + $env:DT_SELF + $q + ' --elevated' + $q) -Verb RunAs -Wait -PassThru; exit $p.ExitCode"
if errorlevel 1 (
    echo.
    echo The install did not finish. If the administrator window closed at once and this folder is on a network or
    echo mapped drive, copy the folder to this PC ^(for example to Downloads^) and run Install.cmd from there.
    pause
)
exit /b

:elevated
if not exist "%DT_ROOT%installer\install.ps1" goto :layout
if not exist "%DT_ROOT%agent\DumpTriageAgent.exe" goto :layout
if not exist "%DT_ROOT%tray\DumpTriageTray.exe" goto :layout

rem The zip's files carry the downloaded-from-the-internet mark; clear it on the zip's own folders only, so the
rem installed exes don't meet a SmartScreen prompt when started by hand.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath (Join-Path $env:DT_ROOT 'installer'), (Join-Path $env:DT_ROOT 'agent'), (Join-Path $env:DT_ROOT 'tray') -Recurse -File | Unblock-File"

rem Arguments go on to install.ps1 (an RMM tool already running as administrator can pass its key-file option); a UAC
rem relaunch passes none.
set "DT_ARGS=%*"
if /i "%~1"=="--elevated" set "DT_ARGS="

rem A first install asks for the key (install.ps1's masked prompt); a reinstall keeps it unless asked to replace it.
set "DT_KEY="
if defined DT_ARGS goto :install
sc.exe query DumpTriageAgent >nul 2>&1
if not errorlevel 1 (
    choice /c YN /n /m "The DumpTriage agent is already installed. Enter a new API key? [Y/N] "
    if not errorlevel 2 set "DT_KEY=-PromptForApiKey"
)

:install
powershell -NoProfile -ExecutionPolicy Bypass -File "%DT_ROOT%installer\install.ps1" -AgentPublishDir "%DT_ROOT%agent" -TrayPublishDir "%DT_ROOT%tray" %DT_KEY% %DT_ARGS%
set "DT_EXIT=%errorlevel%"

echo.
if "%DT_EXIT%"=="0" (
    echo Finished. If a warning above says no API key was provided, run Install.cmd again and answer Y to enter it.
) else (
    echo The install failed ^(exit code %DT_EXIT%^). If the message above says running scripts is disabled or
    echo blocked by policy, a Group Policy or Smart App Control on this PC refuses unsigned scripts: install through
    echo your IT department, or see README.md.
)
pause
exit /b %DT_EXIT%

:layout
echo Install.cmd runs from the extracted release zip ^(dumptriage-agent-windows-x64.zip^): installer\, agent\ and
echo tray\ must be next to it. Extract the whole zip ^(right-click, Extract All^), then run Install.cmd from the
echo extracted folder.
pause
exit /b 1
