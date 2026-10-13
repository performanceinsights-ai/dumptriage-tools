<#
.SYNOPSIS
    Configures Windows Error Reporting (WER) Live Kernel Reports.

.DESCRIPTION
    Sets the FullLiveKernelReports and LiveKernelReports values under
    HKLM\SYSTEM\CurrentControlSet\Control\CrashControl
    (see https://learn.microsoft.com/en-us/windows/win32/wer/wer-settings).
    This location applies to Windows 10 1703, Windows Server 2019 and later.
    Only the parameters you supply are written; at least one is required.
    Requires an elevated session.

.PARAMETER FullLiveReportsMax
    Maximum number of full live dumps on disk at any time (default 1).
    0 disables the live dump feature on Windows 10 1803 / Server 2019 and later.

.PARAMETER SystemThrottleThreshold
    Hours between full live dumps from any component on the system (default 120).

.PARAMETER ComponentThrottleThreshold
    Hours between full live dumps from any single component (default 168).
    Must be >= SystemThrottleThreshold. Setting both to 0 disables time-based throttling.

.PARAMETER LiveKernelReportsPath
    Folder where live kernel reports are stored (default %SystemRoot%\LiveKernelReports).
    A normal local path such as C:\LiveDumps is converted to the NT path format
    (\??\C:\LiveDumps) that the registry value requires, and the folder is created
    if it does not exist.

.EXAMPLE
    .\Set-WerLiveKernelReports.ps1 -FullLiveReportsMax 3 -SystemThrottleThreshold 0 -ComponentThrottleThreshold 0 -LiveKernelReportsPath D:\LiveDumps
#>
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateRange(0, [int]::MaxValue)]
    [int]$FullLiveReportsMax,

    [ValidateRange(0, [int]::MaxValue)]
    [int]$SystemThrottleThreshold,

    [ValidateRange(0, [int]::MaxValue)]
    [int]$ComponentThrottleThreshold,

    [ValidateNotNullOrEmpty()]
    [string]$LiveKernelReportsPath
)

$ErrorActionPreference = 'Stop'

$settings = 'FullLiveReportsMax', 'SystemThrottleThreshold', 'ComponentThrottleThreshold', 'LiveKernelReportsPath'
if (-not ($settings | Where-Object { $PSBoundParameters.ContainsKey($_) })) {
    throw 'Specify at least one of -FullLiveReportsMax, -SystemThrottleThreshold, -ComponentThrottleThreshold, -LiveKernelReportsPath.'
}

$crashControl = 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl'
$fullKey = Join-Path $crashControl 'FullLiveKernelReports'
$pathKey = Join-Path $crashControl 'LiveKernelReports'

function Get-CurrentDword($Key, $Name, $Default) {
    $value = (Get-ItemProperty -Path $Key -Name $Name -ErrorAction SilentlyContinue).$Name
    if ($null -eq $value) { $Default } else { $value }
}

function Set-RegValue($Key, $Name, $Value, $Type) {
    if ($PSCmdlet.ShouldProcess("$Key\$Name", "Set to '$Value'")) {
        if (-not (Test-Path -LiteralPath $Key)) { New-Item -Path $Key -Force | Out-Null }
        New-ItemProperty -Path $Key -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
    }
}

# ComponentThrottleThreshold must be >= SystemThrottleThreshold; check the effective values.
if ($PSBoundParameters.ContainsKey('SystemThrottleThreshold') -or $PSBoundParameters.ContainsKey('ComponentThrottleThreshold')) {
    $system = if ($PSBoundParameters.ContainsKey('SystemThrottleThreshold')) { $SystemThrottleThreshold } else { Get-CurrentDword $fullKey SystemThrottleThreshold 120 }
    $component = if ($PSBoundParameters.ContainsKey('ComponentThrottleThreshold')) { $ComponentThrottleThreshold } else { Get-CurrentDword $fullKey ComponentThrottleThreshold 168 }
    if ($component -lt $system) {
        throw "ComponentThrottleThreshold ($component) must be >= SystemThrottleThreshold ($system)."
    }
}

if ($PSBoundParameters.ContainsKey('LiveKernelReportsPath')) {
    if ($LiveKernelReportsPath -like '\??\*') {
        $ntPath = $LiveKernelReportsPath
        $folder = $LiveKernelReportsPath.Substring(4)
    } elseif ([System.IO.Path]::IsPathRooted($LiveKernelReportsPath) -and $LiveKernelReportsPath -notlike '\\*') {
        $folder = $LiveKernelReportsPath
        $ntPath = "\??\$LiveKernelReportsPath"
    } else {
        throw "LiveKernelReportsPath must be a local absolute path (e.g. C:\LiveDumps) or an NT path (\??\C:\LiveDumps): '$LiveKernelReportsPath'"
    }
    if (-not (Test-Path -LiteralPath $folder)) {
        if ($PSCmdlet.ShouldProcess($folder, 'Create live kernel reports folder')) {
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
        }
    }
    Set-RegValue $pathKey LiveKernelReportsPath $ntPath String
}

foreach ($name in 'FullLiveReportsMax', 'SystemThrottleThreshold', 'ComponentThrottleThreshold') {
    if ($PSBoundParameters.ContainsKey($name)) {
        Set-RegValue $fullKey $name $PSBoundParameters[$name] DWord
    }
}

foreach ($key in $fullKey, $pathKey) {
    if (Test-Path -LiteralPath $key) { Get-ItemProperty -Path $key | Select-Object * -ExcludeProperty PS* }
}
