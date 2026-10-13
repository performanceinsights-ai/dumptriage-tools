#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the DumpTriage Windows agent service, tray logon task, and
    (optionally) its ProgramData state.
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\DumpTriage",
    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"

$ServiceName = "DumpTriageAgent"
$TaskName = "DumpTriageTray"

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    Write-Host "Stopping and removing service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
}

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($task) {
    Write-Host "Removing tray logon task..."
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

Write-Host "Stopping tray process if running..."
Get-Process -Name "DumpTriageTray" -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $InstallDir) {
    Write-Host "Removing install directory $InstallDir..."
    Remove-Item -Path $InstallDir -Recurse -Force
}

if ($RemoveData) {
    $dataDir = "$env:ProgramData\DumpTriage"
    if (Test-Path $dataDir) {
        Write-Host "Removing config/queue/logs at $dataDir..."
        Remove-Item -Path $dataDir -Recurse -Force
    }
}
else {
    Write-Host "Leaving $env:ProgramData\DumpTriage in place (config, queue, logs). Pass -RemoveData to delete it too."
}

Write-Host "Uninstall complete."
