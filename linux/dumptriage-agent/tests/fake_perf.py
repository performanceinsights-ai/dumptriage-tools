#!/usr/bin/env python3
"""A stand-in for `perf` in the agent's tracing tests: the three invocations the agent makes.

record  runs until SIGINT; each SIGUSR2 writes `<-o>.<n>` and prints perf's own "Dump" line, as
        `--switch-output=signal` does (checked against perf 5.14 to 6.8). FAKE_PERF_FAIL makes it exit at once.
script  prints the file named by FAKE_PERF_SCRIPT (real `perf script --header` output kept under fixtures/).
--version
"""
import os
import signal
import sys
import time

args = sys.argv[1:]
if args[:1] == ["--version"]:
    print("perf version 6.8.12")
    sys.exit(0)
if args[:1] == ["script"]:
    if os.environ.get("FAKE_PERF_STDERR_KB"):  # a chatty perf: more than a pipe holds
        sys.stderr.write("warning: ignoring something\n" * (int(os.environ["FAKE_PERF_STDERR_KB"]) * 1024 // 28))
        sys.stderr.flush()
    sys.stdout.write(open(os.environ["FAKE_PERF_SCRIPT"], encoding="utf-8").read())
    sys.exit(int(os.environ.get("FAKE_PERF_SCRIPT_EXIT", "0")))
if args[:1] == ["record"]:
    if os.environ.get("FAKE_PERF_FAIL"):
        print("Permission error: perf_event_paranoid is 4", file=sys.stderr, flush=True)
        sys.exit(1)
    out = args[args.index("-o") + 1]
    count = [0]

    def dump(*_):
        count[0] += 1
        name = f"{out}.2026102112{count[0]:04d}"
        with open(name, "wb") as f:
            f.write(b"PERFILE2" + b"x" * 1024)
        print(f"[ perf record: Dump {name} ]", file=sys.stderr, flush=True)  # the path as given to -o, as perf does

    signal.signal(signal.SIGUSR2, dump)
    signal.signal(signal.SIGINT, lambda *_: sys.exit(0))
    print("[ perf record: fake recorder running ]", file=sys.stderr, flush=True)
    while True:
        time.sleep(0.05)
sys.exit(2)
