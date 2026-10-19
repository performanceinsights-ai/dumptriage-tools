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

    A deployment tool can also pipe the key into this script's stdin
    (Get-Content key.txt | .\install.ps1 ...).

    The key is never taken as a plain command-line argument: that would land
    in PowerShell history, process-creation audit logs and transcripts.

.PARAMETER AgentPublishDir
    Path to the published DumpTriageAgent output (contains DumpTriageAgent.exe).

.PARAMETER TrayPublishDir
    Path to the published DumpTriageTray output (contains DumpTriageTray.exe).

.PARAMETER InstallDir
    Where to copy both apps on this machine. Defaults to Program Files.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$ApiKeyFile,
    [switch]$PromptForApiKey,
    # Removed: kept only so an old call fails with a clear message.
    [Parameter(DontShow = $true)][string]$ApiKey,
    [Parameter(Position = 0, Mandatory = $true)][string]$AgentPublishDir,
    [Parameter(Position = 1, Mandatory = $true)][string]$TrayPublishDir,
    [Parameter(Position = 2)][string]$InstallDir = "$env:ProgramFiles\DumpTriage"
)

$ErrorActionPreference = "Stop"

# An old positional call (`install.ps1 dts_xxx <agent> <tray>`) binds the key to
# a path parameter; catch it here so no later error message echoes it.
$keyInPath = @($AgentPublishDir, $TrayPublishDir, $InstallDir) | Where-Object { $_ -like 'dts_*' }
if ($ApiKey -or $keyInPath) {
    throw "-ApiKey is no longer accepted: a key on the command line is saved in PowerShell history and process audit logs. Revoke that key in the portal, then use -ApiKeyFile <path> or -PromptForApiKey. Clear it from history with: Remove-Item (Get-PSReadLineOption).HistorySavePath"
}
if ($ApiKeyFile -and $PromptForApiKey) {
    throw "Use either -ApiKeyFile or -PromptForApiKey, not both."
}
foreach ($dir in @($AgentPublishDir, $TrayPublishDir)) {
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        throw "Publish directory not found: $dir"
    }
}

$ServiceName = "DumpTriageAgent"
$AgentInstallDir = Join-Path $InstallDir "Agent"
$TrayInstallDir = Join-Path $InstallDir "Tray"
$AgentExe = Join-Path $AgentInstallDir "DumpTriageAgent.exe"
$TrayExe = Join-Path $TrayInstallDir "DumpTriageTray.exe"
$TaskName = "DumpTriageTray"

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

# The data folder holds the DPAPI-protected key, so it and everything in it must be owned by
# SYSTEM, Administrators or a member of Administrators, with no junctions (#3). The agent enforces
# this at every start (and then locks the ACL down); checked here too, so a refusal comes before
# anything on the machine changes.
$DataDir = Join-Path $env:ProgramData 'DumpTriage'
if (Test-Path -LiteralPath $DataDir) {
    $trusted = @('S-1-5-18', 'S-1-5-32-544') + @(Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction SilentlyContinue | ForEach-Object { $_.SID.Value })
    # By hand rather than -Recurse: a junction is refused before anything below it is listed.
    function Test-DataItem($item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "$($item.FullName) is a junction or symlink. Delete $DataDir and run the installer again."
        }
        $owner = (Get-Acl -LiteralPath $item.FullName).GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin $trusted) {
            throw "$($item.FullName) is owned by $owner, not SYSTEM or an administrator, so another account could have created or changed it. Delete $DataDir and run the installer again."
        }
        if ($item.PSIsContainer) {
            Get-ChildItem -LiteralPath $item.FullName -Force -ErrorAction Stop | ForEach-Object { Test-DataItem $_ }
        }
    }
    Test-DataItem (Get-Item -LiteralPath $DataDir -Force)
}

# Read the key before touching the installed service, so a bad key file or an
# abandoned prompt leaves the current install running as it was.
# A first install prompts unless the session can't answer (-NonInteractive, or no console).
$canPrompt = [Environment]::UserInteractive -and
    -not ([Environment]::GetCommandLineArgs() | Where-Object { $_ -like '-NonI*' })
# Never a command-line argument: from a file the caller protects, from this
# script's own stdin (a deployment tool piping it in), or a masked prompt.
function Read-ApiKeyForProvisioning {
    param([string]$ApiKeyFile, [string]$PipedKey, [switch]$Prompt, [switch]$PromptRequired)
    if ($ApiKeyFile) {
        if (-not (Test-Path -LiteralPath $ApiKeyFile -PathType Leaf)) {
            throw "API key file not found: $ApiKeyFile"
        }
        $key = "$(Get-Content -LiteralPath $ApiKeyFile -Raw)".Trim()
        if (-not $key) {
            throw "API key file is empty: $ApiKeyFile"
        }
        return $key
    }
    if ($PipedKey) {
        return $PipedKey
    }
    if ($Prompt) {
        $secure = Read-Host -Prompt "DumpTriage API key (dts_...)" -AsSecureString
        $key = [System.Net.NetworkCredential]::new("", $secure).Password.Trim()
        if (-not $key -and $PromptRequired) {
            throw "No API key entered."
        }
        return $key
    }
    return $null
}

# PowerShell pipeline input (Get-Content key.txt | .\install.ps1 ...), never the
# console's stdin: a deployment tool that redirects stdin and leaves it open
# would block a reinstall that asked for no key at all.
$pipedKey = "$(@($input) | Select-Object -First 1)".Trim()
$plaintextKey = Read-ApiKeyForProvisioning -ApiKeyFile $ApiKeyFile -PipedKey $pipedKey `
    -Prompt:($PromptForApiKey -or (-not $existingService -and $canPrompt)) -PromptRequired:$PromptForApiKey

Write-Host "Installing DumpTriage agent into $InstallDir"
New-Item -ItemType Directory -Force -Path $AgentInstallDir | Out-Null
New-Item -ItemType Directory -Force -Path $TrayInstallDir | Out-Null

if ($existingService) {
    Write-Host "Stopping existing service for upgrade..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
}

Copy-Item -Path (Join-Path $AgentPublishDir "*") -Destination $AgentInstallDir -Recurse -Force
Copy-Item -Path (Join-Path $TrayPublishDir "*") -Destination $TrayInstallDir -Recurse -Force

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
