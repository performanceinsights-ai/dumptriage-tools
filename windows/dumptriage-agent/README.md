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

```powershell
.\installer\install.ps1 -ApiKey dts_xxx -AgentPublishDir .\publish\agent -TrayPublishDir .\publish\tray
```

Registers the `DumpTriageAgent` Windows Service (auto-start), provisions the
API key (encrypted at rest with machine-scope DPAPI — never written to disk
in plaintext), and installs a logon-trigger scheduled task for the tray app.
Run `installer\uninstall.ps1` to remove everything (`-RemoveData` also
deletes the local queue/config/logs).

## Configuration

`%ProgramData%\DumpTriage\config.json`, created with defaults on first run.
Restart the service to pick up changes.

```jsonc
{
  "apiBaseUrl": "https://api.performanceinsights.ai",
  "sendHostname": true,
  "watchedPaths": ["%LOCALAPPDATA%\\CrashDumps", "%WINDIR%\\Minidump", "%WINDIR%"],
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
approved bytes until the nightly window.

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
automatically.

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
