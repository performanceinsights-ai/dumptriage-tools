<#
  Runs the NinjaOne recipe's Pester tests (website#1744), installing Pester 5 for
  the current user first if it isn't there. Exits non-zero on a failure.
  Used by CI; runs the same way locally:

    pwsh -NoProfile -File rmm/ninjaone/tests/Run-Tests.ps1
#>
$ErrorActionPreference = 'Stop'

$minimum = [version]'5.5.0'
if (-not (Get-Module -ListAvailable -Name Pester | Where-Object { $_.Version -ge $minimum })) {
    Install-Module -Name Pester -RequiredVersion 5.6.1 -Scope CurrentUser -Force -SkipPublisherCheck -Repository PSGallery
}
Import-Module -Name Pester -MinimumVersion $minimum

$config = New-PesterConfiguration
$config.Run.Path = $PSScriptRoot
$config.Run.Exit = $true
$config.Output.Verbosity = 'Detailed'
Invoke-Pester -Configuration $config
