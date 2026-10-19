# dumptriage-tools

Scripts and agents for capturing crash dumps on Windows and Linux (and crash reports on macOS) and sending them to
[DumpTriage](https://performanceinsights.ai/), which analyses crash dumps with AI and returns a root-cause
report: Windows BSOD memory dumps, minidumps (.dmp) and Linux core dumps.

The capture scripts work on their own. You don't need DumpTriage to use them: they only make sure a crash
leaves a dump you can open in WinDbg, gdb or crash.

## Capture configuration

| Script | What it does |
|---|---|
| [`windows/Set-WerLocalDumps.ps1`](windows/Set-WerLocalDumps.ps1) | Windows Error Reporting LocalDumps: full user-mode dumps of crashing processes to a folder you choose |
| [`windows/Set-WerLiveKernelReports.ps1`](windows/Set-WerLiveKernelReports.ps1) | Live kernel reports: kernel dumps Windows takes while it keeps running (for example after a driver timeout), and how often it may take them |
| [`linux/configure-dumps.sh`](linux/configure-dumps.sh) | Raw ELF process cores, unlimited core size, retention, and optionally kdump for kernel crashes |

## Collection scripts

Send dumps or reports without installing an agent.

| Script | What it does |
|---|---|
| [`macos/dumptriage-collect.sh`](macos/dumptriage-collect.sh) | Lists the last week's macOS crash reports, kernel panics and spin/hang reports, lets you pick, and asks before sending. `--spindump` samples a hanging app now |
| [`rmm/ninjaone/Invoke-DumpTriageCollect.ps1`](rmm/ninjaone/Invoke-DumpTriageCollect.ps1) | A NinjaOne Script Library script: sends each device's new minidumps on a schedule and writes the report link into a custom field ([setup](rmm/ninjaone/README.md)) |

## Agents

Background agents that watch for new dumps, decide by policy whether to submit them (always, ask first,
notify only, or rules), and upload them to the DumpTriage API.

- [`windows/dumptriage-agent/`](windows/dumptriage-agent/): Windows service and tray app (C#, .NET 8)
- [`linux/dumptriage-agent/`](linux/dumptriage-agent/): a single Python 3 file using only the standard library. It runs as a systemd service, a systemd user service, or from cron

An agent needs an API key from [DumpTriage](https://performanceinsights.ai/); the
[API docs](https://performanceinsights.ai/api-docs/) describe the endpoints it uses.

## Downloads

Every [release](https://github.com/performanceinsights-ai/dumptriage-tools/releases/latest) carries each script
on its own, the Windows agent built (`dumptriage-agent-windows-x64.zip`: agent, tray app and installer), the Linux
agent with `configure-dumps.sh` (`dumptriage-agent-linux.tar.gz`), and `SHA256SUMS`. The newest of each is always at
`https://github.com/performanceinsights-ai/dumptriage-tools/releases/latest/download/<file>`, and listed on
[performanceinsights.ai/downloads](https://performanceinsights.ai/downloads/). A `v*` tag publishes a release
(`.github/workflows/release.yml`).

## Tests

```sh
python3 -m unittest discover -s linux/dumptriage-agent/tests
dotnet test windows/dumptriage-agent/tests/DumpTriage.Common.Tests
dotnet test windows/dumptriage-agent/tests/DumpTriageAgent.Tests
pwsh -NoProfile -File windows/dumptriage-agent/installer/tests/Test-Install.ps1
bash macos/tests/test_collect.sh
pwsh -NoProfile -File rmm/ninjaone/tests/Run-Tests.ps1
```

## Security

Report vulnerabilities to hello@performanceinsights.ai (subject "Security"), not in a public issue.

## License

[MIT](LICENSE)
