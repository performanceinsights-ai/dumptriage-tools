<#
  Checks on installer/install.ps1 that run anywhere pwsh does — no Windows,
  no service, no actual DumpTriageAgent.exe.

  The thing this guards against (website#261): the API key used to be a plaintext
  -ApiKey parameter forwarded straight onto DumpTriageAgent.exe's command
  line (`& $AgentExe --set-api-key $ApiKey`), which put it in plaintext
  process listings and PowerShell history. Both regressions are easy to
  reintroduce by accident — a future edit could add the key back as a
  positional argument, or bring the -ApiKey parameter back — so they're
  checked directly against the file rather than trusted to code review.

  Read-ApiKeyForProvisioning is lifted out of the script by AST rather than
  dot-sourcing, because dot-sourcing would run the installer itself (it
  requires -RunAsAdministrator and touches services).

  Run: pwsh -File windows/dumptriage-agent/installer/tests/Test-Install.ps1
#>
$ErrorActionPreference = "Stop"

$script = Join-Path $PSScriptRoot "../install.ps1"
if (-not (Test-Path $script)) { throw "not found: $script" }

$errors = $null
$tokens = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $script), [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Host "  $_" }
    throw "install.ps1 has $($errors.Count) parse error(s)"
}
Write-Host "ok: install.ps1 parses"

$failures = @()
function Assert-Equal($actual, $expected, $what) {
    if ($actual -ne $expected) {
        $script:failures += "$what`n     expected: $expected`n     actual:   $actual"
    } else {
        Write-Host "ok: $what"
    }
}

# The plaintext -ApiKey parameter must be gone, and -ApiKeyFile must exist,
# so a deployment can't slip the key back in as a command-line argument to
# this script either.
$paramNames = $ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath }
Assert-Equal ($paramNames -contains "ApiKeyFile") $true "install.ps1 takes -ApiKeyFile"
# -ApiKey stays only as a hidden parameter, so an old call is refused with a clear message.
$apiKeyParam = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq "ApiKey" }
$hidden = $apiKeyParam -and ($apiKeyParam.Attributes.Extent.Text -match "DontShow")
Assert-Equal ((-not $apiKeyParam) -or $hidden) $true "install.ps1 takes no plaintext -ApiKey (at most a hidden one that refuses it)"
$refuses = $ast.Extent.Text -match 'if \(\$ApiKey -or \$keyInPath\) \{\s*throw'
Assert-Equal $refuses $true "a call that passes -ApiKey (or a dts_ key positionally) is refused"

# No call may pass --set-api-key a command-line argument: it must always be
# the last element, with the key piped into stdin instead.
$offenders = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) |
    ForEach-Object {
        $elements = $_.CommandElements
        for ($i = 0; $i -lt $elements.Count; $i++) {
            $el = $elements[$i]
            if ($el -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $el.Value -like "--set-api-key*" -and $i -lt $elements.Count - 1) {
                $_.Extent.Text
            }
        }
    }
Assert-Equal (@($offenders).Count) 0 "--set-api-key is never invoked with a command-line argument"
if (@($offenders).Count -gt 0) { $offenders | ForEach-Object { Write-Host "  offending call: $_" } }

# Read-ApiKeyForProvisioning: the -ApiKeyFile path.
$fn = $ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq "Read-ApiKeyForProvisioning"
    }, $true) | Select-Object -First 1
if (-not $fn) { throw "Read-ApiKeyForProvisioning not found in $script" }
Invoke-Expression $fn.Extent.Text

$keyFile = Join-Path ([System.IO.Path]::GetTempPath()) "install-test-key-$([guid]::NewGuid()).txt"
try {
    Set-Content -Path $keyFile -Value " dts_from_file `n" -NoNewline
    Assert-Equal (Read-ApiKeyForProvisioning -ApiKeyFile $keyFile) "dts_from_file" "the key is read from -ApiKeyFile and trimmed"
}
finally {
    Remove-Item -Path $keyFile -ErrorAction SilentlyContinue
}

Assert-Equal (Read-ApiKeyForProvisioning -PipedKey "dts_piped") "dts_piped" "a key piped into the script is used"
Assert-Equal (Read-ApiKeyForProvisioning) $null "with no file, no piped key and no prompt, the existing key is left alone"
Assert-Equal ($ast.Extent.Text -match '\[Console\]::In\.') $false "the console's stdin is never read (a redirected, open stdin would hang a reinstall)"

$missingFile = Join-Path ([System.IO.Path]::GetTempPath()) "install-test-missing-$([guid]::NewGuid()).txt"
$threw = $false
try { Read-ApiKeyForProvisioning -ApiKeyFile $missingFile | Out-Null } catch { $threw = $true }
Assert-Equal $threw $true "a missing -ApiKeyFile is refused, not a silently unprovisioned agent"

$emptyFile = Join-Path ([System.IO.Path]::GetTempPath()) "install-test-empty-$([guid]::NewGuid()).txt"
try {
    Set-Content -Path $emptyFile -Value "" -NoNewline
    $threw = $false
    try { Read-ApiKeyForProvisioning -ApiKeyFile $emptyFile | Out-Null } catch { $threw = $true }
    Assert-Equal $threw $true "an empty -ApiKeyFile is refused"
}
finally {
    Remove-Item -Path $emptyFile -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host ""
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    exit 1
}
Write-Host "`nall install.ps1 checks passed"
