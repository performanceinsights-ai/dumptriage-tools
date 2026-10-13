<#
.SYNOPSIS
    Configures Windows Error Reporting (WER) LocalDumps to write full user-mode crash dumps.

.DESCRIPTION
    Sets the machine-wide LocalDumps values under
    HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps
    (see https://learn.microsoft.com/en-us/windows/win32/wer/wer-settings).
    DumpType is always 2 (full dump). Requires an elevated session.

.PARAMETER DumpLocation
    Folder where dumps are written (DumpFolder). Created if it does not exist.

.PARAMETER DumpCount
    Maximum number of dump files kept in the folder (DumpCount); the oldest is
    overwritten once the limit is reached.

.EXAMPLE
    .\Set-WerLocalDumps.ps1 -DumpLocation C:\CrashDumps -DumpCount 10
#>
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$DumpLocation,

    [Parameter(Mandatory)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$DumpCount
)

$ErrorActionPreference = 'Stop'

$key = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps'
$fullDump = 2

if (-not (Test-Path -LiteralPath $DumpLocation)) {
    if ($PSCmdlet.ShouldProcess($DumpLocation, 'Create dump folder')) {
        New-Item -ItemType Directory -Path $DumpLocation -Force | Out-Null
    }
}

if (-not (Test-Path -LiteralPath $key)) {
    if ($PSCmdlet.ShouldProcess($key, 'Create registry key')) {
        New-Item -Path $key -Force | Out-Null
    }
}

if ($PSCmdlet.ShouldProcess($key, "Set DumpFolder='$DumpLocation', DumpCount=$DumpCount, DumpType=$fullDump")) {
    New-ItemProperty -Path $key -Name DumpFolder -Value $DumpLocation -PropertyType ExpandString -Force | Out-Null
    New-ItemProperty -Path $key -Name DumpCount  -Value $DumpCount    -PropertyType DWord        -Force | Out-Null
    New-ItemProperty -Path $key -Name DumpType   -Value $fullDump     -PropertyType DWord        -Force | Out-Null
    Get-ItemProperty -Path $key | Select-Object DumpFolder, DumpCount, DumpType
}
