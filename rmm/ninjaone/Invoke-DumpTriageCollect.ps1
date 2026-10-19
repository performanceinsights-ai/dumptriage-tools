<#
.SYNOPSIS
  NinjaOne Script Library recipe (website#1744): send this device's new Windows crash
  dumps to DumpTriage and put the report link in a NinjaOne custom field.

.DESCRIPTION
  Run on a schedule as SYSTEM from the NinjaOne Script Library. Each run:

    1. lists %WINDIR%\Minidump\*.dmp (and %WINDIR%\MEMORY.DMP only with
       -IncludeMemoryDmp: a full kernel dump is often gigabytes);
    2. skips any dump already handled, by path + size + last-write time, from
       the state file %ProgramData%\DumpTriage\ninja-state.json;
    3. uploads the rest to POST {ApiBaseUrl}/api/v1/dumps (multipart `file`
       and `hostname`, Bearer auth with an upload-only key);
    4. writes the newest report link into the `dumptriageLastReport` custom
       field (Ninja-Property-Set, when it exists) and prints every link.

  The API key comes from a NinjaOne custom field (Ninja-Property-Get, secure
  field `dumptriageApiKey` by default) or the DUMPTRIAGE_API_KEY environment
  variable. There is deliberately no parameter that takes the key itself: a
  key on a command line lands in process listings and logs.

  NinjaOne script variables arrive as environment variables, so each switch
  can also be set that way: DUMPTRIAGE_API_BASE_URL, includeMemoryDmp=true,
  dryRun=true.

  Exit codes (NinjaOne can alert on a non-zero result):
    0  nothing new, or every new dump was uploaded
    1  could not run: no API key, the key was refused (401/403), bad URL
    2  an upload failed in a way worth retrying (network, 429 quota, 5xx);
       that dump is retried on the next run
    3  the service rejected a dump for good (e.g. not a dump, too large);
       it is recorded and not sent again

  Windows PowerShell 5.1 compatible: no PowerShell 7 operators, no -Form.

.EXAMPLE
  .\Invoke-DumpTriageCollect.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [string]$ApiBaseUrl = 'https://api.performanceinsights.ai',
    # The NAME of the custom field that holds the key, never the key.
    [string]$ApiKeyFieldName = 'dumptriageApiKey',
    [string]$ReportFieldName = 'dumptriageLastReport',
    [switch]$IncludeMemoryDmp,
    [switch]$DryRun,
    [ValidateRange(1, 1000)]
    [int]$MaxUploads = 10,
    # A dump written less than this long ago may still be being written.
    [ValidateRange(0, 86400)]
    [int]$MinAgeSeconds = 120,
    [switch]$NoHostname,
    # Plain strings, not Join-Path: Join-Path refuses the empty %WINDIR% the
    # tests see when they dot-source this on Linux.
    [string]$MinidumpDir = "$env:WINDIR\Minidump",
    [string]$MemoryDmpPath = "$env:WINDIR\MEMORY.DMP",
    [string]$StatePath = "$env:ProgramData\DumpTriage\ninja-state.json"
)

function Test-EnvFlag {
    param([string]$Name)
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ($null -eq $value) { return $false }
    return (@('1', 'true', 'yes', 'on') -contains $value.Trim().ToLowerInvariant())
}

function Test-ApiBaseUrl {
    # https only; plain http just for a local test backend.
    param([string]$Url)
    $uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri)) { return $false }
    if ($uri.Scheme -eq 'https') { return $true }
    return (($uri.Scheme -eq 'http') -and $uri.IsLoopback)
}

function Get-DumpKey {
    param([string]$Path, [long]$Size, [datetime]$LastWriteUtc)
    return ('{0}|{1}|{2}' -f $Path.ToLowerInvariant(), $Size, $LastWriteUtc.ToUniversalTime().Ticks)
}

function Get-DumpCandidate {
    <# The dumps on disk now, oldest first, minus any still being written. #>
    param(
        [string]$MinidumpDir,
        [string]$MemoryDmpPath,
        [switch]$IncludeMemoryDmp,
        [int]$MinAgeSeconds = 120,
        [datetime]$NowUtc = [datetime]::UtcNow
    )
    $files = @()
    if ($MinidumpDir -and (Test-Path -LiteralPath $MinidumpDir -PathType Container)) {
        $files += @(Get-ChildItem -LiteralPath $MinidumpDir -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Extension -eq '.dmp' })
    }
    if ($IncludeMemoryDmp -and $MemoryDmpPath -and (Test-Path -LiteralPath $MemoryDmpPath -PathType Leaf)) {
        $files += @(Get-Item -LiteralPath $MemoryDmpPath)
    }
    $cutoff = $NowUtc.ToUniversalTime().AddSeconds(-$MinAgeSeconds)
    $candidates = @()
    foreach ($file in ($files | Sort-Object LastWriteTimeUtc)) {
        if ($file.LastWriteTimeUtc -gt $cutoff) {
            Write-Host ("Skipping {0}: written in the last {1}s, may still be in progress" -f $file.FullName, $MinAgeSeconds)
            continue
        }
        $candidates += [pscustomobject]@{
            Path         = $file.FullName
            Size         = [long]$file.Length
            LastWriteUtc = $file.LastWriteTimeUtc
            Key          = Get-DumpKey -Path $file.FullName -Size $file.Length -LastWriteUtc $file.LastWriteTimeUtc
        }
    }
    # Emitted one by one: a caller wraps the call in @() (an empty result is no items, not one empty array).
    return $candidates
}

function Read-CollectState {
    <# Hashtable of key -> entry. A missing or unreadable file is an empty
       state: the worst case is a re-upload, which the service dedups. #>
    param([string]$Path)
    $state = @{}
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $state }
    try {
        $doc = [System.IO.File]::ReadAllText($Path) | ConvertFrom-Json
        foreach ($entry in @($doc.entries)) {
            if ($null -ne $entry -and $entry.key) { $state[[string]$entry.key] = $entry }
        }
    } catch {
        Write-Warning ("State file {0} is unreadable ({1}); starting fresh" -f $Path, $_.Exception.Message)
        try { Move-Item -LiteralPath $Path -Destination ($Path + '.corrupt') -Force } catch { }
        return @{}
    }
    return $state
}

function Write-CollectState {
    <# Keeps entries whose file still exists, so the file doesn't grow forever. #>
    param([string]$Path, [hashtable]$State)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $kept = @($State.Values | Where-Object { Test-Path -LiteralPath $_.path } | Sort-Object key)
    $json = ConvertTo-Json -InputObject ([pscustomobject]@{ version = 1; entries = $kept }) -Depth 5
    $tmp = $Path + '.tmp'
    [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}

function Get-DumpTriageApiKey {
    <# NinjaOne custom field first, then DUMPTRIAGE_API_KEY. #>
    param([string]$FieldName)
    if ($FieldName -and (Get-Command -Name 'Ninja-Property-Get' -ErrorAction SilentlyContinue)) {
        try {
            $value = Ninja-Property-Get $FieldName
            if ($value -and ([string]$value).Trim()) { return ([string]$value).Trim() }
        } catch {
            Write-Warning ("Could not read custom field {0}: {1}" -f $FieldName, $_.Exception.Message)
        }
    }
    $fromEnv = [Environment]::GetEnvironmentVariable('DUMPTRIAGE_API_KEY')
    if ($fromEnv -and $fromEnv.Trim()) { return $fromEnv.Trim() }
    return $null
}

function Set-DumpTriageReportField {
    <# $true when the link was written to the custom field. #>
    param([string]$FieldName, [string]$Value)
    if (-not $FieldName -or -not (Get-Command -Name 'Ninja-Property-Set' -ErrorAction SilentlyContinue)) { return $false }
    try {
        Ninja-Property-Set $FieldName $Value | Out-Null
        return $true
    } catch {
        Write-Warning ("Could not write custom field {0}: {1}" -f $FieldName, $_.Exception.Message)
        return $false
    }
}

function Send-DumpTriageUpload {
    <# One streamed multipart POST. Never throws: returns StatusCode 0 and
       Error on a network failure. HttpClient rather than Invoke-RestMethod
       because Windows PowerShell 5.1 has no -Form and would buffer the file. #>
    param(
        [string]$Uri,
        [string]$ApiKey,
        [string]$Path,
        [string]$HostName,
        [int]$TimeoutMinutes = 120
    )
    Add-Type -AssemblyName System.Net.Http
    try {
        # Windows PowerShell 5.1 on older .NET defaults to TLS 1.0.
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    } catch { }
    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromMinutes($TimeoutMinutes)
    # .NET Framework sends 'Expect: 100-continue' on every POST by default,
    # which the API refuses with 417 on an upload (website#1801).
    $client.DefaultRequestHeaders.ExpectContinue = $false
    $stream = $null
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $content = New-Object System.Net.Http.MultipartFormDataContent
        $fileContent = [System.Net.Http.StreamContent]::new($stream, 1MB)
        $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/octet-stream')
        $content.Add($fileContent, 'file', [System.IO.Path]::GetFileName($Path))
        if ($HostName) { $content.Add([System.Net.Http.StringContent]::new($HostName), 'hostname') }
        $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, $Uri)
        $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $ApiKey)
        $request.Content = $content
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Body = $body; Error = $null }
    } catch {
        return [pscustomobject]@{ StatusCode = 0; Body = $null; Error = $_.Exception.GetBaseException().Message }
    } finally {
        if ($null -ne $stream) { $stream.Dispose() }
        $client.Dispose()
    }
}

function Resolve-UploadOutcome {
    <# Success | AuthFailed | Rejected | Retry, from an upload result. #>
    param($Result)
    $code = [int]$Result.StatusCode
    if ($code -eq 200 -or $code -eq 201) { return 'Success' }
    if ($code -eq 401 -or $code -eq 403) { return 'AuthFailed' }
    if (@(400, 413, 415, 422) -contains $code) { return 'Rejected' }
    return 'Retry'
}

function Get-ResponseField {
    param([string]$Body, [string]$Name)
    if (-not $Body) { return $null }
    try {
        $doc = $Body | ConvertFrom-Json
        $prop = $doc.PSObject.Properties[$Name]
        if ($null -ne $prop) { return $prop.Value }
    } catch { }
    return $null
}

function Invoke-DumpTriageCollect {
    <# The whole run; returns the exit code. #>
    param(
        [string]$ApiBaseUrl,
        [string]$ApiKeyFieldName = 'dumptriageApiKey',
        [string]$ReportFieldName = 'dumptriageLastReport',
        [switch]$IncludeMemoryDmp,
        [switch]$DryRun,
        [int]$MaxUploads = 10,
        [int]$MinAgeSeconds = 120,
        [switch]$NoHostname,
        [string]$MinidumpDir,
        [string]$MemoryDmpPath,
        [string]$StatePath,
        [datetime]$NowUtc = [datetime]::UtcNow
    )
    if (-not (Test-ApiBaseUrl -Url $ApiBaseUrl)) {
        Write-Host ("ERROR: ApiBaseUrl must be an https URL, got '{0}'" -f $ApiBaseUrl)
        return 1
    }
    $uploadUri = $ApiBaseUrl.TrimEnd('/') + '/api/v1/dumps'

    $candidates = @(Get-DumpCandidate -MinidumpDir $MinidumpDir -MemoryDmpPath $MemoryDmpPath `
        -IncludeMemoryDmp:$IncludeMemoryDmp -MinAgeSeconds $MinAgeSeconds -NowUtc $NowUtc)
    $state = Read-CollectState -Path $StatePath
    $new = @($candidates | Where-Object { -not $state.ContainsKey($_.Key) })
    if ($new.Count -eq 0) {
        Write-Host ("No new dumps ({0} on disk already handled)." -f @($candidates).Count)
        return 0
    }
    $batch = @($new | Select-Object -First $MaxUploads)
    if ($new.Count -gt $batch.Count) {
        Write-Host ("{0} new dumps; sending the oldest {1} this run, the rest next run." -f $new.Count, $batch.Count)
    }

    if ($DryRun) {
        foreach ($dump in $batch) { Write-Host ("DRY RUN: would upload {0} ({1} bytes) to {2}" -f $dump.Path, $dump.Size, $uploadUri) }
        return 0
    }

    $apiKey = Get-DumpTriageApiKey -FieldName $ApiKeyFieldName
    if (-not $apiKey) {
        Write-Host ("ERROR: no API key. Set the '{0}' custom field (or DUMPTRIAGE_API_KEY) to an upload-only key." -f $ApiKeyFieldName)
        return 1
    }
    $hostName = $null
    if (-not $NoHostname) { $hostName = [Environment]::MachineName }

    $uploaded = 0
    $rejected = 0
    $retry = 0
    $authFailed = $false
    $lastReport = $null
    foreach ($dump in $batch) {
        $result = Send-DumpTriageUpload -Uri $uploadUri -ApiKey $apiKey -Path $dump.Path -HostName $hostName
        $outcome = Resolve-UploadOutcome -Result $result
        $entry = [pscustomobject]@{
            key        = $dump.Key
            path       = $dump.Path
            size       = $dump.Size
            mtime      = $dump.LastWriteUtc.ToString('o')
            handledAt  = $NowUtc.ToString('o')
            outcome    = $null
            dumpId     = $null
            reportUrl  = $null
            httpStatus = [int]$result.StatusCode
        }
        if ($outcome -eq 'Success') {
            $entry.outcome = 'uploaded'
            $entry.dumpId = Get-ResponseField -Body $result.Body -Name 'id'
            $entry.reportUrl = Get-ResponseField -Body $result.Body -Name 'report_url'
            $state[$dump.Key] = $entry
            $uploaded++
            if ($entry.reportUrl) {
                $lastReport = $entry.reportUrl
                Write-Host ("Uploaded {0}: report {1}" -f $dump.Path, $entry.reportUrl)
            } else {
                Write-Host ("Uploaded {0}: dump id {1}" -f $dump.Path, $entry.dumpId)
            }
        } elseif ($outcome -eq 'Rejected') {
            # Sending the same file again would get the same answer.
            $entry.outcome = 'rejected'
            $state[$dump.Key] = $entry
            $rejected++
            Write-Host ("REJECTED {0} (HTTP {1}): {2}" -f $dump.Path, $result.StatusCode, $result.Body)
        } elseif ($outcome -eq 'AuthFailed') {
            $authFailed = $true
            Write-Host ("ERROR: the API key was refused (HTTP {0}). Check the '{1}' custom field holds a live upload key." -f $result.StatusCode, $ApiKeyFieldName)
            break
        } else {
            $retry++
            $why = $result.Error
            if (-not $why) { $why = $result.Body }
            Write-Host ("FAILED {0} (HTTP {1}): {2}. Will retry next run." -f $dump.Path, $result.StatusCode, $why)
            # A quota, outage or network problem hits the next upload too.
            break
        }
    }

    try {
        Write-CollectState -Path $StatePath -State $state
    } catch {
        Write-Warning ("Could not save state to {0}: {1}. Dumps may be sent again (the service dedups them)." -f $StatePath, $_.Exception.Message)
    }
    if ($lastReport) {
        if (Set-DumpTriageReportField -FieldName $ReportFieldName -Value $lastReport) {
            Write-Host ("Wrote the report link to custom field '{0}'." -f $ReportFieldName)
        }
    }
    Write-Host ("DumpTriage: {0} uploaded, {1} rejected, {2} to retry." -f $uploaded, $rejected, $retry)

    if ($authFailed) { return 1 }
    if ($rejected -gt 0) { return 3 }
    if ($retry -gt 0) { return 2 }
    return 0
}

# Dot-sourced (the Pester tests): define the functions, run nothing.
if ($MyInvocation.InvocationName -eq '.') { return }

if (-not $PSBoundParameters.ContainsKey('ApiBaseUrl') -and $env:DUMPTRIAGE_API_BASE_URL) { $ApiBaseUrl = $env:DUMPTRIAGE_API_BASE_URL }
if (Test-EnvFlag 'includeMemoryDmp') { $IncludeMemoryDmp = [switch]$true }
if (Test-EnvFlag 'dryRun') { $DryRun = [switch]$true }

$code = Invoke-DumpTriageCollect -ApiBaseUrl $ApiBaseUrl -ApiKeyFieldName $ApiKeyFieldName `
    -ReportFieldName $ReportFieldName -IncludeMemoryDmp:$IncludeMemoryDmp -DryRun:$DryRun `
    -MaxUploads $MaxUploads -MinAgeSeconds $MinAgeSeconds -NoHostname:$NoHostname `
    -MinidumpDir $MinidumpDir -MemoryDmpPath $MemoryDmpPath -StatePath $StatePath
exit $code
