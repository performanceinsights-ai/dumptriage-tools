# macOS: `dumptriage-collect.sh`

Sends macOS crash reports, kernel panics and spin/hang reports to Dump Triage, and (website#1999) timed performance
captures. Plain bash 3.2 and the tools macOS ships; nothing to install. It asks before sending anything.

```
dumptriage-collect.sh [--days N] [--all] [--spindump PID|NAME] [--dry-run] [--dir DIR]...   reports
dumptriage-collect.sh --profile [S] | --sample PID|NAME [S] | --xctrace [S]                  one performance capture
dumptriage-collect.sh --watch-once | --send-held                                             watching
```

## A performance capture

| Option | What it does |
|---|---|
| `--profile [S]` | system-wide `spindump` for S seconds (30 to 120, default 30: spindump samples every 10 ms, and the service needs about 2,000 on-CPU samples); sudo asks for your password |
| `--sample X [S]` | `sample` of one process, by pid or name, for S seconds (15 to 120, default 30; it samples every 1 ms) |
| `--xctrace [S]` | Instruments' Time Profiler over every process for S seconds (15 to 120, default 30), then `xctrace export` of the time-profile table and a fold to `process;frame;...;frame count` **on this Mac**. Only the folded stacks are sent: the `.trace` bundle and the XML are removed. Needs Xcode: the Command Line Tools do not include `xctrace` (checked on GitHub's macOS 14, 15 and 26 runners). Needs Xcode 16 or later: Xcode 15's `xctrace` left 98% of frames unnamed on macOS 14, where Xcode 16.2's named 70% and the app's own functions (#46). With Xcode 15 or the Command Line Tools selected, the script uses the newest Xcode release of 16 or later in `/Applications` (another folder: set `DUMPTRIAGE_XCODE_APPS`), or stops and says so. |

Each shows what will be sent, asks, then uploads to `POST /api/v1/traces` with a `manifest.json` (schema in
`schemas/trace-manifest.schema.json`; a `hostToken` that is random, kept in `~/.config/dumptriage/host-token`, and not
derived from the host name).

## Watching

Off until `~/.config/dumptriage/tracing.json` says so. The config is the shared one (`schemas/tracing-config.schema.json`):

```json
{
  "schemaVersion": 1,
  "enabled": true,
  "backend": "spindump",
  "triggers": [
    {"name": "cpu-high", "type": "cpuPercent", "threshold": 90, "sustainSec": 60},
    {"name": "slack-busy", "type": "cpuPercent", "process": "Slack Helper", "threshold": 80, "sustainSec": 30},
    {"name": "hang", "type": "hangEvent"}
  ],
  "limits": {"maxDurationSec": 30, "maxDiskMB": 512, "cooldownMin": 60, "dailyCap": 4}
}
```

`--watch-once` checks each trigger once and remembers a run above its threshold between calls, so `sustainSec` works across
calls. When one is met, outside the cooldown and under the daily cap (the rule every platform shares, replayed from
`tests/fixtures/tracing/triggers.json`), it takes a capture with `backend` (`spindump` or `xctrace`; always 30 s: a `maxDurationSec` under 30
is reported and not obeyed, since a shorter profile is too thin to judge; not kept if over `maxDiskMB`) and holds it in `~/.config/dumptriage/tracing-state/spool/`. **Nothing is ever sent from here**: you send what is held with
`dumptriage-collect.sh --send-held`, which lists it, asks, and removes what was sent. `submission` in the config is not read on macOS:
a root daemon cannot read your keychain, so every capture is held.

Readings: `cpuPercent` (the whole Mac, or `process` alone, from `top`), `loadPerCpu` (the one-minute load over `hw.ncpu`) and
`process` come from this Mac; `hangEvent` is a new `.hang` or `.spin` report in the DiagnosticReports folders; `schedule` is a
baseline every `intervalMin` minutes. `psiSomeAvg10` and `diskLatencyMs` have no source here and never fire.

### Run it every minute (launchd)

```sh
sudo cp dumptriage-collect.sh /usr/local/bin/dumptriage-collect.sh && sudo chmod 755 /usr/local/bin/dumptriage-collect.sh
sed "s#__SCRIPT__#/usr/local/bin/dumptriage-collect.sh#; s#__HOME__#$HOME#g" com.performanceinsights.dumptriage-trace.plist |
  sudo tee /Library/LaunchDaemons/com.performanceinsights.dumptriage-trace.plist > /dev/null
sudo launchctl bootstrap system /Library/LaunchDaemons/com.performanceinsights.dumptriage-trace.plist
```

Remove it with `sudo launchctl bootout system/com.performanceinsights.dumptriage-trace` and delete the plist. Output goes to
`/var/log/dumptriage-trace.log`. The daemon runs as root (spindump and xctrace need it) and hands the spool to the owner of `$HOME`.

## Tests

`bash macos/tests/test_collect.sh` runs on Linux or a Mac with fakes for the tools, against real captures in `macos/tests/fixtures/`.
`.github/workflows/macos-trace-capture.yml` runs the script itself with its real tools on macOS 14, 15 and 26.
