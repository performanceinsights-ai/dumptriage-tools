# DumpTriage Linux agent

The Linux counterpart of the [Windows agent](../../windows/dumptriage-agent/): a background service that
watches for crash dumps (ELF process cores and kdump/vmcore kernel dumps), decides whether to submit each
one to DumpTriage from a configurable policy, and uploads it to the existing `POST /api/v1/dumps` endpoint.
No backend changes. It is one Python 3 file using only the standard library, so there is nothing to build
or fetch: install it by copying it, which `installer/install.sh` does.

Pair it with [`../configure-dumps.sh`](../configure-dumps.sh), which makes the kernel actually write
cores somewhere the agent watches (the equivalent of `Set-WerLocalDumps.ps1`).

## Quick start

```sh
# 1. Capture: write raw cores to /var/crash/dumptriage, unlimited size, 14 day retention
sudo linux/configure-dumps.sh                  # add --kdump for kernel crash dumps (needs a reboot)

# 2. Agent: use an upload-only key (see the Windows README for minting one)
echo "dts_xxx" > key.txt
sudo linux/dumptriage-agent/installer/install.sh --api-key-file key.txt --policy AskFirst
```

The installer picks how to run in the background:

| Method | Chosen when | Runs as |
|---|---|---|
| `systemd-system` | root, systemd present | system service, can read root-only dumps (`/var/crash`, `/var/lib/systemd/coredump`) |
| `systemd-user` | normal user whose `systemctl --user` works | user service; `loginctl enable-linger` is turned on so it runs without a login |
| `cron` | no systemd (or `--method cron`) | `run --once` every minute under `flock` (`/etc/cron.d` as root, the user crontab otherwise) |

Override with `--method`. Re-running the installer upgrades the agent and keeps `config.json`; options given
on the command line (`--policy`, `--api-url`, `--watch`) update just those fields. The key is read from
`--api-key-file` (deleted once stored) or stdin (`--api-key-stdin`), never from argv. `installer/uninstall.sh`
removes the service, cron job and binaries; `--remove-data` also deletes config, key and queue.

A user-mode install can only read what that user may read. The kernel writes each core as the crashing
user, mode 0600, so a user agent uploads its own crashes only; use the system install for a whole machine.

## What it detects

Files under `watchedPaths` (two directory levels deep, so kdump's `/var/crash/<timestamp>/dump.<timestamp>`
is found). A file qualifies when its leading bytes classify it as a Linux dump, using the same rules as the
backend's `dump_sniff.py`: an ELF `ET_CORE` (process core), unless it carries a `VMCOREINFO` note or
kernel-space segments (kernel vmcore); `KDUMP   ` / `DISKDUMP` / `makedumpfile` (kdump formats). Anything
else (text, executables, Windows dumps) is ignored. A dump is accepted only after its size has been stable
for `stableSeconds` and no other process holds it open. systemd-coredump's `.zst`, `.xz` and `.lz4` cores
are sniffed through the decompressor and uploaded decompressed (the backend needs the raw ELF) via a spool
file in the state directory that is removed afterwards; `zstd` / `lz4` must be installed for those two.


Files older than the agent's first run are left alone (logged once each), so a fresh install doesn't
upload every old core already in the watched folders. "Older" goes by the later of a file's mtime and
ctime, so a core moved or copied in on purpose (which keeps its mtime) still counts, and so does a core
written while the agent was stopped. An upgrade from a version without this filters nothing, since that
agent has already seen everything in its folders.
## Configuration

`/etc/dumptriage/config.json` (system) or `~/.config/dumptriage/config.json` (user), same model as Windows.
Restart the service to apply a change (`systemctl restart dumptriage-agent`; cron picks it up on the next run).

```jsonc
{
  "apiBaseUrl": "https://api.performanceinsights.ai",
  "sendHostname": true,
  "watchedPaths": ["/var/crash", "/var/lib/systemd/coredump"],  // user installs: ["/var/crash/dumptriage"]
  "pollSeconds": 5, "stableSeconds": 10, "maxRetries": 10,
  "policy": {
    "mode": "AskFirst",            // AlwaysSubmit | AskFirst | NotifyOnly | RuleBased
    "rules": [],                    // RuleBased: first match wins; maxSizeBytes, minSizeBytes, dumpType
                                    //   (LinuxUserCore | LinuxKernel), pathGlob, businessHoursStart/End "HH:MM", action Submit|Ask|Skip
    "defaultRuleAction": "Ask",
    "askTimeoutSeconds": 300,
    "askTimeoutDefaultAction": "Skip",   // fail closed: an unanswered ask never uploads
    "batching": { "mode": "Immediate", "scheduledTimesLocal": ["02:00"] }  // Immediate | ScheduledTimeOfDay
  },
  "centralCopy": { "enabled": false, "destinationPath": "/srv/dumps", "organizeByHostname": true }
}
```

Decision mode (`policy.mode`) decides whether a dump is submitted; timing (`policy.batching`) decides when an
approved one is uploaded. Linux has no tray, so approvals are made on the command line:

```sh
dumptriage-agent list                 # id, state, type, size, path
dumptriage-agent approve <id|all>     # PendingApproval -> upload
dumptriage-agent skip <id|all>
dumptriage-agent retry <id>           # Failed -> queued again
dumptriage-agent status               # counts per state
```

`AskFirst` and rule-`Ask` dumps wait `askTimeoutSeconds`, then take `askTimeoutDefaultAction` (Skip by
default). `NotifyOnly` dumps wait for `approve` with no timeout. Each pending dump is logged to the journal
(`journalctl -u dumptriage-agent`) with the exact command. Transient failures retry with exponential
backoff (30 s doubling, capped at 1 h) up to `maxRetries`; a 400, 413 or 429 is final (`Failed`). A 401 or 403 means the key
is wrong or revoked: no attempt is used up, and uploads pause for 10 minutes, doubling with each refusal in
a row up to 6 hours (every attempt sends the whole dump). Storing a new key with `set-api-key` ends the
pause. With `ScheduledTimeOfDay`, uploads happen only once a scheduled time has passed since the
last flush, and a slot with nothing queued still counts, so a dump queued after it waits for the next one.

A central copy never overwrites another dump's copy: if the name is taken (kdump calls every dump
`vmcore`), the copy gets the first 8 characters of the dump id before its extension (`vmcore.1a2b3c4d`,
`core.app.1.2.1a2b3c4d.zst`). Copies keep the dump's file mode, so a 0600 core stays 0600.

Capture folders like `/var/crash/dumptriage` are world-writable, so the agent never follows a symlink
below a watched path and only reads regular files with a single link. A symlink planted there (to
`/proc/kcore`, or to another user's core) is ignored rather than uploaded. A watched path is not watched if it goes
through a symlink another user could have planted: one not owned by root or the agent's user, or in a
folder they don't own, or in a world-writable folder that isn't sticky. Another user could otherwise point
`/var/crash/dumptriage` anywhere before `configure-dumps.sh` creates it. Real folders owned by someone
else, such as an app's own dump folder, are fine, and so are group-writable folders of root or the agent's
user. The log names the link that was refused, or the real error if a watched path can't be opened.
Central copies are written under a temporary name and moved into place, so a failed copy leaves nothing
behind.

## Performance tracing (website#1999)

Off by default. With the `tracing` section enabled, the daemon keeps a `perf` flight recorder running, watches `/proc` for a
trigger, and when one fires it folds the recorder's snapshot to stacks **on the host**, queues the result like a dump and
uploads it to `POST /api/v1/traces`. Only text leaves the host: folded stacks (`process;frame;...;frame count`, a kernel
frame ending in `_[k]`, as `stackcollapse-perf.pl --kernel` writes them) and a `manifest.json`. The raw `perf.data` is
deleted as soon as it is folded. The service must have traces on (`TRACES_ENABLED`); until then the upload is refused and
retried.

```sh
sudo linux/configure-tracing.sh                    # installs perf; see its header for what else it can do
```

```jsonc
"tracing": {
  "enabled": true,
  "backend": "perf",                  // perf (default: folded stacks) | perfetto (a binary trace, parsed on a worker; below)
  "submission": "AskFirst",           // AlwaysSubmit | AskFirst | NotifyOnly | RuleBased: held for approval by default
  "triggers": [
    { "name": "cpu-high",  "type": "cpuPercent",    "threshold": 90, "sustainSec": 30 },
    { "name": "sql-busy",  "type": "process",       "process": "sqlservr", "threshold": 150, "sustainSec": 20 },
    { "name": "io-slow",   "type": "diskLatencyMs", "threshold": 100, "sustainSec": 10 },
    { "name": "pressure",  "type": "psiSomeAvg10",  "threshold": 20,  "sustainSec": 30 },
    { "name": "load",      "type": "loadPerCpu",    "threshold": 2,   "sustainSec": 60 },
    { "name": "baseline",  "type": "schedule",      "intervalMin": 1440 }
  ],
  "limits": { "maxDurationSec": 60, "maxDiskMB": 512, "maxOverheadPercent": 2, "cooldownMin": 60, "dailyCap": 4 }
}
```

The keys are the shared contract ([`schemas/tracing-config.schema.json`](../../schemas/tracing-config.schema.json)); the agent checks
the section and turns tracing off, saying why in the log, if it does not read.

| Trigger | Reads | Value |
|---|---|---|
| `cpuPercent` | `/proc/stat` | percent of all CPUs busy (not idle, not iowait); with `process`, percent of **one** CPU used by processes of that name (`comm`, 15 characters) |
| `process` | `/proc/<pid>/stat` | the same per-process percent (needs `process`) |
| `loadPerCpu` | `/proc/loadavg` | the 1-minute load divided by the CPU count |
| `psiSomeAvg10` | `/proc/pressure/{cpu,io,memory}` | the highest `some avg10` of the three |
| `diskLatencyMs` | `/proc/diskstats` | the worst whole disk's average time per completed I/O over the interval |
| `schedule` | the clock | a baseline every `intervalMin` (the first one an interval after the first run) |
| `hangEvent` | nothing on Linux | never fires; the log says so |

A trigger is met when its value stays at or above `threshold` for `sustainSec`; a dip, or no reading, restarts the clock. Then
`cooldownMin` (no capture within it) and `dailyCap` (captures in the last 24 hours) decide whether it captures. These are the
rules of the shared fixture [`tests/fixtures/tracing/triggers.json`](../../tests/fixtures/tracing/triggers.json), which this
agent's tests run, as do the Windows and macOS suites.

**Limits, enforced here and not by the service:**
- `maxDurationSec`: only the last this-many seconds of the snapshot are folded and sent.
- `maxDiskMB`: the recorder's ring buffer is a quarter of it in total (a snapshot is about the ring's size); a snapshot over it is
  discarded; the trace folder is kept inside it (folded files of submitted or skipped traces and leftover snapshots go first, then
  the oldest queued ones, which fail with `dropped: over the maxDiskMB limit`).
- `maxOverheadPercent`: the recorder's own CPU, read from `/proc/<pid>/stat` over 60 s, as a share of all CPUs. Over it, the recorder is stopped for
  30 minutes and `status` says why. This sees only the recorder process; `--overwrite` makes it write nothing until a snapshot, so it is
  near zero, and the cost of sampling itself (kernel interrupts, charged to whatever was running) is not in it. **Measured on rob-dev
  (8 vCPU KVM guest, 8 CPU-bound workers, `-F 99 -a -g`, 8 alternating rounds): the slowdown of the workload could not be told from
  run-to-run noise** (baseline 2.16 to 2.69 s, median 2.38 s; with the recorder, median 2.23 s on the `cycles` event and 2.31 s on `cpu-clock`; an earlier six-round
  run read +10% and was noise too). The 2% target is therefore **unverified**; it needs a quiet machine and many more rounds.
- `cooldownMin`, `dailyCap`: above.

It needs `perf` and, to sample the whole system, root (the system install): `kernel.perf_event_paranoid` is 4 on Ubuntu 24.04.
`run --once` (the cron fallback) does not trace: a recorder has to stay running. A trace is a row of `kind` `trace` in the same queue, so
`list`, `approve`, `skip` and `retry` work on it, and a held trace is logged with the exact command.

```sh
dumptriage-agent trace-capture        # ask the running daemon for a manual capture now (cooldown and cap still apply)
dumptriage-agent status               # queue counts, then the recorder's state, last error and measured overhead
```

**What the manifest says:** the trigger and the value that fired it; the capture window (it ends when the trigger fired, and
starts where the folded samples start: the header's `sample duration` is 0 in a `--switch-output` dump); the exact recorder command with home
directories replaced; perf's version; the kernel; an agent version; and a random per-install `hostToken`, kept in the queue, not derived
from the hostname. The hostname field is sent as for dumps (`sendHostname`), and the service keeps only a keyed hash.

**Redaction on the host:** home directories become `<user>` in every frame name and in the command line. Command-line arguments of other
processes are never read.

### The perfetto backend (dumptriage-tools#28)

`"backend": "perfetto"` runs [`tracebox`](https://perfetto.dev/docs/) (Perfetto's all-in-one recorder) instead of perf, for what an on-CPU
profile cannot show: scheduling, wakeups, why a thread was off the CPU, run-queue latency, CPU frequency, interrupts and block I/O,
beside CPU samples with kernel and DWARF-unwound user stacks. It is opt-in (perf stays the default) and needs more of the host.

```sh
sudo linux/dumptriage-agent/installer/install.sh --with-perfetto     # adds tracebox, pinned and checksummed (installer/perfetto.pin)
```

The installer downloads the release named in [`installer/perfetto.pin`](dumptriage-agent/installer/perfetto.pin) (Perfetto v58.2, amd64 and arm64) over HTTPS and
refuses to install it unless its SHA-256 is the one kept in that file. The agent runs `tracebox --txt -c <config> -o <file>` with the shipped
[`perfetto/flight-recorder.pbtx`](perfetto/flight-recorder.pbtx), filled in with the ring-buffer size and **one `pi_<name>` trigger per configured trigger
name** (and `pi_manual`), all `STOP_TRACING`. The session records into the ring buffer and writes nothing until a trigger: the agent then runs
`tracebox trigger_perfetto pi_<name>` (the ticket's draft said `--trigger`; the real applet is `trigger_perfetto`), waits for the file, writes
`manifest.json` (`traceType` `perfetto`, `tool.name` `perfetto`) and queues the **binary** like any trace (held for approval by default, uploaded as
`application/octet-stream`). It is never parsed on the host or in the backend, only on a worker.

- **Limits.** `maxDiskMB`: the ring buffer is at most half of it and at most 48 MiB (the service takes 64 MiB); a trace over the budget is discarded.
  `maxDurationSec`: the buffer is sized from the byte rate the last capture measured, to hold about that many seconds with a quarter to spare (the first
  session guesses 32 MiB). `maxOverheadPercent`: the CPU of tracebox **and the services it starts** (`traced`, `traced_probes`, `traced_perf`), as for perf.
  `cooldownMin`, `dailyCap`: as for perf.
- **A capture ends the session** (the trace is written when it stops), so a new one starts at once and has no history for its first seconds. A session nobody
  triggered ends after 24 hours; its ring buffer is thrown away and a new one starts.
- **It falls back to perf, with a log line saying why,** if `tracebox` is not installed (looked for in `DUMPTRIAGE_TRACEBOX`, next to the agent,
  `/usr/local/lib/dumptriage/`, `~/.local/lib/dumptriage/` and `PATH`), the config is missing, or tracefs (`/sys/kernel/tracing` or `/sys/kernel/debug/tracing`) is not readable.
- **Privacy: a Perfetto trace holds every process's command line, arguments included.** `linux.process_stats` records them (954 process rows of the agent's real
  trace had a command line, e.g. `--config.file=...` and `-H fd://`) and the host cannot redact a binary. That is why a trace is held for approval by default, and why the service drops arguments
  when it parses one (website#2001). Do not set `AlwaysSubmit` on a host whose command lines carry secrets.

**Measured on rob-dev (Ubuntu 24.04, kernel 6.8.0-147, 8 vCPU KVM guest, tracebox v58.2, 2026-10-22), one run each unless said:**

| What | Result |
|---|---|
| A trigger-stopped session writes a valid trace | yes, with `ulimit -n` raised: `trace_processor_shell` reads it (352,230 `sched_slice` rows, 728,924 `thread_state`, 10,378 to 11,521 `perf_sample`, 873 processes, 2,895 threads) |
| Trace size per minute on a busy host | 15.99 MB for 14.75 s and 18.01 MB for 15 s: **about 1.1 to 1.2 MB/s, 65 to 72 MB a minute** with the shipped config and the CI runners and containers of a busy host; a 48 MiB ring holds about 40 s here |
| Recorder CPU, own processes only | 2.45 CPU-seconds in 28 s on 8 CPUs with six busy loops: **1.1% of the machine** (below the 2% target; this sees only tracebox and its children, not the kernel's cost of sampling, which is charged to whatever was running, so the target is not verified) |
| `linux.perf` with all processes on a default `ulimit -n` (1024) | `proc_descriptors.cc: Failed to open /proc/<pid>/maps (errno: 24, Too many open files)` for most processes: the agent raises the limit for tracebox to 65536 |
| Unix socket path | `traced` refuses a path longer than about 100 bytes (`errno 36, File name too long`), which is what a state-directory path usually is: the agent uses a `0700` directory with a short path (`/run/dt-XXXXXXXX`; the sockets themselves are world-writable, so the directory is what keeps other users off) |
| Import warning | `perf_samples_skipped: 115` ("a process will be absent from the trace, no data loss for the rest") |
| Privileges | run as root; `perf_event_paranoid` is 4 on this host and does not matter to root. Non-root, containers, Debian 12 and RHEL 9 were **not** run |

Not verified: tracefs on Debian 12, RHEL 9, Ubuntu 22.04 and container hosts; the `perf_event_paranoid` value `linux.perf` needs for a non-root agent;
the real ring-buffer overhead on a quiet host (the A/B on a shared VM is inside its noise, as for perf).

**Checked on real perf** (a one-snapshot capture per tool, 2026-10-21, host kernel 6.8.0-147, containers `--privileged`): Ubuntu 22.04
(perf 5.15.216), Ubuntu 24.04 (6.8.12), Debian 12 (6.1.190) and Rocky Linux 9, the RHEL 9 family (5.14.0-687.58.1.el9_8). On all four,
`perf record -F 99 -a -g --overwrite --switch-output=signal` writes `<-o>.<timestamp>` on each `SIGUSR2` and once more on exit, and
`perf script -f --header` reads it. Differences the code handles: the event is `cycles` on 5.15 and 6.1 and `cycles:P` on 5.14 (EL9)
and 6.8; the "Dump" line names the file as it was given to `-o` (a full path when absolute); the header's `sample duration` and
`time of first/last sample` are 0 in every one; a snapshot holds what was recorded since the previous one. Ubuntu's `perf`
wrapper refuses to run when the package is for another kernel (`perf not found for kernel ...`): its real binary is under
`/usr/lib/linux-tools-<version>/`. Folding is pinned to the three distros' real output in `tests/fixtures/`.

## Differences from the Windows agent

- No tray popup: approval is the CLI above. A desktop notification is a follow-up.
- The API key is a 0600 file (`api-key` in the config directory); Windows encrypts it with machine DPAPI.
- `batching.mode` `Idle` is not available (no idle-time source); it uploads immediately and logs a warning.
- Polling instead of file-system events (every `pollSeconds`); the stability check makes this equivalent.

## Tests

```sh
python3 -m unittest discover -s linux/dumptriage-agent/tests     # sniffing, policy, queue, upload (mock server), tracing
bash linux/tests/test_configure_tracing.sh                       # configure-tracing.sh with a fake perf
```

The tracing tests use a fake `perf` (`tests/fake_perf.py`), a fake `/proc` and a fake clock; the schema checks need `pip install jsonschema` and are skipped without it.
