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


Files older than the agent's first run are left alone, so a fresh install doesn't upload every old core
already in the watched folders; a core written while the agent was stopped is still picked up.
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

## Differences from the Windows agent

- No tray popup: approval is the CLI above. A desktop notification is a follow-up.
- The API key is a 0600 file (`api-key` in the config directory); Windows encrypts it with machine DPAPI.
- `batching.mode` `Idle` is not available (no idle-time source); it uploads immediately and logs a warning.
- Polling instead of file-system events (every `pollSeconds`); the stability check makes this equivalent.

## Tests

```sh
python3 -m unittest discover -s linux/dumptriage-agent/tests     # sniffing, policy, queue, upload (mock server)
```
