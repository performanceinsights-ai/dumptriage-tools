# dumptriage-tools

Scripts and agents for capturing crash dumps on Windows and Linux and sending them to
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

## Agents

Background agents that watch for new dumps, decide by policy whether to submit them (always, ask first,
notify only, or rules), and upload them to the DumpTriage API.

- [`windows/dumptriage-agent/`](windows/dumptriage-agent/): Windows service and tray app (C#, .NET 8)
- [`linux/dumptriage-agent/`](linux/dumptriage-agent/): a single Python 3 file using only the standard library. It runs as a systemd service, a systemd user service, or from cron

An agent needs an API key from [DumpTriage](https://performanceinsights.ai/); the
[API docs](https://performanceinsights.ai/api-docs/) describe the endpoints it uses.

## Tests

```sh
python3 -m unittest discover -s linux/dumptriage-agent/tests
dotnet test windows/dumptriage-agent/tests/DumpTriage.Common.Tests
```

## Security

Report vulnerabilities to hello@performanceinsights.ai (subject "Security"), not in a public issue.

## License

[MIT](LICENSE)
