# macOS report fixtures

Reports `../test_collect.sh` copies into a fake DiagnosticReports folder. Copied from the DumpTriage service's own fixtures (2026-10-19); each real one is kept under its upstream licence, whose full text (copyright and permission notice) is in `LICENSES/<fixture>.<licence>.txt`. The repository's MIT licence does not cover them.

| Fixture | What it is | Source | Licence | Edits |
|---|---|---|---|---|
| `panic-tahoe-intel-iclgraphics.panic` | JSON kernel panic (bug_type 210), macOS 26.6 (25G72) on an Intel machine reporting MacBookPro16,2 (a Dell Latitude 7320 running macOS): `macOSPanicString` with a symbolicated backtrace, `"Unexpected context status buffer entry" @IGHardwareCommandStreamer.cpp:71` raised in `com.apple.driver.AppleIntelICLGraphics`, frames without `+ 0x` offsets | https://raw.githubusercontent.com/holdmysocks/latitude-7320-detachable-hackintosh/HEAD/research/data/layer4/panic-GA2-context-status-buffer.txt (blob `eda57bb1b8675ca4f4ab8315d43d4ba75c14cddc`, sha256 `af8a84d059074fcc9a66ed1fdbd5522a1dc3cd84439ef9f34efc68921d13489b`) | MIT, © 2026 holdmysocks | `incident_id` zeroed; renamed to `.panic` (what macOS names it) |
| `spindump-catalina-slack.txt` | spindump text, Mac OS X 10.15.7 (19H1030), Report Version 29, Slack 4.19 (Electron 13) sampled 22.56 s: `Thread name "..."` labels, `(image + offset)` frames, bundle-id Binary Images | https://raw.githubusercontent.com/electron/symbolicate-mac/HEAD/spec/fixtures/spindump.txt (blob `9f1d872fab4982d950373abc9fff74695ae3b270`, sha256 `e10d8cf6be66540acb83918815d7cf36661b8c789781a21fd8b0c86027fe422c`) | ISC, © Contributors to the Electron project | none: no user, host or machine identifiers in it |

## Constructed (written for DumpTriage, 2026-10-17)

| Fixture | Shape |
|---|---|
| `crash-constructed-monterey-null-call.ips` | `.ips` app crash: a one-line header (bug_type 309, macOS 12.1 (21C52)) then the JSON body. `/usr/local/bin/napper` (fictional) dies with EXC_BAD_ACCESS (SIGSEGV) `KERN_INVALID_ADDRESS at 0x0000000000000000`; frame 0 is at offset 0 of a `usedImages` entry with no name or path (a call through a NULL function pointer), then `libsystem_c.dylib!nanosleep`, the tool itself (unsymbolicated) and `dyld!start`; a second, untriggered thread; incident, crash-reporter key and sleep/wake UUIDs and a responsible process the digest must leave out |

## Real, from GitHub's macOS runners (2026-10-23, dumptriage-tools#41's workflow, run 39907335668)

Captures of a CPU burner (`macos/tests/burn.c`) and the runner's own processes, made by `.github/workflows/macos-trace-capture.yml`. No user or host identifiers: the runner's user is `runner`. They are this repository's own output of Apple's tools, under the repository's licence.

| Fixture | What it is | Edits |
|---|---|---|
| `xctrace-time-profile-macos26.xml` | `xctrace export` of the `time-profile` table, macOS 26.6.2, Xcode 26.6: the stack is a `<tagged-backtrace>`; frames named, burner frames included | the header and the first 400 of 12,915 rows, closed |
| `xctrace-time-profile-macos15.xml` | the same on macOS 15.7.9, Xcode 16.4: the stack is a `<backtrace>` in the row | the first 400 of 11,913 rows |
| `xctrace-time-profile-macos14.xml` | the same on macOS 14.8.9, Xcode 15.4: as macOS 15, but the unstripped burner's own frames are addresses, not names | the first 300 of 30,519 rows |
| `sample-macos26.txt` | `sample` of the burner for 10 s at 1 ms, macOS 26.6.2 | none |

`test_collect.sh` checks the awk fold of each export against an independent XML reader that resolves `id`/`ref`, on the full exports and the trimmed ones.
