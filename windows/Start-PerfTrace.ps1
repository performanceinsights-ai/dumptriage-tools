<#
.SYNOPSIS
    Takes one WPR performance trace for DumpTriage, without the agent (website#1999, dumptriage-tools#29).

.DESCRIPTION
    Records with PerfInsights.wprp (CPU samples every 4 ms with stacks, context switches with stacks, ready threads, disk
    I/O, DPC/ISR, hard faults) for -Seconds while you reproduce the slowness, stops, and writes two files to -OutDir:
    the trace as a zip (the .etl first, as the service expects) and its manifest.json. With -Upload it sends both to
    POST /api/v1/traces with the key in $env:DUMPTRIAGE_API_KEY; otherwise it prints the command to do so.

    Nothing on the machine is changed except the WPR sampling interval, which is set to 4 ms while recording and back to
    WPR's 1 ms default afterwards. If another tool holds the kernel logger (Procmon, xperf, an EDR), wpr refuses to
    start and this script stops without touching that session or its interval. The trace holds process names, file paths and call
    stacks of everything that ran; review before sending.

.PARAMETER Seconds
    How long to record, 10 to 600. Reproduce the problem during this time.

.PARAMETER OutDir
    Where the zip and manifest go. Created if missing.

.PARAMETER Upload
    Send the trace to DumpTriage after recording.

.PARAMETER ApiUrl
    The DumpTriage API, default https://api.performanceinsights.ai.

.EXAMPLE
    .\Start-PerfTrace.ps1 -Seconds 60 -OutDir C:\Traces
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateRange(10, 600)]
    [int]$Seconds = 60,

    [ValidateNotNullOrEmpty()]
    [string]$OutDir = (Join-Path $env:PUBLIC 'DumpTriage-Traces'),

    [switch]$Upload,

    [ValidateNotNullOrEmpty()]
    [string]$ApiUrl = 'https://api.performanceinsights.ai'
)

$ErrorActionPreference = 'Stop'
$profilePath = Join-Path $PSScriptRoot 'PerfInsights.wprp'
if (-not (Test-Path -LiteralPath $profilePath)) { throw "PerfInsights.wprp must be next to this script ($profilePath)" }
$wpr = Join-Path $env:SystemRoot 'System32\wpr.exe'
if (-not (Test-Path -LiteralPath $wpr)) { throw "wpr.exe is not on this system (Windows 10, Server 2016 or later ship it)" }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$etl = Join-Path $env:TEMP "dumptriage-$stamp.etl"
$zip = Join-Path $OutDir "trace-$stamp.zip"
$manifestPath = Join-Path $OutDir "manifest-$stamp.json"

$start = (Get-Date).ToUniversalTime()
$output = & $wpr -start "$profilePath!FlightRecorder" -filemode 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "wpr could not start ($LASTEXITCODE): another tool may be tracing (Procmon, xperf, an EDR, another WPR session). $output"
}
# The interval is system-wide: changed only once the kernel logger is ours, and it applies to the running session.
& $wpr -setprofint 40000 | Out-Null
try {
    Write-Host "Recording for $Seconds s: reproduce the problem now."
    Start-Sleep -Seconds $Seconds
}
finally {
    $stopOutput = & $wpr -stop $etl 2>&1 | Out-String
    $stopCode = $LASTEXITCODE
    # A stop that failed can leave the session running and holding the kernel logger: end it.
    if ($stopCode -ne 0) { & $wpr -cancel 2>&1 | Out-Null }
    & $wpr -setprofint 10000 | Out-Null
}
$stop = (Get-Date).ToUniversalTime()
if ($stopCode -ne 0 -or -not (Test-Path -LiteralPath $etl)) { throw "wpr -stop failed ($stopCode): $stopOutput" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $etl, 'trace.etl', 'Optimal')
    if (Test-Path -LiteralPath "$etl.NGENPDB") {
        foreach ($f in Get-ChildItem -LiteralPath "$etl.NGENPDB" -File) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, "trace.etl.NGENPDB/$($f.Name)", 'Optimal')
        }
    }
}
finally {
    $archive.Dispose()
    Remove-Item -LiteralPath $etl -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "$etl.NGENPDB" -Recurse -ErrorAction SilentlyContinue
}

$version = ((& $wpr -help 2>&1 | Out-String) -split "`n" | Select-String -Pattern 'Version (\S+)' | Select-Object -First 1).Matches.Groups[1].Value
$manifest = [ordered]@{
    schemaVersion = 1
    traceType     = 'etl'
    trigger       = [ordered]@{ name = 'manual'; type = 'manual' }
    window        = [ordered]@{ startUtc = $start.ToString('yyyy-MM-ddTHH:mm:ssZ'); stopUtc = $stop.ToString('yyyy-MM-ddTHH:mm:ssZ') }
    tool          = [ordered]@{ name = 'wpr'; version = $(if ($version) { $version } else { 'unknown' }); command = "wpr -start PerfInsights.wprp!FlightRecorder -filemode (4 ms sampling); wpr -stop" }
    os            = [ordered]@{ family = 'windows'; build = [string][Environment]::OSVersion.Version.Build }
    hostToken     = ([guid]::NewGuid().ToString('N').Substring(0, 16))
    agentVersion  = 'Start-PerfTrace.ps1/1'
}
$manifestJson = $manifest | ConvertTo-Json -Compress -Depth 4
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, (New-Object System.Text.UTF8Encoding $false))
$size = (Get-Item -LiteralPath $zip).Length
Write-Host "Wrote $zip ($size bytes) and $manifestPath"

if ($Upload) {
    if (-not $env:DUMPTRIAGE_API_KEY) { throw 'Set $env:DUMPTRIAGE_API_KEY to an upload key first' }
    Add-Type -AssemblyName System.Net.Http
    $client = New-Object System.Net.Http.HttpClient
    $client.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $env:DUMPTRIAGE_API_KEY)
    $form = New-Object System.Net.Http.MultipartFormDataContent
    $form.Add((New-Object System.Net.Http.StringContent($manifestJson)), 'manifest')
    $file = New-Object System.Net.Http.StreamContent([System.IO.File]::OpenRead($zip))
    $file.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue('application/zip')
    $form.Add($file, 'file', [System.IO.Path]::GetFileName($zip))
    $response = $client.PostAsync("$($ApiUrl.TrimEnd('/'))/api/v1/traces", $form).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) { throw "upload refused ($([int]$response.StatusCode)): $body" }
    Write-Host "Uploaded: $body"
}
else {
    Write-Host "To send it: set `$env:DUMPTRIAGE_API_KEY and run this script again with -Upload, or:"
    Write-Host "  curl.exe -H `"Authorization: Bearer %DUMPTRIAGE_API_KEY%`" -F manifest=<$manifestPath -F file=@$zip $ApiUrl/api/v1/traces"
}
