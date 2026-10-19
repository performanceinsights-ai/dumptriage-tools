# DumpTriage for NinjaOne

A NinjaOne Script Library script that sends a device's new Windows crash dumps
to DumpTriage and writes the report link back into a NinjaOne custom field. Nothing is installed on the
endpoint: NinjaOne runs the script on a schedule, as SYSTEM.

NinjaOne already knows a device crashed. This script adds what the crash was:
the technician opens the device in NinjaOne, clicks the link in
`dumptriageLastReport`, and reads the debugger-driven root cause.
It stops there: no alerting, inventory or grouping of its own.

## What a run does

1. Lists `%WINDIR%\Minidump\*.dmp`. `%WINDIR%\MEMORY.DMP` (a full or kernel
   dump, often gigabytes) is only included with `-IncludeMemoryDmp`.
2. Skips anything already handled. `%ProgramData%\DumpTriage\ninja-state.json`
   records each dump by path, size and last-write time, so a rerun sends
   nothing twice. A dump written in the last 2 minutes is left for the next
   run, in case it is still being written.
3. Uploads the rest, oldest first, at most 10 per run, to
   `POST {ApiBaseUrl}/api/v1/dumps` (multipart `file` and `hostname`, Bearer
   auth). The hostname is only ever turned into a keyed hash server side; send
   `-NoHostname` to leave it out.
4. Writes the newest report link into the `dumptriageLastReport` custom field
   and prints every link to the activity log.

| Exit code | Meaning | What to do |
|---|---|---|
| 0 | Nothing new, or everything uploaded | Nothing |
| 1 | Could not run: no key, key refused (401/403), bad URL | Fix the key field |
| 2 | Upload failed in a retryable way: network, quota (429), outage (5xx) | Nothing; the next run retries it |
| 3 | The service rejected a dump for good (not a dump, too large) | Look at the log; it is not resent |

## Setting it up

### 1. Mint an upload-only key

Use an upload-only key, never the account's primary key: it can only call
`POST /api/v1/dumps`, so a key read off one device can't list or download the
organisation's other dumps. One key per NinjaOne organisation (or per client,
for an MSP) makes revoking one easy:

```sh
curl -X POST https://api.performanceinsights.ai/api/v1/customers/me/api-keys \
  -H "Authorization: Bearer <primary key>" -H "Content-Type: application/json" \
  -d '{"name": "ninjaone", "scope": "upload"}'
# -> {"id": "...", "api_key": "dts_...", "scope": "upload", ...}  (shown once)
curl -X DELETE https://api.performanceinsights.ai/api/v1/customers/me/api-keys/<id> \
  -H "Authorization: Bearer <primary key>"
```

### 2. Create two custom fields

In Administration > Devices > Global (or Role) Custom Fields:

| Name | Type | Technician | Automations | Notes |
|---|---|---|---|---|
| `dumptriageApiKey` | Secure | Read/Write (or none) | Read Only | The upload-only key. Set it at organisation level so devices inherit it. |
| `dumptriageLastReport` | Text | Read Only | Read/Write | Filled in by the script. |

The script reads the key with `Ninja-Property-Get` and never takes it as a
script parameter: a key on a command line shows up in process listings and
logs. Outside NinjaOne it reads `DUMPTRIAGE_API_KEY` instead.

### 3. Add the script

Administration > Library > Automation > Add > New Script:

- Name: `DumpTriage: send crash dumps`
- Language: PowerShell; Operating system: Windows; Architecture: All
- Run as: System
- Body: paste `Invoke-DumpTriageCollect.ps1` as it is.
- Optional script variables (NinjaOne passes them as environment
  variables): checkbox `includeMemoryDmp`, checkbox `dryRun`, text
  `DUMPTRIAGE_API_BASE_URL` (only for a self-hosted or staging backend).

Parameters, if you would rather use the preset parameter line:
`-IncludeMemoryDmp`, `-DryRun`, `-MaxUploads 10`, `-NoHostname`,
`-ApiBaseUrl https://...`, `-ApiKeyFieldName`, `-ReportFieldName`.

### 4. Schedule it

In the policy for the devices you want covered, Scheduled Automations > Add:
the script above, every 4 hours (or daily). A crash reboots the machine and
Windows writes the minidump during that reboot, so the next scheduled run
after it picks the dump up. Add a condition or alert on a non-zero result
(see the exit codes) if you want to hear about a bad key.

Try it first with `dryRun` ticked on one device: the activity log lists what
would be sent and nothing is uploaded.

### Minidumps by default

Minidumps are small (a few hundred KB) and hold little beyond the faulting
thread and loaded modules, which keeps both the upload and what leaves the
device small. Turn on `includeMemoryDmp` only where you need kernel-level
detail and the device has the bandwidth. A large `MEMORY.DMP` can outlast
NinjaOne's default script timeout; raise the timeout for that script.

## Tests

`tests/Invoke-DumpTriageCollect.Tests.ps1` (Pester 5) dot-sources the script,
which defines its functions without running it, and mocks the upload and the
NinjaOne cmdlet wrappers:

```sh
pwsh -NoProfile -File rmm/ninjaone/tests/Run-Tests.ps1
```

CI runs them in the `ninjaone` job of `.github/workflows/tests.yml`.
