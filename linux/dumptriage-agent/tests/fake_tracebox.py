#!/usr/bin/env python3
"""A stand-in for `tracebox` in the agent's perfetto tests.

--version             Perfetto's own banner.
trigger_perfetto NAME writes <PERFETTO_PRODUCER_SOCK_NAME's directory>/trigger with the name (the real one sends it over the socket).
--txt -c CFG -o OUT   the autostart session: runs until a trigger file appears, then writes OUT (FAKE_TRACEBOX_BYTES bytes, default 4096)
                      and exits 0; with FAKE_TRACEBOX_FAIL it exits 1 at once; with FAKE_TRACEBOX_TIMEOUT it ends by itself after
                      that many seconds without a trigger (the real one writes the ring buffer then too) and exits 0.
It copies the config it was given to <OUT>.config so a test can read what the agent rendered.
"""
import os
import sys
import time
from pathlib import Path

args = sys.argv[1:]
if args[:1] == ["--version"]:
    print("Perfetto v58.2-add693d8b (add693d8b338ba9599dbcbc3e300b1ab8c000897)")
    sys.exit(0)
sockdir = Path(os.environ.get("PERFETTO_PRODUCER_SOCK_NAME", "/nonexistent/p")).parent
if args[:1] == ["trigger_perfetto"]:
    if not sockdir.is_dir() or not (sockdir / "session").exists():
        print("Failed to connect to the traced service", file=sys.stderr)
        sys.exit(1)
    (sockdir / "trigger").write_text(args[1])
    sys.exit(0)
if "-c" in args:
    if os.environ.get("FAKE_TRACEBOX_FAIL"):
        print("Failed to start traced: errno 36", file=sys.stderr, flush=True)
        sys.exit(1)
    out = Path(args[args.index("-o") + 1])
    Path(str(out) + ".config").write_text(Path(args[args.index("-c") + 1]).read_text())
    (sockdir / "session").write_text(str(os.getpid()))
    if os.environ.get("FAKE_TRACEBOX_CHILD"):  # the real tracebox starts traced, traced_probes and traced_perf
        import subprocess

        child = subprocess.Popen(["sleep", "300"])
        Path(str(out) + ".child").write_text(str(child.pid))
    limit = float(os.environ["FAKE_TRACEBOX_TIMEOUT"]) if os.environ.get("FAKE_TRACEBOX_TIMEOUT") else None
    started = time.time()
    print("[0.1] fake tracebox: session started", file=sys.stderr, flush=True)
    while not (sockdir / "trigger").exists():
        if limit is not None and time.time() - started > limit:
            out.write_bytes(b"P" * 16)
            sys.exit(0)
        time.sleep(0.05)
    time.sleep(0.2)  # the stop delay
    out.write_bytes(b"\x0a\x0bPERFETTO-TRACE" + b"x" * int(os.environ.get("FAKE_TRACEBOX_BYTES", "4096")))
    sys.exit(0)
sys.exit(2)
