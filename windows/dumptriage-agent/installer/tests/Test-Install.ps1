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
$refusesNamedPipe = $ast.Extent.Text -match 'if \(\$PipedApiKey -and -not \$MyInvocation\.ExpectingInput\) \{\s*throw'
Assert-Equal $refusesNamedPipe $true "-PipedApiKey given as a named argument (not piped) is refused"
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

# Install.cmd, the double-click launcher at the zip root (#80): it must start this script with the zip's folders and
# a process-only bypass, never pass a key, keep CRLF (cmd.exe misreads some LF-only batch files) and land at the root.
$launcherPath = Join-Path $PSScriptRoot "../Install.cmd"
$launcher = [System.IO.File]::ReadAllText((Resolve-Path $launcherPath))
$installLine = 'powershell -NoProfile -ExecutionPolicy Bypass -File "%DT_ROOT%installer\install.ps1" -AgentPublishDir "%DT_ROOT%agent" -TrayPublishDir "%DT_ROOT%tray" %DT_KEY% %DT_ARGS%'
Assert-Equal $launcher.Contains($installLine) $true "Install.cmd runs install.ps1 with the zip's agent and tray folders and a process-only bypass"
Assert-Equal (@('AgentPublishDir', 'TrayPublishDir', 'PromptForApiKey') | Where-Object { $paramNames -notcontains $_ }) $null "the parameters Install.cmd passes exist in install.ps1"
Assert-Equal ($launcher -match '(?i)-ApiKey|dts_') $false "Install.cmd passes no API key (install.ps1 prompts for it)"
# The UAC relaunch starts cmd.exe with the path in doubled quotes: the .cmd's own runas verb (cmd /C "%1") loses the
# quotes on a path with ( ) or &, such as a second download's "dumptriage-agent-windows-x64 (1)".
Assert-Equal $launcher.Contains("-FilePath `$env:ComSpec -ArgumentList ('/c ' + `$q + `$q + `$env:DT_SELF + `$q + ' --elevated' + `$q) -Verb RunAs -Wait -PassThru -ErrorAction Stop; exit `$p.ExitCode } catch {") $true "the UAC relaunch runs cmd.exe on the doubled-quoted path and waits for it"
Assert-Equal ($launcher -match '(?i)Set-ExecutionPolicy') $false "Install.cmd changes no execution policy setting"
Assert-Equal ($launcher -match '(?<!\r)\n') $false "Install.cmd has CRLF line endings"
$release = Get-Content -Raw (Join-Path $PSScriptRoot "../../../../.github/workflows/release.yml")
Assert-Equal $release.Contains('Move-Item "$out\installer\Install.cmd" "$out\Install.cmd"') $true "release.yml puts Install.cmd at the zip root"

# -Reference (dumptriage-tools#77): counted as the service counts it, and written into config.json without touching the rest.
foreach ($name in @("Get-ReferenceLength", "Set-ConfigReference")) {
    $f = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true) |
        Select-Object -First 1
    if (-not $f) { throw "$name not found in $script" }
    Invoke-Expression $f.Extent.Text
}
Assert-Equal (Get-ReferenceLength "  client=Acme ") 11 "the reference is trimmed before it is counted"
Assert-Equal (Get-ReferenceLength ([string]::Concat(@([char]::ConvertFromUtf32(0x1F600)) * 200))) 200 "an emoji counts once, as the service counts it"
Assert-Equal ($ast.Extent.Text -match "\(Get-ReferenceLength \`$Reference\) -gt \`$ReferenceMaxLength\) \{\s*throw") $true "a -Reference over 200 characters is refused"

$configDir = Join-Path ([System.IO.Path]::GetTempPath()) "install-test-cfg-$([guid]::NewGuid())"
$configPath = Join-Path $configDir "config.json"
try {
    Set-ConfigReference -ConfigPath $configPath -Reference " client=Acme "
    Assert-Equal ((Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).reference) "client=Acme" "a first install writes the label, trimmed"

    Set-Content -LiteralPath $configPath -NoNewline -Value '{"apiBaseUrl":"https://x.example","protectedApiKey":"AQAA","policy":{"mode":"RuleBased","rules":[{"dumpType":"Kernel","action":"Ask"}]},"tracing":{"triggers":[{"name":"cpu","type":"cpuPercent","threshold":90}]},"reference":"old"}'
    Set-ConfigReference -ConfigPath $configPath -Reference "client=Beta;ticket=INC-1"
    $raw = Get-Content -LiteralPath $configPath -Raw
    $after = $raw | ConvertFrom-Json
    Assert-Equal $after.reference "client=Beta;ticket=INC-1" "a reinstall replaces the label"
    Assert-Equal "$($after.protectedApiKey)|$($after.policy.rules[0].dumpType)|$($after.tracing.triggers[0].threshold)" "AQAA|Kernel|90" "and keeps the key, the rules and the triggers"
    Assert-Equal ([IO.File]::ReadAllBytes($configPath)[0]) ([byte][char]'{') "config.json is written without a byte-order mark"

    Set-ConfigReference -ConfigPath $configPath -Reference ""
    Assert-Equal ((Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).PSObject.Properties.Name -contains "reference") $false "-Reference '' removes it"
}
finally {
    Remove-Item -LiteralPath $configDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host ""
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    exit 1
}
Write-Host "`nall install.ps1 checks passed"
