# DumpTriage Windows Agent

A Windows background service that watches for crash dump files, decides
whether to submit each one to DumpTriage based on a configurable policy, and
optionally archives a copy to a shared location. No backend changes are
required — it targets the existing `POST /api/v1/dumps` endpoint the web
uploader already uses.

## Layout

- `src/DumpTriage.Common` — cross-platform core: dump detection/sniffing,
  the policy engine, the local SQLite queue, the API client, the central-copy
  service. No Windows-only APIs; builds and unit-tests on any OS.
- `src/DumpTriageAgent` — the Windows Service. Owns filesystem watching, the
  queue, and all HTTP submission. No UI (Session 0 can't show one).
- `src/DumpTriageTray` — a per-user tray app (WinForms) that shows approval
  prompts and status, started at logon. Talks to the service over a named
  pipe (`\\.\pipe\DumpTriageAgent`); never touches the API directly.
- `tests/DumpTriage.Common.Tests` — xUnit tests for the policy engine, batch
  scheduler, dump sniffer, stability checker, queue store, and central-copy
  service.

## Linux (headless service)

On Linux the agent runs as a plain systemd service with **no tray app**.
Everything except the tray works the same: watching, policy, queue, upload,
central copy. Differences:

- Paths follow the FHS: config `/etc/dumptriage/config.json`, queue
  `/var/lib/dumptriage/queue.db`, logs `/var/log/dumptriage` (and the journal).
- The API key is stored in `/etc/dumptriage/apikey` (mode 0600); config.json
  only holds a `file:` reference. Provision with
  `echo <key> | DumpTriageAgent --set-api-key-stdin`.
- Default watched paths are `/var/crash` and `/var/lib/dumptriage/incoming`;
  adjust `watchedPaths` / `watchedFilters` in config.json. This agent only
  recognises Windows dump formats (MDMP / PAGE); for Linux cores and kdump
  vmcores use the [Linux agent](../../linux/dumptriage-agent/).
- The unit runs with `ProtectSystem=strict`; if `centralCopy` is enabled, add
  its destination with `systemctl edit dumptriage-agent` (`ReadWritePaths=`).
- No UI means no prompts: use `AlwaysSubmit` or `RuleBased`. `AskFirst`
  resolves via `askTimeoutDefaultAction` after the timeout, and `NotifyOnly`
  dumps stay pending (a warning is logged). `Idle` batching can't detect user
  activity, so use `Immediate` or `ScheduledTimeOfDay`.

```bash
dotnet publish src/DumpTriageAgent -c Release -r linux-x64 --self-contained -o publish/agent
echo "$DUMPTRIAGE_KEY" | sudo installer/linux/install.sh publish/agent -
```

## Why two processes (Windows)

A Windows Service runs in Session 0 and can't show a toast or tray icon to a
logged-in user. A pure per-user tray app can't run before login or watch
`%WINDIR%\MEMORY.DMP` reliably. So the service does the watching/policy/
submission (runs as LocalSystem, always on) and the tray app only handles UI,
started per-user via a logon-triggered scheduled task.

## Building

```powershell
dotnet publish src/DumpTriageAgent -c Release -r win-x64 --self-contained -o publish/agent
dotnet publish src/DumpTriageTray -c Release -r win-x64 --self-contained -o publish/tray
```

(Both must be built on Windows — the tray app requires the Windows Desktop
SDK. The `DumpTriage.Common` and test projects are plain net8.0 and build
fine on Linux/macOS too, which is how this was developed/tested.)

## Installing

Use an **upload-only** key, not the account's primary key. An upload key can
only call `POST /api/v1/dumps`, so a key copied off one machine can't list or
download the org's other dumps. Make one on the portal's Account page (or with
the API), one per fleet or group, and revoke it by id if a machine is lost:

```sh
curl -X POST https://api.performanceinsights.ai/api/v1/customers/me/api-keys \
  -H "Authorization: Bearer <primary key>" -H "Content-Type: application/json" \
  -d '{"name": "workstations", "scope": "upload"}'
# -> {"id": "...", "api_key": "dts_...", "scope": "upload", ...}  (shown once)
curl -X DELETE https://api.performanceinsights.ai/api/v1/customers/me/api-keys/<id> \
  -H "Authorization: Bearer <primary key>"
```

```powershell
.\installer\install.ps1 -AgentPublishDir .\publish\agent -TrayPublishDir .\publish\tray
```

On a first install the script asks for the API key at a masked prompt. For an
unattended install, put the key in a file and pass `-ApiKeyFile key.txt` (the
file is deleted once the key is stored), or pipe it into the script's stdin
(`Get-Content key.txt | .\installer\install.ps1 ...`). The key is never
accepted as a command-line argument, because that would leave it in PowerShell
history and in process-creation audit logs.

Registers the `DumpTriageAgent` Windows Service (auto-start), provisions the
API key (encrypted at rest with machine-scope DPAPI — never written to disk
in plaintext), and installs a logon-trigger scheduled task for the tray app.
Run `installer\uninstall.ps1` to remove everything (`-RemoveData` also
deletes the local queue/config/logs).

## Configuration

`%ProgramData%\DumpTriage\config.json`, created with defaults on first run.
Restart the service to pick up changes.

`%ProgramData%\DumpTriage` (config with the DPAPI-protected key, queue, logs)
is readable by SYSTEM and Administrators only: the service sets a protected
ACL on it at every start. It refuses to start if that folder is a junction or
is owned by any other account, since another user could have created it
first; delete it and reinstall.

```jsonc
{
  "apiBaseUrl": "https://api.performanceinsights.ai",
  "sendHostname": true,
  "watchedPaths": ["%LOCALAPPDATA%\\CrashDumps", "%WINDIR%\\Minidump", "%WINDIR%"],
  "watchedFilters": ["*.dmp", "MEMORY.DMP"],
  "startupScanMaxAgeDays": 30,    // on start, also look at dumps already there from the last N days; 0 = off
  "policy": {
    "mode": "AskFirst",           // AlwaysSubmit | AskFirst | NotifyOnly | RuleBased
    "rules": [],                   // used only when mode == RuleBased
    "defaultRuleAction": "Ask",
    "askTimeoutSeconds": 300,
    "askTimeoutDefaultAction": "Skip",  // fail closed: an unanswered prompt does NOT submit
    "batching": {
      "mode": "Immediate",         // Immediate | ScheduledTimeOfDay | Idle
      "scheduledTimesLocal": [],
      "idleMinutes": 15
    }
  },
  "centralCopy": {
    "enabled": false,
    "destinationPath": "\\\\fileserver\\dumps",
    "organizeByHostname": true
  }
}
```

**Decision mode** (`policy.mode`) decides *whether* a dump gets submitted at
all; **timing mode** (`policy.batching`) decides *when* an already-approved
dump actually gets POSTed. They're independent — e.g. `AskFirst` +
`ScheduledTimeOfDay` prompts immediately on detection but doesn't upload the
approved bytes until the nightly window. A scheduled time counts as used even when nothing was
queued then, so a dump approved after it waits for the next one, and a fresh
install or restart waits for the next scheduled time rather than uploading at
once.

`NotifyOnly` dumps are listed under **Submit dump** in the tray icon's menu;
choosing one uploads it. Approval popups (`AskFirst`) are shown one at a time,
each with the time the service has left to wait for it.

`RuleBased` mode evaluates `policy.rules` in order, first match wins:

```jsonc
{ "maxSizeBytes": 104857600, "action": "Submit" },
{ "dumpType": "KernelMode", "action": "Ask" },
{ "pathGlob": "C:\\Dumps\\*", "action": "Submit" }
```

`centralCopy`, when enabled, copies every approved dump to
`destinationPath\<hostname>\` (a plain file copy — works for a UNC share or a
local/mapped folder) alongside submitting it to the API. It's a best-effort
archival step: if the share is offline, submission still proceeds and the
failure is only logged (`CentralCopyError` on the queue record), not retried
automatically. The copy is written under a `.tmp` name and renamed when
complete, so an interrupted copy never sits at the final name.

## Performance traces (website#1999, dumptriage-tools#29)

Off by default. With the `tracing` section enabled, the service keeps a WPR flight recorder running (memory mode: a ring of
buffers, nothing written to disk until a capture), watches the triggers, and when one fires it stops the recorder into an
`.etl`, zips it (`trace.etl` first, with its `.etl.NGENPDB` folder when WPR writes one), writes a `manifest.json`, queues the
pair like a dump and starts the recorder again. The upload sends it to `POST /api/v1/traces`; the zip is deleted once it is
sent and never goes to `centralCopy`. The `.etl` is not read on this machine: the service parses it on one of its Windows
workers. The service must accept WPR traces (`TRACE_ETL_ENABLED`); until it does, the upload is refused and retried.

```jsonc
"tracing": {
  "enabled": true,
  "backend": "wpr",                   // the only backend on Windows
  "submission": "AskFirst",           // AlwaysSubmit | AskFirst | NotifyOnly | RuleBased: held for approval by default
  "triggers": [
    { "name": "cpu-high",  "type": "cpuPercent",    "threshold": 90, "sustainSec": 30 },
    { "name": "sql-busy",  "type": "process",       "process": "sqlservr", "threshold": 150, "sustainSec": 20 },
    { "name": "io-slow",   "type": "diskLatencyMs", "threshold": 100, "sustainSec": 10 },
    { "name": "queue",     "type": "loadPerCpu",    "threshold": 2,   "sustainSec": 60 },
    { "name": "hang",      "type": "hangEvent" },
    { "name": "baseline",  "type": "schedule",      "intervalMin": 1440 }
  ],
  "limits": { "maxDurationSec": 60, "maxDiskMB": 512, "maxOverheadPercent": 2, "cooldownMin": 60, "dailyCap": 4 }
}
```

The keys are the shared contract ([`schemas/tracing-config.schema.json`](../../schemas/tracing-config.schema.json)); a section
that does not read turns tracing off, and the log says why. `RuleBased` uses only the `policy.rules` that name traces
(`"dumpType": "Trace"`), then `defaultRuleAction`: a dump rule never sends a trace. With tracing off, traces still waiting
for a decision are skipped and their zips removed; queued ones still upload, after the dumps.

| Trigger | Reads | Value |
|---|---|---|
| `cpuPercent` | `GetSystemTimes` | percent of all CPUs busy; with `process`, percent of **one** CPU used by processes of that name (with or without `.exe`) |
| `process` | each process's `TotalProcessorTime` | the same per-process percent (needs `process`) |
| `loadPerCpu` | `\System\Processor Queue Length` | threads waiting for a CPU, per CPU |
| `diskLatencyMs` | `\PhysicalDisk(_Total)\Avg. Disk sec/Transfer` | average time per I/O, in ms |
| `hangEvent` | the Application log | an Application Hang event (ID 1002) since the last reading |
| `schedule` | the clock | a baseline every `intervalMin` (the first one an interval after the first run) |

A trigger is met when its value stays at or above `threshold` for `sustainSec`; then `cooldownMin` and `dailyCap` decide
whether it captures, by the rules of the shared fixture [`tests/fixtures/tracing/triggers.json`](../../tests/fixtures/tracing/triggers.json).
For a capture now, create an empty `capture.request` in `%ProgramData%\DumpTriage\traces` (cooldown and cap still apply).
`%ProgramData%\DumpTriage\traces\status.json` says whether the recorder runs, the last error and the last capture.

**What it records** ([`windows/PerfInsights.wprp`](../PerfInsights.wprp)): CPU samples every 4 ms with stacks (`wpr -setprofint
40000`, set once the recorder has the kernel logger and put back to WPR's 1 ms default when the service stops), context switches with stacks (how
long the working threads waited, and on what), ready-thread events, disk I/O and its issuing thread, DPC and ISR time, hard
faults, process, thread and image loads, and the .NET runtime's loader and JIT events with its rundown when the trace is
saved. Without those last ones most frames of a managed process have no name: 85% of a PowerShell workload's busy samples on
the lab VM, 1% with them.

**Limits, enforced here and not by the service:**
- `maxDiskMB`: the kernel ring is a quarter of it (16 to 256 buffers of 1 MB; the trace WPR writes is about the ring's size), a
  zip over it is discarded, and the trace folder is kept inside it.
- `maxDurationSec`: in memory mode the ring holds whatever fits, so the window is what the ring held when the trigger fired;
  the manifest says what it is.
- `maxOverheadPercent`: **cannot be measured per process on Windows**: the kernel logger's cost is charged to whatever was
  running, not to the service. The profile is made light enough instead, and status.json says so. **Measured on the lab VM
  (Server 2022, 4 vCPU GCE e2-standard-4, a fixed SHA-256 workload with and without the recorder, alternating):** 16 rounds,
  mean +1.7%, median +1.6%, slower in 11 of 16; the rounds vary by about 5% on their own. The first draft of the profile
  (ReadyThread stacks, file I/O, 1 ms sampling) cost about 4%.
- `cooldownMin`, `dailyCap`: above.

It needs `wpr.exe` (Windows 10, Server 2016 and later ship it) and runs as LocalSystem, which the kernel logger requires.
**It never takes the kernel logger from another tool:** when `wpr -start` fails (Procmon, xperf, an EDR or another WPR
session holds it), the service leaves that session alone, says why in status.json and tries again 5 minutes later.

**What the manifest says:** the trigger and the value that fired it, the window, the exact wpr command, WPR's version, the
Windows build, an agent version and a random per-install `hostToken` kept in the queue, not derived from the hostname. The
`.etl` itself holds process names, file paths and call stacks of everything that ran; command lines and event payloads of
other providers are not recorded by the profile.

**Without the agent:** [`windows/Start-PerfTrace.ps1`](../Start-PerfTrace.ps1) (with `PerfInsights.wprp` next to it, both in
the release) takes one capture in file mode while you reproduce the problem, writes the zip and its manifest, and with
`-Upload` sends them using the key in `$env:DUMPTRIAGE_API_KEY`:

```powershell
.\Start-PerfTrace.ps1 -Seconds 60 -OutDir C:\Traces             # then run it again with -Upload, or use the curl line it prints
```

## Uploads, retries and holds

- **Detection.** A dump is recorded once it has stopped changing and can be
  opened; one still being written after 30 minutes is looked at again 5
  minutes later. A file is known by its path, size and last-write time, and
  a dump by its sha256: a new crash written over `MEMORY.DMP` is a new dump,
  and a copy of a dump already in the queue doesn't touch that record. A
  watched folder that doesn't exist yet, or is deleted later, is watched once
  it (re)appears.
- **On start** the service also looks at the dumps already in the watched
  folders (`watchedFilters`) last written within `startupScanMaxAgeDays`
  (default 30) and since the agent's first start: a dump written while it was
  stopped, or a `MEMORY.DMP` Windows wrote early in the boot after a crash,
  raises no watcher event, but a fresh install leaves older dumps alone. Ones
  already in the queue are skipped without hashing them again. A dump the last
  run left waiting for a decision or an approval gets one again.
- **Before upload** the file's size and last-write time are checked against
  what was hashed. If they moved, the file is hashed again; changed bytes end
  that record (`Superseded`) and the file is detected again as a new dump. Every
  upload sends `X-Content-SHA256`, and the server refuses other bytes with a
  422, handled the same way.
- **No whole-upload timeout.** An upload is abandoned when the body stops
  moving for 5 minutes, or when the server hasn't answered 30 minutes after
  the last byte (it scans and stores a large dump before it answers).
- **Transient failures** (network, 5xx, a file held open by antivirus or a
  backup) are retried with exponential backoff per dump (1 minute, doubling,
  up to 6 hours); a network or server failure ends that pass over the queue.
  A dump gives up 7 days after its first failure. With `ScheduledTimeOfDay`
  a slot opens at the first check at or after its time and is used up even
  with nothing queued, so a dump queued later that day waits for the next
  one; the last slot is kept in the queue, so a fresh install or a restart
  waits for the next slot rather than uploading at once. A slot stays open for the dumps that were queued when it opened, while any
  of them is still waiting (in its backoff, or behind an outage or hold), so
  it goes once its backoff ends rather than at the next slot. A dump queued
  after the slot opened waits for the next slot, and the next slot replaces
  one still held open, so a failing dump never keeps uploads going outside
  the schedule.
- **Permanent rejections** (400, 413, 415, 422) and a deleted file fail the
  dump at once.
- **Queue-wide holds**, which spend no retries and show in the tray: Terms
  of Service not accepted (15 minutes), API key rejected or account closed
  (401/403, 1 hour), monthly quota used up (429, 6 hours).
- **Retry failed uploads** in the tray menu puts every `Failed` dump back in
  the queue.
- A dump cut off mid-upload by a crash, reboot or service stop is requeued at
  the next start. A corrupt `queue.db` is moved to `queue.db.corrupt-<time>`
  and an empty queue started. A `queue.db` made by an older agent gets the
  new columns added in place and keeps its rows.

## Known limitations / follow-ups

- **Toast prompts are a lightweight borderless popup, not a true Action
  Center toast.** Actionable toast buttons for an unpackaged Win32 app
  require registering an AppUserModelID + COM notification activator via the
  installer — real added complexity for marginal UX gain. `ApprovalPopupForm`
  can be swapped for a real toast later without touching the IPC protocol.
- No installer for email/Slack notification fallback (useful for `NotifyOnly`
  mode on unattended machines) — scoped out of v1.
- Central-copy failures aren't retried; only the API submission has retry/
  backoff via the queue.
- No MSI — `installer/install.ps1` is the v1 install path. A WiX MSI is a
  reasonable follow-up once this is validated in the field.
