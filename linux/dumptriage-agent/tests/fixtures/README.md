# Linux agent tracing fixtures

Real `perf script --header` output of one `--switch-output=signal` snapshot per perf version, captured 2026-10-21 by
`perf record -F 99 -a -g --overwrite --switch-output=signal` (then `kill -USR2`) while four CPU-bound `python3` loops ran, in a
`--privileged` container of each distro on a 6.8.0-147 host (so the kernel is the same; the perf tool and its output are the distro's).
Our own captures, covered by the repository's MIT licence; nothing third-party.

| Fixture | Distro | perf | Event | Samples kept |
|---|---|---|---|---|
| `perf-script-ubuntu2204-perf5.15.txt` | Ubuntu 22.04.5 | 5.15.216 | `cycles` | every 12th of 1555: 130 |
| `perf-script-rocky9-perf5.14.txt` | Rocky Linux 9.3 (the RHEL 9 family) | 5.14.0-687.58.1.el9_8 | `cycles:P` | every 10th of 1404: 141 |
| `perf-script-debian12-perf6.1.txt` | Debian 12 | 6.1.190 | `cycles` | every 14th of 1899: 136 |

`trim_perf_script.py` made them: it keeps the header except the host's `bpf_prog_info` lines, then every Nth sample unchanged. The
container ids in the headers are the hostnames of throwaway containers. Not kept here: Ubuntu 24.04 (6.8.12), which behaves like Rocky 9
for everything the agent reads and is the host the agent was run on.
