#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the DumpTriage Windows agent: the watcher/upload service plus a
    per-user tray helper that shows approval prompts.

.DESCRIPTION
    Expects both projects already published (see README.md - `dotnet publish
    -c Release -r win-x64 --self-contained` for each). Copies the published
    output into place, provisions the API key via the service exe's
    bootstrap mode (`--set-api-key`, which encrypts it with machine-scope
    DPAPI rather than writing it to config.json in plaintext), registers the
    Windows Service, and installs a logon-trigger scheduled task so the tray
    app starts for whichever user is signed in.

.PARAMETER ApiKey
    The customer's DumpTriage API key (starts with dts_). Required on first
    install; omit on a reinstall to leave the existing key untouched.

.PARAMETER AgentPublishDir
    Path to the published DumpTriageAgent output (contains DumpTriageAgent.exe).

.PARAMETER TrayPublishDir
    Path to the published DumpTriageTray output (contains DumpTriageTray.exe).

.PARAMETER InstallDir
    Where to copy both apps on this machine. Defaults to Program Files.
#>
param(
    [string]$ApiKey,
    [Parameter(Mandatory = $true)][string]$AgentPublishDir,
    [Parameter(Mandatory = $true)][string]$TrayPublishDir,
    [string]$InstallDir = "$env:ProgramFiles\DumpTriage"
)

$ErrorActionPreference = "Stop"

$ServiceName = "DumpTriageAgent"
$AgentInstallDir = Join-Path $InstallDir "Agent"
$TrayInstallDir = Join-Path $InstallDir "Tray"
$AgentExe = Join-Path $AgentInstallDir "DumpTriageAgent.exe"
$TrayExe = Join-Path $TrayInstallDir "DumpTriageTray.exe"
$TaskName = "DumpTriageTray"

Write-Host "Installing DumpTriage agent into $InstallDir"
New-Item -ItemType Directory -Force -Path $AgentInstallDir | Out-Null
New-Item -ItemType Directory -Force -Path $TrayInstallDir | Out-Null

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "Stopping existing service for upgrade..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
}

Copy-Item -Path (Join-Path $AgentPublishDir "*") -Destination $AgentInstallDir -Recurse -Force
Copy-Item -Path (Join-Path $TrayPublishDir "*") -Destination $TrayInstallDir -Recurse -Force

if ($ApiKey) {
    Write-Host "Provisioning API key..."
    # Via stdin, not argv: a command-line argument is visible to every user
    # on the machine (process listing) and can end up in audit logs.
    $ApiKey | & $AgentExe --set-api-key-stdin
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to provision API key (exit code $LASTEXITCODE)"
    }
}
elseif (-not $existingService) {
    Write-Warning "No -ApiKey supplied on first install. Run '<key> | $AgentExe --set-api-key-stdin' before starting the service, or the agent will fail to submit dumps."
}

if ($existingService) {
    Write-Host "Service already registered, will restart with upgraded binaries."
}
else {
    Write-Host "Registering Windows Service..."
    New-Service `
        -Name $ServiceName `
        -BinaryPathName "`"$AgentExe`"" `
        -DisplayName "DumpTriage Agent" `
        -Description "Watches for crash dumps and submits them to DumpTriage." `
        -StartupType Automatic | Out-Null
}

Write-Host "Starting service..."
Start-Service -Name $ServiceName

Write-Host "Installing per-user logon task for the tray helper..."
$action = New-ScheduledTaskAction -Execute $TrayExe
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -GroupId "Users" -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "Done. Config: $env:ProgramData\DumpTriage\config.json - edit the policy there and restart the service to apply changes."
Write-Host "The tray helper will start at next logon; to start it now for the current session, run: Start-Process `"$TrayExe`""
