#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the DumpTriage Windows agent: the watcher/upload service plus a
    per-user tray helper that shows approval prompts.

.DESCRIPTION
    Expects both projects already published (see README.md - `dotnet publish
    -c Release -r win-x64 --self-contained` for each). Copies the published
    output into place, provisions the API key via the service exe's
    bootstrap mode (`--set-api-key-stdin`, which encrypts it with machine-scope
    DPAPI rather than writing it to config.json in plaintext), registers the
    Windows Service, and installs a logon-trigger scheduled task so the tray
    app starts for whichever user is signed in.

.PARAMETER ApiKeyFile
    A file holding the DumpTriage API key (starts with dts_). The file is
    deleted once the key is stored. Use this for unattended installs.

.PARAMETER PromptForApiKey
    Ask for the API key at a masked prompt. This is also what happens on a
    first install in an interactive session when no -ApiKeyFile is given.
    On a reinstall without either option the existing key is left untouched.

    The key is never taken as a plain command-line argument: that would land
    in PowerShell history, process-creation audit logs and transcripts.

.PARAMETER AgentPublishDir
    Path to the published DumpTriageAgent output (contains DumpTriageAgent.exe).

.PARAMETER TrayPublishDir
    Path to the published DumpTriageTray output (contains DumpTriageTray.exe).

.PARAMETER InstallDir
    Where to copy both apps on this machine. Defaults to Program Files.
#>
param(
    [string]$ApiKeyFile,
    [switch]$PromptForApiKey,
    # Removed: kept only to fail with a clear message instead of "parameter not found".
    [Parameter(DontShow = $true)][string]$ApiKey,
    [Parameter(Mandatory = $true)][string]$AgentPublishDir,
    [Parameter(Mandatory = $true)][string]$TrayPublishDir,
    [string]$InstallDir = "$env:ProgramFiles\DumpTriage"
)

$ErrorActionPreference = "Stop"

if ($ApiKey) {
    throw "-ApiKey is no longer accepted: a key on the command line is saved in PowerShell history and process audit logs. Revoke that key in the portal, then use -ApiKeyFile <path> or -PromptForApiKey. Clear it from history with: Remove-Item (Get-PSReadLineOption).HistorySavePath"
}
if ($ApiKeyFile -and $PromptForApiKey) {
    throw "Use either -ApiKeyFile or -PromptForApiKey, not both."
}
if ($ApiKeyFile -and -not (Test-Path -LiteralPath $ApiKeyFile -PathType Leaf)) {
    throw "API key file not found: $ApiKeyFile"
}

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

# A first install prompts unless the session can't answer (-NonInteractive, or no console).
$canPrompt = [Environment]::UserInteractive -and
    -not ([Environment]::GetCommandLineArgs() | Where-Object { $_ -like '-NonI*' })
$plaintextKey = $null
if ($ApiKeyFile) {
    $plaintextKey = (Get-Content -LiteralPath $ApiKeyFile -Raw).Trim()
}
elseif ($PromptForApiKey -or (-not $existingService -and $canPrompt)) {
    $secure = Read-Host -Prompt "DumpTriage API key (dts_...)" -AsSecureString
    $plaintextKey = [System.Net.NetworkCredential]::new("", $secure).Password.Trim()
}

if ($plaintextKey) {
    Write-Host "Provisioning API key..."
    # Via stdin, not argv: a command-line argument is visible to every user
    # on the machine (process listing) and can end up in audit logs.
    $plaintextKey | & $AgentExe --set-api-key-stdin
    $exit = $LASTEXITCODE
    $plaintextKey = $null
    if ($exit -ne 0) {
        throw "Failed to provision API key (exit code $exit)"
    }
    if ($ApiKeyFile) {
        Remove-Item -LiteralPath $ApiKeyFile -Force
        Write-Host "Removed $ApiKeyFile now that the key is stored."
    }
}
elseif (-not $existingService) {
    Write-Warning "No API key provided on first install. Re-run with -PromptForApiKey or -ApiKeyFile <path> before the agent can submit dumps."
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
