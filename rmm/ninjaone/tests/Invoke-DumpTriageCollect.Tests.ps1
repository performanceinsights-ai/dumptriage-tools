<#
  Pester 5 tests for the NinjaOne recipe (website#1744). Run anywhere pwsh runs:

    pwsh -NoProfile -Command "Invoke-Pester -Path rmm/ninjaone/tests -Output Detailed -CI"

  The script is dot-sourced, which defines its functions and runs nothing.
  The HTTP call (Send-DumpTriageUpload) and the NinjaOne cmdlet wrappers are
  mocked; the cmdlets themselves are faked as global functions where the
  wrappers are under test.
#>

BeforeAll {
    $script:ScriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Invoke-DumpTriageCollect.ps1'
    . $script:ScriptPath

    function New-FakeDump {
        param([string]$Dir, [string]$Name, [int]$Bytes = 64, [datetime]$WrittenUtc = [datetime]'2026-10-01T08:00:00Z')
        New-Item -ItemType Directory -Path $Dir -Force | Out-Null
        $path = Join-Path $Dir $Name
        [System.IO.File]::WriteAllBytes($path, [byte[]](@(0x4D, 0x44, 0x4D, 0x50) + @(0) * ($Bytes - 4)))
        (Get-Item -LiteralPath $path).LastWriteTimeUtc = $WrittenUtc.ToUniversalTime()
        return $path
    }

    function New-UploadResponse {
        param([int]$StatusCode = 201, [string]$Id = ([guid]::NewGuid().ToString()))
        $body = '{"id":"' + $Id + '","status":"uploaded","report_url":"https://portal.example.com/dumps/' + $Id + '"}'
        return [pscustomobject]@{ StatusCode = $StatusCode; Body = $body; Error = $null }
    }
}

Describe 'the script file' {
    BeforeAll {
        $tokens = $null
        $errors = $null
        $script:Ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Resolve-Path $script:ScriptPath).Path, [ref]$tokens, [ref]$errors)
        $script:Tokens = $tokens
        $script:ParseErrors = $errors
    }

    It 'parses' {
        $script:ParseErrors | Should -BeNullOrEmpty
    }

    It 'has no parameter that takes the API key itself' {
        # website#261 again: a key on a command line ends up in process listings.
        $names = $script:Ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath }
        $names | Should -Contain 'ApiKeyFieldName'
        $names | Where-Object { $_ -match 'Key' -and $_ -ne 'ApiKeyFieldName' } | Should -BeNullOrEmpty
        $names | Where-Object { $_ -match 'Token|Secret|Password' } | Should -BeNullOrEmpty
    }

    It 'uses nothing Windows PowerShell 5.1 lacks' {
        # ?? ??= ?. and the ternary are PowerShell 7 only; -Form/-AsHashtable are 6+.
        $script:Tokens | Where-Object { $_.Kind -in 'QuestionQuestion', 'QuestionQuestionEquals', 'QuestionDot', 'QuestionLBracket' } |
            Should -BeNullOrEmpty
        $script:Ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.TernaryExpressionAst] }, $true) |
            Should -BeNullOrEmpty
        $script:Ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandParameterAst] -and $n.ParameterName -in 'Form', 'AsHashtable' }, $true) |
            Should -BeNullOrEmpty
        $script:Ast.ScriptRequirements.RequiredPSVersion | Should -BeNullOrEmpty
    }

    It 'turns off Expect: 100-continue for the upload' {
        # .NET Framework's HttpClient sends it by default and the API answers
        # an upload carrying it with 417, retried forever (website#1801).
        $assignments = $script:Ast.FindAll({ param($n)
            $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $n.Left.Extent.Text -eq '$client.DefaultRequestHeaders.ExpectContinue' }, $true)
        @($assignments).Count | Should -Be 1
        @($assignments)[0].Right.Extent.Text | Should -Be '$false'
    }
}

Describe 'Get-DumpCandidate' {
    BeforeEach {
        $script:Dir = Join-Path $TestDrive ([guid]::NewGuid().ToString())
        $script:Now = [datetime]'2026-10-02T08:00:00Z'
    }

    It 'lists the minidumps oldest first and ignores other files' {
        New-FakeDump -Dir $script:Dir -Name 'b.dmp' -WrittenUtc '2026-10-01T09:00:00Z' | Out-Null
        New-FakeDump -Dir $script:Dir -Name 'a.dmp' -WrittenUtc '2026-10-01T10:00:00Z' | Out-Null
        New-FakeDump -Dir $script:Dir -Name 'notes.txt' | Out-Null

        $found = Get-DumpCandidate -MinidumpDir $script:Dir -NowUtc $script:Now

        @($found).Count | Should -Be 2
        Split-Path -Leaf $found[0].Path | Should -Be 'b.dmp'
        Split-Path -Leaf $found[1].Path | Should -Be 'a.dmp'
        $found[0].Size | Should -Be 64
    }

    It 'skips a dump written too recently to be complete' {
        New-FakeDump -Dir $script:Dir -Name 'fresh.dmp' -WrittenUtc $script:Now.AddSeconds(-30) | Out-Null

        @(Get-DumpCandidate -MinidumpDir $script:Dir -MinAgeSeconds 120 -NowUtc $script:Now).Count | Should -Be 0
    }

    It 'only includes MEMORY.DMP behind the switch' {
        $memory = New-FakeDump -Dir (Join-Path $script:Dir 'win') -Name 'MEMORY.DMP'

        @(Get-DumpCandidate -MinidumpDir $script:Dir -MemoryDmpPath $memory -NowUtc $script:Now).Count | Should -Be 0
        $with = Get-DumpCandidate -MinidumpDir $script:Dir -MemoryDmpPath $memory -IncludeMemoryDmp -NowUtc $script:Now
        @($with).Count | Should -Be 1
        $with[0].Path | Should -Be (Get-Item -LiteralPath $memory).FullName
    }

    It 'returns nothing for a missing folder' {
        @(Get-DumpCandidate -MinidumpDir (Join-Path $script:Dir 'absent') -NowUtc $script:Now).Count | Should -Be 0
    }
}

Describe 'the state file' {
    It 'round-trips, including a single entry' {
        $dump = New-FakeDump -Dir (Join-Path $TestDrive 'state-rt') -Name 'one.dmp'
        $statePath = Join-Path (Join-Path (Join-Path $TestDrive 'state-rt') 'sub') 'ninja-state.json'
        $state = @{ 'k1' = [pscustomobject]@{ key = 'k1'; path = $dump; outcome = 'uploaded' } }

        Write-CollectState -Path $statePath -State $state
        $back = Read-CollectState -Path $statePath

        $back.Keys | Should -Be @('k1')
        $back['k1'].outcome | Should -Be 'uploaded'
        (Get-Content -Raw $statePath | ConvertFrom-Json).entries.GetType().IsArray | Should -BeTrue
    }

    It 'drops entries whose file is gone' {
        $statePath = Join-Path $TestDrive 'state-gone.json'
        $state = @{ 'gone' = [pscustomobject]@{ key = 'gone'; path = (Join-Path $TestDrive 'nope.dmp'); outcome = 'uploaded' } }

        Write-CollectState -Path $statePath -State $state

        (Read-CollectState -Path $statePath).Count | Should -Be 0
    }

    It 'treats an unreadable file as empty and keeps it aside' {
        $statePath = Join-Path $TestDrive 'state-bad.json'
        Set-Content -LiteralPath $statePath -Value '{not json'

        (Read-CollectState -Path $statePath 3>$null).Count | Should -Be 0
        Test-Path -LiteralPath ($statePath + '.corrupt') | Should -BeTrue
    }
}

Describe 'Invoke-DumpTriageCollect' {
    BeforeEach {
        $script:Dir = Join-Path $TestDrive ([guid]::NewGuid().ToString())
        $script:StatePath = Join-Path (Join-Path $script:Dir 'state') 'ninja-state.json'
        $script:Minidumps = Join-Path $script:Dir 'Minidump'
        $script:Now = [datetime]'2026-10-02T08:00:00Z'
        $Run = @{
            ApiBaseUrl    = 'https://api.example.com/'
            MinidumpDir   = $script:Minidumps
            MemoryDmpPath = (Join-Path $script:Dir 'MEMORY.DMP')
            StatePath     = $script:StatePath
            NowUtc        = $script:Now
        }
        Mock Get-DumpTriageApiKey { 'dts_upload_only' }
        Mock Set-DumpTriageReportField { $true }
        Mock Write-Host { }
    }

    It 'uploads a new minidump once, with the hostname, and writes back its report link' {
        New-FakeDump -Dir $script:Minidumps -Name '101624-01.dmp' | Out-Null
        Mock Send-DumpTriageUpload { New-UploadResponse -Id '11111111-1111-1111-1111-111111111111' }

        Invoke-DumpTriageCollect @Run | Should -Be 0

        Should -Invoke Send-DumpTriageUpload -Times 1 -Exactly -ParameterFilter {
            $Uri -eq 'https://api.example.com/api/v1/dumps' -and $ApiKey -eq 'dts_upload_only' -and
            $HostName -eq [Environment]::MachineName -and (Split-Path -Leaf $Path) -eq '101624-01.dmp'
        }
        Should -Invoke Set-DumpTriageReportField -Times 1 -Exactly -ParameterFilter {
            $FieldName -eq 'dumptriageLastReport' -and $Value -eq 'https://portal.example.com/dumps/11111111-1111-1111-1111-111111111111'
        }

        # A rerun finds nothing new.
        Invoke-DumpTriageCollect @Run | Should -Be 0
        Should -Invoke Send-DumpTriageUpload -Times 1 -Exactly
    }

    It 'sends a dump again once its size or time changes' {
        $path = New-FakeDump -Dir $script:Minidumps -Name 'reused.dmp'
        Mock Send-DumpTriageUpload { New-UploadResponse }
        Invoke-DumpTriageCollect @Run | Should -Be 0

        New-FakeDump -Dir $script:Minidumps -Name 'reused.dmp' -Bytes 128 | Out-Null
        Invoke-DumpTriageCollect @Run | Should -Be 0

        Should -Invoke Send-DumpTriageUpload -Times 2 -Exactly
        $path | Should -Exist
    }

    It 'writes the newest dump''s link when several go up' {
        New-FakeDump -Dir $script:Minidumps -Name 'old.dmp' -WrittenUtc '2026-10-01T01:00:00Z' | Out-Null
        New-FakeDump -Dir $script:Minidumps -Name 'new.dmp' -WrittenUtc '2026-10-01T02:00:00Z' | Out-Null
        Mock Send-DumpTriageUpload { New-UploadResponse -Id 'aaaaaaaa-0000-0000-0000-000000000001' } -ParameterFilter { $Path -like '*old.dmp' }
        Mock Send-DumpTriageUpload { New-UploadResponse -Id 'bbbbbbbb-0000-0000-0000-000000000002' } -ParameterFilter { $Path -like '*new.dmp' }

        Invoke-DumpTriageCollect @Run | Should -Be 0

        Should -Invoke Set-DumpTriageReportField -Times 1 -Exactly -ParameterFilter { $Value -like '*bbbbbbbb-0000-0000-0000-000000000002' }
    }

    It 'leaves MEMORY.DMP alone unless asked' {
        New-FakeDump -Dir $script:Dir -Name 'MEMORY.DMP' | Out-Null
        Mock Send-DumpTriageUpload { New-UploadResponse }

        Invoke-DumpTriageCollect @Run | Should -Be 0
        Should -Invoke Send-DumpTriageUpload -Times 0 -Exactly

        Invoke-DumpTriageCollect @Run -IncludeMemoryDmp | Should -Be 0
        Should -Invoke Send-DumpTriageUpload -Times 1 -Exactly -ParameterFilter { $Path -like '*MEMORY.DMP' }
    }

    It 'omits the hostname with -NoHostname' {
        New-FakeDump -Dir $script:Minidumps -Name 'a.dmp' | Out-Null
        Mock Send-DumpTriageUpload { New-UploadResponse }

        Invoke-DumpTriageCollect @Run -NoHostname | Should -Be 0

        Should -Invoke Send-DumpTriageUpload -Times 1 -Exactly -ParameterFilter { -not $HostName }
    }

    It 'does nothing on a dry run, and needs no key for it' {
        New-FakeDump -Dir $script:Minidumps -Name 'a.dmp' | Out-Null
        Mock Get-DumpTriageApiKey { $null }
        Mock Send-DumpTriageUpload { throw 'must not upload on a dry run' }

        Invoke-DumpTriageCollect @Run -DryRun | Should -Be 0

        Should -Invoke Send-DumpTriageUpload -Times 0 -Exactly
        $script:StatePath | Should -Not -Exist
    }

    It 'exits 1 without a key and uploads nothing' {
        New-FakeDump -Dir $script:Minidumps -Name 'a.dmp' | Out-Null
        Mock Get-DumpTriageApiKey { $null }
        Mock Send-DumpTriageUpload { New-UploadResponse }

        Invoke-DumpTriageCollect @Run | Should -Be 1

        Should -Invoke Send-DumpTriageUpload -Times 0 -Exactly
    }

    It 'exits 1 for a non-https API URL' {
        Mock Send-DumpTriageUpload { New-UploadResponse }
        $insecure = $Run.Clone()
        $insecure.ApiBaseUrl = 'http://api.example.com'

        Invoke-DumpTriageCollect @insecure | Should -Be 1
    }

    It 'exits 1 when the key is refused, and stops without recording the dump' {
        New-FakeDump -Dir $script:Minidumps -Name 'a.dmp' -WrittenUtc '2026-10-01T01:00:00Z' | Out-Null
        New-FakeDump -Dir $script:Minidumps -Name 'b.dmp' -WrittenUtc '2026-10-01T02:00:00Z' | Out-Null
        Mock Send-DumpTriageUpload { [pscustomobject]@{ StatusCode = 401; Body = '{"detail":"invalid key"}'; Error = $null } }

        Invoke-DumpTriageCollect @Run | Should -Be 1

        Should -Invoke Send-DumpTriageUpload -Times 1 -Exactly
        (Read-CollectState -Path $script:StatePath).Count | Should -Be 0
    }

    It 'exits 3 for a dump the service rejects, and does not send it again' {
        New-FakeDump -Dir $script:Minidumps -Name 'junk.dmp' | Out-Null
        Mock Send-DumpTriageUpload { [pscustomobject]@{ StatusCode = 422; Body = '{"detail":"not a dump"}'; Error = $null } }

        Invoke-DumpTriageCollect @Run | Should -Be 3
        Invoke-DumpTriageCollect @Run | Should -Be 0

        Should -Invoke Send-DumpTriageUpload -Times 1 -Exactly
        @((Read-CollectState -Path $script:StatePath).Values)[0].outcome | Should -Be 'rejected'
        Should -Invoke Set-DumpTriageReportField -Times 0 -Exactly
    }

    It 'exits 2 on <name>, stops there, and retries the dump next run' -ForEach @(
        @{ name = 'a network failure'; status = 0 }
        @{ name = 'the quota (429)'; status = 429 }
        @{ name = 'an outage (503)'; status = 503 }
    ) {
        New-FakeDump -Dir $script:Minidumps -Name 'a.dmp' -WrittenUtc '2026-10-01T01:00:00Z' | Out-Null
        New-FakeDump -Dir $script:Minidumps -Name 'b.dmp' -WrittenUtc '2026-10-01T02:00:00Z' | Out-Null
        $code = $status
        Mock Send-DumpTriageUpload { [pscustomobject]@{ StatusCode = $code; Body = $null; Error = 'unreachable' } }

        Invoke-DumpTriageCollect @Run | Should -Be 2
        Should -Invoke Send-DumpTriageUpload -Times 1 -Exactly

        Mock Send-DumpTriageUpload { New-UploadResponse }
        Invoke-DumpTriageCollect @Run | Should -Be 0
        Should -Invoke Send-DumpTriageUpload -Times 3 -Exactly
    }

    It 'sends at most -MaxUploads per run, oldest first' {
        1..3 | ForEach-Object { New-FakeDump -Dir $script:Minidumps -Name "d$_.dmp" -WrittenUtc ([datetime]'2026-10-01T00:00:00Z').AddHours($_) | Out-Null }
        Mock Send-DumpTriageUpload { New-UploadResponse }

        Invoke-DumpTriageCollect @Run -MaxUploads 2 | Should -Be 0
        Should -Invoke Send-DumpTriageUpload -Times 2 -Exactly
        Should -Invoke Send-DumpTriageUpload -Times 0 -Exactly -ParameterFilter { $Path -like '*d3.dmp' }

        Invoke-DumpTriageCollect @Run -MaxUploads 2 | Should -Be 0
        Should -Invoke Send-DumpTriageUpload -Times 3 -Exactly
    }
}

Describe 'the NinjaOne cmdlet wrappers' {
    AfterEach {
        Remove-Item -Path Function:\Ninja-Property-Get, Function:\Ninja-Property-Set -ErrorAction SilentlyContinue
        Remove-Item -Path Env:\DUMPTRIAGE_API_KEY -ErrorAction SilentlyContinue
        Remove-Variable -Name NinjaWrites -Scope Global -ErrorAction SilentlyContinue
    }

    It 'reads the key from the custom field when Ninja-Property-Get exists' {
        function global:Ninja-Property-Get { param($Name) if ($Name -eq 'dumptriageApiKey') { ' dts_from_field ' } }
        $env:DUMPTRIAGE_API_KEY = 'dts_from_env'

        Get-DumpTriageApiKey -FieldName 'dumptriageApiKey' | Should -Be 'dts_from_field'
    }

    It 'falls back to DUMPTRIAGE_API_KEY when the field is empty or the cmdlet absent' {
        $env:DUMPTRIAGE_API_KEY = 'dts_from_env'
        Get-DumpTriageApiKey -FieldName 'dumptriageApiKey' | Should -Be 'dts_from_env'

        function global:Ninja-Property-Get { param($Name) '' }
        Get-DumpTriageApiKey -FieldName 'dumptriageApiKey' | Should -Be 'dts_from_env'
    }

    It 'returns nothing when there is no key anywhere' {
        Get-DumpTriageApiKey -FieldName 'dumptriageApiKey' | Should -BeNullOrEmpty
    }

    It 'writes the report link with Ninja-Property-Set when it exists' {
        $global:NinjaWrites = @()
        function global:Ninja-Property-Set { param($Name, $Value) $global:NinjaWrites += "$Name=$Value" }

        Set-DumpTriageReportField -FieldName 'dumptriageLastReport' -Value 'https://portal.example.com/dumps/x' | Should -BeTrue
        $global:NinjaWrites | Should -Be @('dumptriageLastReport=https://portal.example.com/dumps/x')
    }

    It 'reports $false outside NinjaOne' {
        Set-DumpTriageReportField -FieldName 'dumptriageLastReport' -Value 'https://x' | Should -BeFalse
    }
}

Describe 'Resolve-UploadOutcome' {
    It 'maps HTTP <status> to <outcome>' -ForEach @(
        @{ status = 201; outcome = 'Success' }
        @{ status = 200; outcome = 'Success' }
        @{ status = 401; outcome = 'AuthFailed' }
        @{ status = 403; outcome = 'AuthFailed' }
        @{ status = 400; outcome = 'Rejected' }
        @{ status = 413; outcome = 'Rejected' }
        @{ status = 422; outcome = 'Rejected' }
        @{ status = 429; outcome = 'Retry' }
        @{ status = 503; outcome = 'Retry' }
        @{ status = 0; outcome = 'Retry' }
    ) {
        Resolve-UploadOutcome -Result ([pscustomobject]@{ StatusCode = $status }) | Should -Be $outcome
    }
}
