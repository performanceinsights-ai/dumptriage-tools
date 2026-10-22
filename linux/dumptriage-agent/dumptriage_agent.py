#!/usr/bin/env python3
"""DumpTriage Linux agent.

Watches directories for crash dumps (ELF process cores, kdump/vmcore), decides
whether to submit each one to DumpTriage from a configurable policy, and
uploads it to POST /api/v1/dumps. The Linux counterpart of the Windows
DumpTriageAgent service: same config model, same queue states, same upload
request. Python 3 standard library only, so installing it is copying one file.

Run it as a daemon (`run`, what the systemd unit does), or as a bounded pass
(`run --once`, what the cron fallback does). The other subcommands operate on
the local queue: status, list, approve, skip, retry, set-api-key.

Performance tracing (website#1999, off by default; see the `tracing` config section and the README): a perf
flight recorder runs under the daemon, a trigger on /proc metrics takes a snapshot, the snapshot is folded to
stacks on the host and queued like a dump (held for approval by default), then uploaded to POST /api/v1/traces.
"""
import argparse
import errno
import fnmatch
import hashlib
import http.client
import json
import logging
import lzma
import os
import platform
import re
import resource
import shutil
import signal
import socket
import sqlite3
import stat
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.parse
import uuid
from collections import Counter
from datetime import datetime, timedelta, timezone
from pathlib import Path

log = logging.getLogger("dumptriage")

AGENT_VERSION = "0.3.0"

# --- dump sniffing -----------------------------------------------------------
# Mirrors the backend's detect_dump_type (service/backend/app/services/dump_sniff.py),
# Linux part, so rule-based policy can match on type before upload. The server
# stays the source of truth; the type is never sent to the API.

USER_CORE, KERNEL_DUMP = "LinuxUserCore", "LinuxKernel"
HEADER_BYTES = 64 * 1024
_ET_CORE, _PT_LOAD, _PT_NOTE = 4, 1, 4
_KERNEL_SPACE_START = 0xFFFF_0000_0000_0000
_VSYSCALL_PAGE = 0xFFFF_FFFF_FF60_0000


def _program_headers(h, endian, is_64):
    if is_64:
        if len(h) < 64:
            return
        phoff = struct.unpack_from(endian + "Q", h, 32)[0]
        phentsize, phnum = struct.unpack_from(endian + "HH", h, 54)
        fields, minimum = endian + "IIQQQQ", 40
    else:
        if len(h) < 52:
            return
        phoff = struct.unpack_from(endian + "I", h, 28)[0]
        phentsize, phnum = struct.unpack_from(endian + "HH", h, 42)
        fields, minimum = endian + "IIIII", 20
    if phentsize < minimum:
        return
    for i in range(phnum):
        start = phoff + i * phentsize
        if start + minimum > len(h):
            return
        v = struct.unpack_from(fields, h, start)
        yield (v[0], v[2], v[3], v[5]) if is_64 else (v[0], v[1], v[2], v[4])


def _has_kernel_markers(h, endian, is_64):
    for p_type, offset, vaddr, filesz in _program_headers(h, endian, is_64):
        if p_type == _PT_LOAD and is_64 and filesz > 0 and vaddr >= _KERNEL_SPACE_START and vaddr != _VSYSCALL_PAGE:
            return True
        if p_type == _PT_NOTE and b"VMCOREINFO" in h[offset:offset + filesz]:
            return True
    return False


def detect_dump_type(header):
    """USER_CORE, KERNEL_DUMP, or None when the bytes are not a Linux dump."""
    if header[:8] in (b"KDUMP   ", b"DISKDUMP") or header[:12] == b"makedumpfile":
        return KERNEL_DUMP
    if header[:4] == b"\x7fELF" and len(header) >= 20 and header[5] in (1, 2):
        endian = "<" if header[5] == 1 else ">"
        if struct.unpack_from(endian + "H", header, 16)[0] != _ET_CORE:
            return None
        return KERNEL_DUMP if _has_kernel_markers(header, endian, header[4] == 2) else USER_CORE
    return None


# --- compressed cores (systemd-coredump writes .zst / .xz / .lz4) ------------

# Both read the compressed stream from stdin, so they only ever see the file the agent opened.
_DECOMPRESSORS = {".zst": ["zstd", "-dc"], ".lz4": ["lz4", "-dc"]}


def compression_of(path):
    ext = Path(path).suffix.lower()
    return ext if ext in (".zst", ".lz4", ".xz") else None


def read_header(f, compression=None, n=HEADER_BYTES):
    """The first n (decompressed) bytes of the open file f."""
    if compression == ".xz":
        with lzma.open(f, "rb") as x:
            return x.read(n)
    if compression:
        proc = subprocess.Popen(_DECOMPRESSORS[compression], stdin=f, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        try:
            return proc.stdout.read(n)
        finally:
            proc.kill()
            proc.wait()
    return f.read(n)


def decompress_to(f, compression, dest):
    if compression == ".xz":
        with lzma.open(f, "rb") as src, open(dest, "wb") as out:
            shutil.copyfileobj(src, out, 1 << 20)
        return
    with open(dest, "wb") as out:
        subprocess.run(_DECOMPRESSORS[compression], stdin=f, stdout=out, check=True)


class NotWatched(OSError):
    """The path is not under a watched path the agent trusts (any more)."""


def open_dir_safely(path, _depth=0):
    """A file descriptor for the directory `path`, walked from / one component at a time.

    /var/crash is world-writable, so another user could create /var/crash/dumptriage before
    configure-dumps.sh does. As a real folder that is harmless: it only holds files that user could
    write anyway, and open_dump reads nothing through a symlink or hard link below it. A symlink
    could point anywhere, so one is followed only if it belongs to root or the agent's user and sits
    in a folder that also does and that is not world-writable, or is sticky (then only they can
    replace the link). Group write counts as theirs: granting a group is the owner's choice, and
    user-private groups make ~ group-writable on some distributions. The walk then restarts at the
    link's target, which is checked the same way, and `..` is resolved like the kernel does. Every
    real folder is opened with O_PATH | O_NOFOLLOW relative to the previous one (search permission
    is enough), so nothing can be swapped for a symlink between a check and its use. Raises
    NotWatched for a refused symlink."""
    if _depth > 40:
        raise OSError(errno.ELOOP, "too many symlinks", path)
    ok = (0, os.geteuid())
    parts = [p for p in path.split("/") if p not in ("", ".")]
    fd, cur = os.open("/", os.O_PATH | os.O_DIRECTORY), "/"
    try:
        for i, part in enumerate(parts):
            if part == "..":
                cur = os.path.dirname(cur)
                next_fd = open_dir_safely(cur, _depth + 1)
            else:
                st = os.stat(part, dir_fd=fd, follow_symlinks=False)
                if stat.S_ISLNK(st.st_mode):
                    parent = os.fstat(fd)
                    if (st.st_uid not in ok or parent.st_uid not in ok
                            or (parent.st_mode & stat.S_IWOTH and not parent.st_mode & stat.S_ISVTX)):
                        raise NotWatched(errno.EPERM, "goes through a symlink another user could have planted",
                                         os.path.join(cur, part))
                    target = os.readlink(part, dir_fd=fd)
                    rest = "/".join(parts[i + 1:])
                    target = target if target.startswith("/") else os.path.join(cur, target)
                    os.close(fd)
                    fd = None
                    return open_dir_safely(os.path.join(target, rest) if rest else target, _depth + 1)
                next_fd = os.open(part, os.O_PATH | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                cur = os.path.join(cur, part)
            os.close(fd)
            fd = next_fd
        result, fd = fd, None
        return result
    finally:
        if fd is not None:
            os.close(fd)


def root_path(root):
    """A configured watched path as the agent uses it: ~ expanded, made absolute, not normalised,
    so a `..` after a symlink means what it means to the kernel (open_dir_safely resolves it)."""
    root = os.path.expanduser(root)
    if not root.startswith("/"):
        root = os.path.join(os.getcwd(), root)
    return root.rstrip("/") or "/"


def trusted_root(root):
    """Whether open_dir_safely accepts a watched root (see there)."""
    try:
        os.close(open_dir_safely(root_path(root)))
        return True
    except OSError:
        return False


def open_dump(path, roots):
    """Open a dump for reading without following any symlink below its watched root (#4).

    Capture folders such as /var/crash/dumptriage are world-writable, and the system agent runs as
    root: a symlink planted there (to /proc/kcore, or another user's core) must not be read, uploaded
    or copied. Every component below the root is opened with O_NOFOLLOW, relative to its parent, so a
    directory swapped for a symlink after the scan is refused too, and the file must be a regular file
    with a single link. The root itself is opened by open_dir_safely."""
    roots = [root_path(r) for r in roots]
    root = max((r for r in roots if path.startswith(r.rstrip("/") + "/")), key=len, default=None)
    if root is None:
        raise NotWatched(errno.EPERM, "no longer under a watched path; add its folder back to watchedPaths and run `retry`", path)
    parts = path[len(root.rstrip("/")) + 1:].split("/")
    if any(part in ("", ".", "..") for part in parts):
        raise NotWatched(errno.EPERM, "not a plain path below its watched path", path)
    dir_fd = open_dir_safely(root)
    try:
        for part in parts[:-1]:
            next_fd = os.open(part, os.O_PATH | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=dir_fd)
            os.close(dir_fd)
            dir_fd = next_fd
        fd = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=dir_fd)
    finally:
        os.close(dir_fd)
    try:
        st = os.fstat(fd)
        if not stat.S_ISREG(st.st_mode) or st.st_nlink != 1:
            raise OSError(errno.EPERM, "not a regular file with a single link", path)
        return os.fdopen(fd, "rb")
    except BaseException:
        os.close(fd)
        raise


# --- config ------------------------------------------------------------------

DEFAULT_CONFIG = {
    "apiBaseUrl": "https://api.performanceinsights.ai",
    "sendHostname": True,
    "watchedPaths": ["/var/crash", "/var/lib/systemd/coredump"],
    "pollSeconds": 5,
    "stableSeconds": 10,
    "maxRetries": 10,
    "policy": {
        "mode": "AskFirst",  # AlwaysSubmit | AskFirst | NotifyOnly | RuleBased
        "rules": [],  # RuleBased only: maxSizeBytes, minSizeBytes, dumpType, pathGlob, action
        "defaultRuleAction": "Ask",
        "askTimeoutSeconds": 300,
        "askTimeoutDefaultAction": "Skip",  # fail closed: an unanswered ask never submits
        "batching": {"mode": "Immediate", "scheduledTimesLocal": [], "idleMinutes": 15},
    },
    "centralCopy": {"enabled": False, "destinationPath": None, "organizeByHostname": True},
    # Performance tracing (dumptriage-tools schemas/tracing-config.schema.json, #26): off until the host opts in.
    "tracing": {
        "enabled": False,
        "backend": "perf",
        "submission": "AskFirst",  # a trace is held for approval by default (PRD Q9)
        "triggers": [],
        "limits": {"maxDurationSec": 60, "maxDiskMB": 512, "maxOverheadPercent": 2, "cooldownMin": 60, "dailyCap": 4},
    },
}


def merge(base, over):
    out = dict(base)
    for k, v in over.items():
        out[k] = merge(base[k], v) if isinstance(v, dict) and isinstance(base.get(k), dict) else v
    return out


def load_config(path):
    if Path(path).exists():
        return merge(DEFAULT_CONFIG, json.loads(Path(path).read_text()))
    return DEFAULT_CONFIG


def default_dirs():
    if os.geteuid() == 0:
        return Path("/etc/dumptriage"), Path("/var/lib/dumptriage")
    home = Path.home()
    return (Path(os.environ.get("XDG_CONFIG_HOME", home / ".config")) / "dumptriage",
            Path(os.environ.get("XDG_STATE_HOME", home / ".local/state")) / "dumptriage")


# --- policy (pure, same decisions as PolicyEngine / BatchScheduler) -----------

AUTO, ASK, MANUAL, SKIP = "AutoSubmit", "RequiresApproval", "ManualOnly", "Skip"
_RULE_OUTCOME = {"submit": AUTO, "ask": ASK, "skip": SKIP}


def rule_matches(rule, dump, now):
    if rule.get("maxSizeBytes") is not None and dump["size_bytes"] > rule["maxSizeBytes"]:
        return False
    if rule.get("minSizeBytes") is not None and dump["size_bytes"] < rule["minSizeBytes"]:
        return False
    if rule.get("dumpType") and rule["dumpType"].lower() != dump["dump_type"].lower():
        return False
    if rule.get("pathGlob") and not fnmatch.fnmatchcase(dump["file_path"], rule["pathGlob"]):
        return False
    start, end = rule.get("businessHoursStart"), rule.get("businessHoursEnd")
    if start and end:
        t = now.strftime("%H:%M:%S")
        inside = start <= t <= end if start <= end else t >= start or t <= end
        if not inside:
            return False
    return True


def evaluate_policy(dump, policy, now):
    mode = policy["mode"]
    if mode == "AlwaysSubmit":
        return AUTO
    if mode == "AskFirst":
        return ASK
    if mode == "NotifyOnly":
        return MANUAL
    if mode == "RuleBased":
        for rule in policy["rules"]:
            if rule_matches(rule, dump, now):
                return _RULE_OUTCOME[rule["action"].lower()]
        return _RULE_OUTCOME[policy["defaultRuleAction"].lower()]
    raise ValueError(f"unknown policy mode {mode!r}")


def should_submit_now(batching, now, last_flush):
    """Timing only: whether an already-approved dump may be uploaded now.

    ScheduledTimeOfDay: only when a scheduled time has passed since the last flush.
    The caller records every flush, also one with nothing to send, so a dump queued
    after an empty slot waits for the next one (#9), and records a first run as a
    flush, so it waits for the first slot rather than uploading at once."""
    mode = batching["mode"]
    if mode == "ScheduledTimeOfDay":
        for t in batching["scheduledTimesLocal"]:
            hh, mm = (int(x) for x in t.split(":")[:2])
            at = now.replace(hour=hh, minute=mm, second=0, microsecond=0)
            if now >= at and last_flush < at:
                return True
        return False
    if mode == "Idle":
        log.warning("batching mode Idle is not supported on Linux; uploading immediately")
    return True


# --- queue -------------------------------------------------------------------

DETECTED, PENDING, QUEUED, SUBMITTING, SUBMITTED, FAILED, SKIPPED = (
    "Detected", "PendingApproval", "Queued", "Submitting", "Submitted", "Failed", "Skipped")


def utcnow():
    return datetime.now(timezone.utc)


def iso(dt):
    return dt.isoformat(timespec="seconds") if dt else None


class Queue:
    def __init__(self, path, clock=None):
        Path(path).parent.mkdir(parents=True, exist_ok=True)
        self.created = not Path(path).exists()  # a first run, not an upgrade (#17)
        self.db = sqlite3.connect(path, timeout=30)
        self.db.row_factory = sqlite3.Row
        self.db.executescript("""
            CREATE TABLE IF NOT EXISTS dumps (
                id TEXT PRIMARY KEY, file_path TEXT NOT NULL, size_bytes INTEGER NOT NULL,
                sha256 TEXT, dump_type TEXT NOT NULL, compression TEXT, state TEXT NOT NULL,
                detected_at_utc TEXT NOT NULL, ask_deadline_utc TEXT, next_attempt_utc TEXT,
                submitted_at_utc TEXT, retry_count INTEGER NOT NULL DEFAULT 0, last_error TEXT,
                central_copied_at_utc TEXT, central_copy_error TEXT);
            CREATE INDEX IF NOT EXISTS idx_dumps_state ON dumps(state);
            CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT);
        """)
        # A trace is a second kind of row in the same queue (website#1999): the same states, policy, approve/skip/retry
        # and upload retry. A queue from before it gets the two columns added; its rows are all dumps.
        columns = {r["name"] for r in self.db.execute("PRAGMA table_info(dumps)")}
        if "kind" not in columns:
            self.db.execute("ALTER TABLE dumps ADD COLUMN kind TEXT NOT NULL DEFAULT 'dump'")
        if "manifest" not in columns:
            self.db.execute("ALTER TABLE dumps ADD COLUMN manifest TEXT")
        self.db.commit()
        if self.created:
            # Recorded with the file itself, so a first run that dies before the agent starts
            # watching isn't later mistaken for an upgrade (which filters nothing, #17).
            self.set_meta("watching_since_utc", iso((clock or utcnow)()))

    def add(self, **row):
        cols = ", ".join(row)
        self.db.execute(f"INSERT OR IGNORE INTO dumps ({cols}) VALUES ({', '.join('?' * len(row))})", list(row.values()))
        self.db.commit()

    def update(self, dump_id, **fields):
        sets = ", ".join(f"{k} = ?" for k in fields)
        self.db.execute(f"UPDATE dumps SET {sets} WHERE id = ?", [*fields.values(), dump_id])
        self.db.commit()

    def get(self, dump_id):
        rows = self.db.execute("SELECT * FROM dumps WHERE id = ? OR id LIKE ?", (dump_id, dump_id + "%")).fetchall()
        return rows[0] if len(rows) == 1 else None

    def in_state(self, *states):
        q = ", ".join("?" * len(states))
        return self.db.execute(f"SELECT * FROM dumps WHERE state IN ({q}) ORDER BY detected_at_utc", states).fetchall()

    def known(self, dump_id):
        return self.db.execute("SELECT 1 FROM dumps WHERE id = ?", (dump_id,)).fetchone() is not None

    def meta(self, key):
        r = self.db.execute("SELECT v FROM meta WHERE k = ?", (key,)).fetchone()
        return r["v"] if r else None

    def set_meta(self, key, value):
        self.db.execute("INSERT OR REPLACE INTO meta VALUES (?, ?)", (key, value))
        self.db.commit()

    def recover(self):
        # A crash mid-upload leaves Submitting rows; the upload is idempotent server-side (dedup).
        self.db.execute("UPDATE dumps SET state = ? WHERE state = ?", (QUEUED, SUBMITTING))
        self.db.commit()


# --- upload ------------------------------------------------------------------

SUCCESS, REJECTED, QUOTA, AUTH, TRANSIENT = "success", "rejected", "quota", "auth", "transient"
AUTH_BACKOFF_SECONDS = 600


def upload_dump(base_url, api_key, path, hostname, name=None, timeout=1800):
    """Stream one file to POST /api/v1/dumps as multipart/form-data (same request
    as the web uploader and the Windows agent), named `name` (default: the path's
    basename). Returns (outcome, status, message)."""
    u = urllib.parse.urlsplit(base_url)
    boundary = uuid.uuid4().hex
    pre = b""
    if hostname:
        pre += (f'--{boundary}\r\nContent-Disposition: form-data; name="hostname"\r\n\r\n{hostname}\r\n').encode()
    name = (name or os.path.basename(path)).replace('"', "")
    pre += (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{name}"\r\n'
            "Content-Type: application/octet-stream\r\n\r\n").encode()
    post = f"\r\n--{boundary}--\r\n".encode()
    size = os.path.getsize(path)
    conn_cls = http.client.HTTPSConnection if u.scheme == "https" else http.client.HTTPConnection
    conn = conn_cls(u.hostname, u.port, timeout=timeout)
    try:
        conn.putrequest("POST", (u.path.rstrip("/") or "") + "/api/v1/dumps")
        conn.putheader("Authorization", f"Bearer {api_key}")
        conn.putheader("Content-Type", f"multipart/form-data; boundary={boundary}")
        conn.putheader("Content-Length", str(len(pre) + size + len(post)))
        conn.endheaders()
        conn.send(pre)
        with open(path, "rb") as f:
            while chunk := f.read(1 << 20):
                conn.send(chunk)
        conn.send(post)
        resp = conn.getresponse()
        body = resp.read().decode("utf-8", "replace")[:500]
    except (OSError, http.client.HTTPException) as e:
        return TRANSIENT, None, str(e)
    finally:
        conn.close()
    if resp.status == 201:
        return SUCCESS, 201, None
    if resp.status == 400:
        return REJECTED, 400, body
    if resp.status in (413, 429):
        return QUOTA, resp.status, body
    if resp.status in (401, 403):
        return AUTH, resp.status, body
    return TRANSIENT, resp.status, body


# --- stability -----------------------------------------------------------------

def open_by_other_process(path):
    """Best effort: is some other process holding the file open? (Needs root to see other users' fds.)"""
    me = os.getpid()
    try:
        target = os.path.realpath(path)
        for pid in filter(str.isdigit, os.listdir("/proc")):
            if int(pid) == me:
                continue
            fd_dir = f"/proc/{pid}/fd"
            try:
                for fd in os.listdir(fd_dir):
                    try:
                        if os.readlink(f"{fd_dir}/{fd}") == target:
                            return True
                    except OSError:
                        pass
            except OSError:
                pass
    except OSError:
        pass
    return False


# --- agent -------------------------------------------------------------------

# --- performance tracing -------------------------------------------------------
# website#1999 / dumptriage-tools#27. Everything here is standard library. The kernel's own perf does the sampling;
# the agent decides when (triggers on /proc), keeps the recorder inside its limits, folds the snapshot to text on the
# host and queues it like a dump. The shared trigger rules are tests/fixtures/tracing/triggers.json (#26).

TRACE_TYPE = "folded"  # what the perf backend uploads; the perfetto backend uploads "perfetto"
PERFETTO_TRACE_TYPE = "perfetto"
PERFETTO_SUFFIX = ".perfetto-trace"
PERFETTO_BUFFER_CAP_KB = 48 * 1024  # a trace is about its buffer's size, and the service takes at most 64 MiB (TRACE_MAX_BYTES)
PERFETTO_BUFFER_START_KB = 32 * 1024
PERFETTO_SIDE_BUFFER_KB = 2048  # buffers { size_kb: ... DISCARD } in perfetto/flight-recorder.pbtx: the process tree
PERFETTO_STOP_DELAY_MS = 2000  # what a trigger keeps recording after it fires
PERFETTO_SESSION_MS = 24 * 3600 * 1000  # a session nobody triggers ends after this and is started again
PERFETTO_TRIGGER_WAIT_SECONDS = 30
TRACE_SUFFIXES = (".folded", PERFETTO_SUFFIX)
TRIGGER_TYPES = ("cpuPercent", "loadPerCpu", "psiSomeAvg10", "diskLatencyMs", "process", "hangEvent", "schedule")
THRESHOLD_TYPES = ("cpuPercent", "loadPerCpu", "psiSomeAvg10", "diskLatencyMs", "process")
SUBMISSION_MODES = ("AlwaysSubmit", "AskFirst", "NotifyOnly", "RuleBased")
SECONDS_PER_DAY = 86400
PERF_MAX_STACK = 127
FOLDED_MAX_BYTES = 8 * 1024 * 1024
RECORDER_RETRY_SECONDS = 300
BACKEND_RECHECK_SECONDS = 600
RECORDER_START_GRACE_SECONDS = 3.0  # perf that dies this soon failed to start (no permission, no events)
OVERHEAD_WINDOW_SECONDS = 60
OVERHEAD_BACKOFF_SECONDS = 1800
SNAPSHOT_WAIT_SECONDS = 60
SCRIPT_TIMEOUT_SECONDS = 300


def validate_tracing(tracing):
    """Problems with a `tracing` config section, as the schema (schemas/tracing-config.schema.json) and this agent's
    backends would report them; an empty list means usable. Fail closed: the agent does not trace on a config it
    cannot read."""
    problems = []
    if tracing.get("backend") not in ("perf", "perfetto"):
        problems.append(f"backend {tracing.get('backend')!r} is not available in this agent (perf and perfetto are)")
    if tracing.get("submission") not in SUBMISSION_MODES:
        problems.append(f"submission {tracing.get('submission')!r} is not one of {', '.join(SUBMISSION_MODES)}")
    if not tracing.get("triggers"):
        problems.append("no triggers are configured")
    names = set()
    for i, t in enumerate(tracing.get("triggers", [])):
        where = f"triggers[{i}]"
        if not re.fullmatch(r"[A-Za-z0-9._-]{1,64}", str(t.get("name", ""))):
            problems.append(f"{where}: name must be 1 to 64 letters, digits, '.', '_' or '-'")
        elif t["name"] in names:
            problems.append(f"{where}: name {t['name']!r} is used twice")
        names.add(t.get("name"))
        if t.get("type") not in TRIGGER_TYPES:
            problems.append(f"{where}: type {t.get('type')!r} is not one of {', '.join(TRIGGER_TYPES)}")
            continue
        if t["type"] in THRESHOLD_TYPES and not isinstance(t.get("threshold"), (int, float)):
            problems.append(f"{where}: {t['type']} needs a numeric threshold")
        if t["type"] == "process" and not t.get("process"):
            problems.append(f"{where}: a process trigger needs a process name")
        if t["type"] == "schedule" and not (isinstance(t.get("intervalMin"), int) and t["intervalMin"] >= 1):
            problems.append(f"{where}: a schedule trigger needs intervalMin of 1 or more")
        if t["type"] == "psiSomeAvg10" and isinstance(t.get("threshold"), (int, float)) and t["threshold"] > 100:
            problems.append(f"{where}: psiSomeAvg10 is a percentage, at most 100")
        if not isinstance(t.get("sustainSec", 0), int) or t.get("sustainSec", 0) < 0:
            problems.append(f"{where}: sustainSec must be a whole number of seconds, 0 or more")
    limits = tracing.get("limits", {})
    for key in ("maxDurationSec", "maxDiskMB"):
        if not isinstance(limits.get(key), int) or limits[key] < 1:
            problems.append(f"limits.{key} must be a whole number, 1 or more")
    if not isinstance(limits.get("maxOverheadPercent"), (int, float)) or not 0 < limits["maxOverheadPercent"] <= 100:
        problems.append("limits.maxOverheadPercent must be above 0 and at most 100")
    for key in ("cooldownMin", "dailyCap"):
        if not isinstance(limits.get(key), int) or limits[key] < 0:
            problems.append(f"limits.{key} must be a whole number, 0 or more")
    return problems


# Trigger rules. One implementation, run by the daemon and by the shared fixture's test.

class TriggerRunner:
    """A trigger is met when its metric stays at or above the threshold for sustainSec. A dip below, or no reading,
    restarts the clock; once met it must be sustained again."""

    def __init__(self, trigger):
        self.threshold = trigger.get("threshold")
        self.sustain = trigger.get("sustainSec", 0)
        self.run_start = None

    def feed(self, t, value):
        if value is None or value < self.threshold:
            self.run_start = None
            return False
        if self.run_start is None:
            self.run_start = t
        if t - self.run_start >= self.sustain:
            self.run_start = None
            return True
        return False


def capture_outcome(t, limits, history):
    """What a met trigger does at time `t`: cooldown (a capture within cooldownMin), capped (dailyCap captures in
    the last 24 h), else fire. Cooldown is reported before the cap."""
    if any(a > t - limits["cooldownMin"] * 60 for a in history):
        return "cooldown"
    if sum(1 for a in history if a > t - SECONDS_PER_DAY) >= limits["dailyCap"]:
        return "capped"
    return "fire"


def evaluate_series(trigger, limits, history, series):
    """The shared fixture's contract: (tSec, value) samples in, the first met moment decided. Returns
    {"outcome": ..., "atSec": ...} (atSec absent for no_fire)."""
    runner = TriggerRunner(trigger)
    for t, value in series:
        if runner.feed(t, value):
            return {"outcome": capture_outcome(t, limits, history), "atSec": t}
    return {"outcome": "no_fire"}


# /proc readers

class ProcSampler:
    """Reads the metrics triggers use from /proc (a fake tree in tests). cpuPercent, per-process percent and disk
    latency are deltas between two reads, so the first read has none of them (None)."""

    DISK = re.compile(r"(sd[a-z]+|vd[a-z]+|xvd[a-z]+|hd[a-z]+|nvme\d+n\d+|mmcblk\d+|md\d+|dm-\d+)")

    def __init__(self, root="/proc", want_processes=False):
        self.root = Path(root)
        self.want_processes = want_processes
        self.clk = os.sysconf("SC_CLK_TCK") if hasattr(os, "sysconf") else 100
        self.prev = None
        self.ncpu = 1

    def _read(self, name):
        try:
            return (self.root / name).read_text()
        except OSError:
            return ""

    def _cpu(self):
        total = busy = None
        ncpu = 0
        for line in self._read("stat").splitlines():
            f = line.split()
            if f and f[0] == "cpu":
                v = [int(x) for x in f[1:9]]
                total, busy = sum(v), sum(v) - v[3] - v[4]  # not idle, not iowait
            elif f and re.fullmatch(r"cpu\d+", f[0]):
                ncpu += 1
        self.ncpu = max(ncpu, 1)
        return total, busy

    def _psi(self):
        best = None
        for res in ("cpu", "io", "memory"):
            m = re.search(r"^some .*?avg10=([0-9.]+)", self._read(f"pressure/{res}"), re.M)
            if m:
                best = max(best or 0.0, float(m.group(1)))
        return best

    def _disks(self):
        out = {}
        for line in self._read("diskstats").splitlines():
            f = line.split()
            if len(f) >= 11 and self.DISK.fullmatch(f[2]):
                out[f[2]] = (int(f[3]) + int(f[7]), int(f[6]) + int(f[10]))  # completed ios, ms spent on them
        return out

    def _procs(self):
        out = {}
        try:
            entries = [e for e in os.listdir(self.root) if e.isdigit()]
        except OSError:
            return out
        for pid in entries:
            try:
                text = (self.root / pid / "stat").read_text()
            except OSError:
                continue
            end = text.rfind(")")
            if end == -1:
                continue
            rest = text[end + 2:].split()
            if len(rest) > 12:
                out[pid] = (text[text.find("(") + 1:end], int(rest[11]) + int(rest[12]))
        return out

    def read(self, now):
        """Metrics at epoch seconds `now`: cpuPercent, loadPerCpu, psiSomeAvg10, diskLatencyMs, procs {comm: percent of
        one CPU}. A metric that cannot be read (or has no previous read to compare with) is None."""
        total, busy = self._cpu()
        try:
            load = float(self._read("loadavg").split()[0]) / self.ncpu
        except (IndexError, ValueError):
            load = None
        disks = self._disks()
        procs = self._procs() if self.want_processes else {}
        cur = {"t": now, "total": total, "busy": busy, "disks": disks, "procs": procs}
        out = {"cpuPercent": None, "loadPerCpu": load, "psiSomeAvg10": self._psi(), "diskLatencyMs": None, "procs": {}}
        prev, self.prev = self.prev, cur
        if prev is None:
            return out
        if total is not None and total > prev["total"]:
            out["cpuPercent"] = 100.0 * (busy - prev["busy"]) / (total - prev["total"])
        worst = 0.0
        for name, (ios, ms) in disks.items():
            if name in prev["disks"] and ios > prev["disks"][name][0]:
                worst = max(worst, (ms - prev["disks"][name][1]) / (ios - prev["disks"][name][0]))
        out["diskLatencyMs"] = worst if disks else None
        elapsed = now - prev["t"]
        if self.want_processes and elapsed > 0:
            per = Counter()
            for pid, (comm, ticks) in procs.items():
                if pid in prev["procs"]:
                    per[comm] += max(ticks - prev["procs"][pid][1], 0) / self.clk / elapsed * 100.0
            out["procs"] = dict(per)
        return out


def trigger_value(trigger, metrics):
    kind = trigger["type"]
    if kind in ("process",) or (kind == "cpuPercent" and trigger.get("process")):
        return metrics["procs"].get(trigger["process"][:15])  # comm is at most 15 characters
    return metrics.get(kind)


# Folding: perf script text to `process;root;...;leaf count` (what stackcollapse-perf.pl writes, with its --kernel
# marker: a kernel frame ends in `_[k]`). Only these stacks leave the host.

_SAMPLE_LINE = re.compile(
    r"^(?P<comm>\S.*?)\s+(?P<pid>\d+)(?:/\d+)?\s+(?:\[\d+\]\s+)?(?P<time>\d+\.\d+):\s*(?:\d+\s+)?(?P<event>[^\s:][^\s]*?):(?:\s|$)"
)
_OFFSET = re.compile(r"\+0x[0-9a-fA-F]+$")
_HOME = (
    (re.compile(r"(/home/)[^/\s;:)]+"), r"\1<user>"),
    (re.compile(r"(/Users/)[^/\s;:)]+"), r"\1<user>"),
)


def redact_home(text):
    for pattern, repl in _HOME:
        text = pattern.sub(repl, text)
    return text


_NOT_KERNEL_PSEUDO = ("[unknown]", "[vdso]", "[vsyscall]", "[heap]", "[stack]", "[anon]")


def _is_kernel_dso(dso):
    """The kernel itself (`[kernel.kallsyms]`, `[kernel.vmlinux]`) or a loaded module: perf names a module's DSO `[name]` or its .ko file
    under /lib/modules. (No module frame was in any real capture here, so the module forms are not checked against real output.)"""
    if dso.startswith("[kernel"):
        return True
    if dso.startswith("[") and dso not in _NOT_KERNEL_PSEUDO:
        return True
    return dso.startswith("/lib/modules/") or re.search(r"\.ko(\.(xz|zst|gz))?$", dso) is not None


def _frame_name(line):
    """The symbol of a `perf script` frame line, with `_[k]` for the kernel; None if the line is not a frame."""
    body = line.strip().split(None, 1)
    if len(body) != 2 or not re.fullmatch(r"[0-9a-fA-F]+", body[0]):
        return None
    rest = body[1]
    cut = rest.rfind(" (")
    if cut == -1 or not rest.endswith(")"):
        return None
    symbol = _OFFSET.sub("", rest[:cut].strip()) or "[unknown]"
    symbol = redact_home(symbol).replace(";", ",")
    return symbol + "_[k]" if _is_kernel_dso(rest[cut + 2:-1]) and symbol != "[unknown]" else symbol


class Folded:
    def __init__(self, stacks, first, last, samples, other_events, lines_unread):
        self.stacks, self.first, self.last = stacks, first, last
        self.samples, self.other_events, self.lines_unread = samples, other_events, lines_unread

    @property
    def span(self):
        return (self.last - self.first) if self.samples else 0.0

    def text(self, max_bytes=FOLDED_MAX_BYTES):
        out, used = [], 0
        for stack, count in self.stacks.most_common():
            line = f"{stack} {count}\n"
            if used + len(line) > max_bytes:
                break
            out.append(line)
            used += len(line)
        return "".join(out)


def fold_perf_script(lines, window_sec=None):
    """Fold `perf script [--header]` lines. Only the first event's samples count (another event's would double-count
    the same time). With `window_sec`, only the samples within that many seconds of the last one are kept: the
    flight recorder's ring buffer can hold more than the limits allow to be sent."""
    interned, samples = {}, []
    comm = None
    frames = []
    other = unread = 0
    first_event = None

    def flush():
        nonlocal comm, frames
        if comm is not None:
            key = ";".join([redact_home(comm).replace(";", ","), *reversed(frames[:512])])
            samples.append((current_t, interned.setdefault(key, key)))
        comm, frames = None, []

    current_t = 0.0
    for line in lines:
        line = line.rstrip("\n")
        if not line.strip():
            flush()
            continue
        if line.startswith("#"):
            continue
        if line[0] in " \t":
            if comm is not None:
                name = _frame_name(line)
                if name is None:
                    unread += 1
                else:
                    frames.append(name)
            continue
        flush()
        m = _SAMPLE_LINE.match(line)
        if m is None:
            unread += 1
            continue
        if first_event is None:
            first_event = m.group("event")
        if m.group("event") != first_event:
            other += 1
            continue
        comm, current_t = m.group("comm"), float(m.group("time"))
    flush()
    if not samples:
        return Folded(Counter(), 0.0, 0.0, 0, other, unread)
    last = max(t for t, _ in samples)
    cutoff = last - window_sec if window_sec else None
    kept = [(t, k) for t, k in samples if cutoff is None or t >= cutoff]
    return Folded(Counter(k for _, k in kept), min(t for t, _ in kept), last, len(kept), other, unread)


# Upload

def upload_trace(base_url, api_key, path, manifest_json, hostname, name="trace.folded", timeout=600, content_type="text/plain"):
    """POST /api/v1/traces as multipart/form-data: the manifest.json text, the optional hostname and the folded
    stacks as `file`. Returns (outcome, status, message), like upload_dump."""
    u = urllib.parse.urlsplit(base_url)
    boundary = uuid.uuid4().hex
    pre = b""
    for field, value in (("manifest", manifest_json), ("hostname", hostname)):
        if value:
            pre += f'--{boundary}\r\nContent-Disposition: form-data; name="{field}"\r\n\r\n{value}\r\n'.encode()
    name = name.replace('"', "")
    pre += (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{name}"\r\n'
            f"Content-Type: {content_type}\r\n\r\n").encode()
    post = f"\r\n--{boundary}--\r\n".encode()
    size = os.path.getsize(path)
    conn_cls = http.client.HTTPSConnection if u.scheme == "https" else http.client.HTTPConnection
    conn = conn_cls(u.hostname, u.port, timeout=timeout)
    try:
        conn.putrequest("POST", (u.path.rstrip("/") or "") + "/api/v1/traces")
        conn.putheader("Authorization", f"Bearer {api_key}")
        conn.putheader("Content-Type", f"multipart/form-data; boundary={boundary}")
        conn.putheader("Content-Length", str(len(pre) + size + len(post)))
        conn.endheaders()
        conn.send(pre)
        with open(path, "rb") as f:
            while chunk := f.read(1 << 20):
                conn.send(chunk)
        conn.send(post)
        resp = conn.getresponse()
        body = resp.read().decode("utf-8", "replace")[:500]
    except (OSError, http.client.HTTPException) as e:
        return TRANSIENT, None, str(e)
    finally:
        conn.close()
    if resp.status == 201:
        return SUCCESS, 201, None
    if resp.status in (400, 422):
        return REJECTED, resp.status, body
    if resp.status in (413, 429):
        return QUOTA, resp.status, body
    if resp.status in (401, 403):
        return AUTH, resp.status, body
    return TRANSIENT, resp.status, body


# The recorder

class Recorder:
    """What both recorders (perf and perfetto) share with the agent: what they are called, what they upload, and that a session ending
    by itself is a fault unless the recorder says it is not."""

    kind = "perf"
    trace_type = TRACE_TYPE
    suffix = ".folded"

    def ended_cleanly(self):
        return False


def trigger_name_for(name):
    """The name a configured trigger has in the perfetto config and on `tracebox trigger_perfetto`: one place, so they cannot differ."""
    return "pi_" + re.sub(r"[^A-Za-z0-9_.-]", "_", name)


def render_perfetto_config(template, buffer_kb, trigger_names, stop_delay_ms=PERFETTO_STOP_DELAY_MS, timeout_ms=PERFETTO_SESSION_MS):
    """The flight-recorder config for these triggers: one `pi_<name>` trigger each, all of which stop the session."""
    names = []
    for name in trigger_names:
        trigger = trigger_name_for(name)
        if trigger not in names:
            names.append(trigger)
    block = "\n".join(f'  triggers {{ name: "{n}" stop_delay_ms: {stop_delay_ms} }}' for n in names)
    return (template.replace("{{BUFFER_KB}}", str(int(buffer_kb))).replace("{{TRIGGER_TIMEOUT_MS}}", str(int(timeout_ms)))
            .replace("{{TRIGGERS}}", block))


def descendant_cpu_ticks(proc_root, pid):
    """utime+stime of `pid` and every process below it: tracebox starts traced, traced_probes and traced_perf itself, and
    the cost of the recorder is all of them."""
    root = Path(proc_root)
    children, ticks = {}, {}
    try:
        entries = [e for e in os.listdir(root) if e.isdigit()]
    except OSError:
        return None
    for entry in entries:
        try:
            text = (root / entry / "stat").read_text()
            rest = text[text.rfind(")") + 2:].split()
            children.setdefault(rest[1], []).append(entry)
            ticks[entry] = int(rest[11]) + int(rest[12])
        except (OSError, ValueError, IndexError):
            continue
    if str(pid) not in ticks:
        return None
    total, stack = 0, [str(pid)]
    while stack:
        cur = stack.pop()
        total += ticks.get(cur, 0)
        stack.extend(children.get(cur, []))
    return total


def _raise_file_limit():
    """The perf data source opens a few files per sampled process; the default 1024 runs out on a busy host
    ("Too many open files" in proc_descriptors.cc, seen on the first real capture)."""
    soft, hard = resource.getrlimit(resource.RLIMIT_NOFILE)
    want = 65536 if hard == resource.RLIM_INFINITY else min(hard, 65536)
    if soft == resource.RLIM_INFINITY or soft < want:
        resource.setrlimit(resource.RLIMIT_NOFILE, (want, hard))


class PerfettoRecorder(Recorder):
    """`tracebox` in autostart mode with a STOP_TRACING flight-recorder config: a ring buffer that writes nothing until a
    trigger. Unlike perf, a snapshot ends the session (the trace is written when it stops), so the recorder starts a new
    one after every capture and has no history for the first seconds of it."""

    kind = "perfetto"
    trace_type = PERFETTO_TRACE_TYPE
    suffix = PERFETTO_SUFFIX

    def __init__(self, tracebox, template, trace_dir, ncpu, limits, trigger_names, proc_root="/proc", rate_bytes_per_sec=None):
        self.tracebox, self.template, self.dir, self.ncpu = tracebox, template, Path(trace_dir), ncpu
        self.limits, self.trigger_names, self.proc_root = limits, list(trigger_names), Path(proc_root)
        self.rate = rate_bytes_per_sec
        self.proc = None
        self.log_path = self.dir / "perfetto.log"
        self.config_path = self.dir / "flight-recorder.pbtx"
        self.out_path = self.dir / "perfetto-recording.part"
        self.sock_dir = None
        self.command = []
        self.started_wall = None
        self.lookback = 0.0
        self.cpu_ticks = None
        self.session_buffer_kb = 0

    def buffer_kb(self):
        """The ring buffer: sized from the rate the last capture measured so it holds about maxDurationSec (with a quarter
        to spare), within half the disk budget and the service's upload cap; the first session guesses 32 MiB."""
        # The trace also holds the small process buffer (PERFETTO_SIDE_BUFFER_KB in the config), so the ring is the budget less that.
        cap = max(min(self.limits["maxDiskMB"] * 1024 // 2, PERFETTO_BUFFER_CAP_KB) - PERFETTO_SIDE_BUFFER_KB, PERFETTO_SIDE_BUFFER_KB)
        want = PERFETTO_BUFFER_START_KB
        if self.rate:
            want = int(self.rate * self.limits["maxDurationSec"] * 1.25 / 1024)
        return max(min(want, cap), min(8192, cap))

    def _env(self):
        return dict(os.environ, PERFETTO_PRODUCER_SOCK_NAME=str(self.sock_dir / "p"), PERFETTO_CONSUMER_SOCK_NAME=str(self.sock_dir / "c"))

    def start(self):
        self.dir.mkdir(parents=True, exist_ok=True)
        # Sockets in their own 0700 directory with a short path (a unix socket path is limited to about 100 bytes): the sockets
        # themselves are world-writable, so the directory is what keeps other users off the recorder.
        parent = "/run" if os.geteuid() == 0 and os.path.isdir("/run") else (os.environ.get("XDG_RUNTIME_DIR") or tempfile.gettempdir())
        self.sock_dir = Path(tempfile.mkdtemp(prefix="dt-", dir=parent))
        fd = os.open(self.config_path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w") as f:
            self.session_buffer_kb = self.buffer_kb()
            f.write(render_perfetto_config(self.template, self.session_buffer_kb, self.trigger_names))
        self.out_path.unlink(missing_ok=True)
        self.command = [self.tracebox, "--txt", "-c", str(self.config_path), "-o", str(self.out_path)]
        log_file = open(self.log_path, "wb")
        try:
            self.proc = subprocess.Popen(self.command, stdin=subprocess.DEVNULL, stdout=log_file, stderr=log_file, env=self._env(),
                                         start_new_session=True, preexec_fn=_raise_file_limit)
        except (OSError, subprocess.SubprocessError) as e:  # not executable here (wrong architecture, noexec), or the limit cannot be raised
            self._cleanup()
            raise OSError(f"tracebox could not be run: {e}") from e
        finally:
            log_file.close()
        deadline = time.monotonic() + RECORDER_START_GRACE_SECONDS
        while time.monotonic() < deadline:
            if self.proc.poll() is not None:
                self._cleanup()
                raise OSError(f"tracebox exited with {self.proc.returncode}: {self.tail()}")
            time.sleep(min(0.1, RECORDER_START_GRACE_SECONDS))
        self.started_wall = time.time()
        self.cpu_ticks = None

    def tail(self):
        try:
            return " ".join(self.log_path.read_text(errors="replace").split())[-300:]
        except OSError:
            return ""

    def alive(self):
        return self.proc is not None and self.proc.poll() is None

    def ended_cleanly(self):
        """The session ran out its trigger timeout with nobody asking: tracebox wrote the unwanted ring buffer and exited 0."""
        return self.proc is not None and self.proc.poll() == 0

    def _cleanup(self):
        if self.sock_dir is not None:
            shutil.rmtree(self.sock_dir, ignore_errors=True)
            self.sock_dir = None

    def discard_output(self):
        self.out_path.unlink(missing_ok=True)
        self._cleanup()

    def snapshot(self, trigger_name):
        """Fire the trigger, wait for the session to stop and write the file; its path. The session is over afterwards."""
        if self.proc is None or self.sock_dir is None or not self.alive():
            raise OSError("no recorder session is running (the last capture ended it); the next one starts on the next tick")
        try:
            fired = subprocess.run([self.tracebox, "trigger_perfetto", trigger_name_for(trigger_name)], env=self._env(), capture_output=True,
                                   text=True, timeout=PERFETTO_TRIGGER_WAIT_SECONDS)
        except subprocess.TimeoutExpired as e:
            raise OSError("trigger_perfetto did not return") from e
        if fired.returncode != 0:
            raise OSError(f"trigger_perfetto exited with {fired.returncode}: {' '.join((fired.stderr or fired.stdout).split())[-200:]}")
        try:
            self.proc.wait(timeout=SNAPSHOT_WAIT_SECONDS * 2)
        except subprocess.TimeoutExpired as e:
            raise OSError("tracebox did not stop after the trigger") from e
        code = self.proc.returncode
        self.proc = None
        self._cleanup()
        if code != 0 or not self.out_path.exists() or self.out_path.stat().st_size == 0:
            raise OSError(f"tracebox exited with {code} and wrote no trace: {self.tail()}")
        age = max(time.time() - (self.started_wall or time.time()), 0.0)
        size = self.out_path.stat().st_size
        full = size >= 0.9 * self.session_buffer_kb * 1024  # a full ring has dropped its oldest data: size over age is then not the rate
        if not full and age > 0:
            self.rate = size / age  # the next ring buffer is sized from this
        # The history the trace holds: the session's age while the ring has room, else what the ring holds at the known rate
        # (at most maxDurationSec's worth if the rate is not known yet), never more than the age.
        if not full:
            self.lookback = age
        elif self.rate:
            self.lookback = min(size / self.rate, age)
        else:
            self.lookback = min(float(self.limits["maxDurationSec"]), age)
        return self.out_path

    def overhead_percent(self, now_wall):
        """tracebox's CPU and that of the services it started, as a share of all CPUs since the last reading (60 s apart or more)."""
        if self.proc is None or (self.cpu_ticks is not None and now_wall - self.cpu_ticks[0] < OVERHEAD_WINDOW_SECONDS):
            return None  # not due: the scan reads every process on the host, so it is not done on every tick
        ticks = descendant_cpu_ticks(self.proc_root, self.proc.pid)
        if ticks is None:
            return None
        clk = os.sysconf("SC_CLK_TCK") if hasattr(os, "sysconf") else 100
        if self.cpu_ticks is None:
            self.cpu_ticks = (now_wall, ticks)
            return None
        wall0, ticks0 = self.cpu_ticks
        self.cpu_ticks = (now_wall, ticks)
        return 100.0 * ((ticks - ticks0) / clk) / ((now_wall - wall0) * max(self.ncpu, 1))

    def stop(self):
        if self.proc is not None:
            if self.proc.poll() is None:
                # The whole process group: tracebox started traced, traced_probes and traced_perf, and killing only the leader would leave them sampling.
                self._signal_group(signal.SIGTERM)
                try:
                    self.proc.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    self._signal_group(signal.SIGKILL)
                    self.proc.wait()
            self.proc = None
        self.discard_output()

    def _signal_group(self, sig):
        try:
            os.killpg(self.proc.pid, sig)  # start_new_session made tracebox the leader of its own group
        except OSError:
            self.proc.send_signal(sig)


class PerfRecorder(Recorder):
    """`perf record -F 99 -a -g --overwrite --switch-output=signal`: a ring buffer that costs a bounded amount of
    memory and CPU and writes nothing until SIGUSR2, which dumps what it holds to `perf.data.<timestamp>`."""

    def __init__(self, perf_bin, trace_dir, ncpu, max_disk_mb, proc_root="/proc"):
        self.perf, self.dir, self.ncpu = perf_bin, Path(trace_dir), ncpu
        self.max_disk_mb, self.proc_root = max_disk_mb, Path(proc_root)
        self.proc = None
        self.log_path = self.dir / "perf-record.log"
        self.command = []
        self.started_at = None
        self.cpu_ticks = None  # (wall, ticks) at the last overhead reading

    def mmap_pages(self):
        """Pages per CPU of the ring: a quarter of the disk budget in total at most (a snapshot is about the ring's
        size), a power of two as perf wants, and at least 64 and at most 16384 pages (64 MiB) per CPU."""
        pages = (self.max_disk_mb * 1024 * 1024 // 4) // max(self.ncpu, 1) // 4096
        n = 64
        while n * 2 <= min(pages, 16384):
            n *= 2
        return n

    def start(self):
        self.dir.mkdir(parents=True, exist_ok=True)
        self.command = [self.perf, "record", "-F", "99", "-a", "-g", "--overwrite", "--switch-output=signal",
                        "-m", str(self.mmap_pages()), "-o", str(self.dir / "perf.data")]
        log_file = open(self.log_path, "wb")
        try:
            self.proc = subprocess.Popen(self.command, stdin=subprocess.DEVNULL, stdout=log_file, stderr=log_file,
                                         start_new_session=True)
        finally:
            log_file.close()
        deadline = time.monotonic() + RECORDER_START_GRACE_SECONDS
        while time.monotonic() < deadline:
            if self.proc.poll() is not None:
                raise OSError(f"perf record exited with {self.proc.returncode}: {self.tail()}")
            time.sleep(min(0.1, RECORDER_START_GRACE_SECONDS))
        self.started_at = time.monotonic()
        self.cpu_ticks = None

    def tail(self):
        try:
            return " ".join(self.log_path.read_text(errors="replace").split())[-300:]
        except OSError:
            return ""

    def alive(self):
        return self.proc is not None and self.proc.poll() is None

    def snapshot(self, trigger_name=None):
        """Dump the ring buffer now; the path of the new perf.data.<timestamp>."""
        before = set(self.dir.glob("perf.data.[0-9]*"))
        self.proc.send_signal(signal.SIGUSR2)
        deadline = time.monotonic() + SNAPSHOT_WAIT_SECONDS
        while time.monotonic() < deadline:
            if self.proc.poll() is not None:
                raise OSError(f"perf record exited while dumping: {self.tail()}")
            new = sorted(set(self.dir.glob("perf.data.[0-9]*")) - before)
            # perf prints "[ perf record: Dump <file> ]" once the file is complete: the name as given to -o, so the full
            # path when -o is absolute (as here) and the bare name when it is relative (checked on perf 5.14 to 6.8).
            if new and f"Dump {new[-1]}" in self.tail_all():
                return new[-1]
            time.sleep(0.2)
        raise OSError("perf record did not finish the dump in time")

    def tail_all(self):
        try:
            return self.log_path.read_text(errors="replace")
        except OSError:
            return ""

    def overhead_percent(self, now_wall):
        """The recorder's own CPU as a share of the machine's CPU over the last reading's interval: utime+stime of the
        perf process. A lower bound (the kernel's sampling interrupts are charged to whatever was running), so the
        README also records an A/B measurement. None until two readings OVERHEAD_WINDOW_SECONDS apart exist."""
        try:
            text = (self.proc_root / str(self.proc.pid) / "stat").read_text()
            rest = text[text.rfind(")") + 2:].split()
            ticks = int(rest[11]) + int(rest[12])
        except (OSError, ValueError, IndexError, AttributeError):
            return None
        clk = os.sysconf("SC_CLK_TCK") if hasattr(os, "sysconf") else 100
        if self.cpu_ticks is None:
            self.cpu_ticks = (now_wall, ticks)
            return None
        wall0, ticks0 = self.cpu_ticks
        if now_wall - wall0 < OVERHEAD_WINDOW_SECONDS:
            return None
        self.cpu_ticks = (now_wall, ticks)
        return 100.0 * ((ticks - ticks0) / clk) / ((now_wall - wall0) * max(self.ncpu, 1))

    def stop(self):
        if self.proc is None:
            return
        if self.proc.poll() is None:
            self.proc.send_signal(signal.SIGINT)
            try:
                self.proc.wait(timeout=30)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait()
        self.proc = None
        # SIGINT makes perf dump the buffer once more: nobody asked for that one.
        for leftover in self.dir.glob("perf.data.[0-9]*"):
            leftover.unlink(missing_ok=True)
        (self.dir / "perf.data").unlink(missing_ok=True)


class TraceState:
    """What the daemon holds between ticks while tracing: the recorder, the readers and the runners."""

    def __init__(self):
        self.validated = False
        self.disabled = False
        self.recorder = None
        self.sampler = None
        self.runners = []
        self.recorder_retry_at = 0.0
        self.error = None
        self.overhead = None
        self.warned_hang = False
        self.tool_version = None
        self.backend = "perf"
        self.backend_note = None  # why the configured backend is not the one running
        self.backend_check_at = 0.0
        self.template = None
        self.tracebox_version = None
        self.perfetto_rate = None


class Agent:
    def __init__(self, config, config_dir, state_dir, queue=None, uploader=upload_dump, clock=utcnow,
                 trace_uploader=upload_trace, perf_bin=None, proc_root="/proc", tracebox_bin=None, tracefs_dirs=None):
        self.cfg, self.config_dir, self.state_dir = config, Path(config_dir), Path(state_dir)
        self.queue = queue or Queue(self.state_dir / "queue.db", clock)
        self.uploader, self.clock = uploader, clock
        self.trace_uploader, self.perf_bin, self.proc_root = trace_uploader, perf_bin, proc_root
        self.tracebox_bin, self.tracefs_dirs = tracebox_bin, tracefs_dirs or ("/sys/kernel/tracing", "/sys/kernel/debug/tracing")
        self.trace_state = TraceState()
        self.stable = {}  # path -> (size, mtime_ns, first_seen_unchanged_monotonic)
        self.ignored = set()  # (path, size, mtime_ns) already sniffed as not-a-dump
        self.untrusted = set()  # (watched root, problem) already logged by candidates
        self.hostname = socket.gethostname() if config["sendHostname"] else None
        self.queue.recover()
        # Files older than the agent's first run are left alone (#17): a fresh install mustn't upload
        # every old core already lying around. A queue that already existed (an upgrade) has seen
        # everything in its folders, so it filters nothing; files written while the agent was stopped
        # always count. Never later than now, in case the clock was ahead when it was recorded.
        now = self.clock()
        since = self.queue.meta("watching_since_utc")
        if since is None:  # a queue from before this setting (a new one gets it on creation): an upgrade
            since = iso(datetime(1970, 1, 1, tzinfo=timezone.utc))
            self.queue.set_meta("watching_since_utc", since)
        elif datetime.fromisoformat(since) > now:
            since = iso(now)
            self.queue.set_meta("watching_since_utc", since)
        self.watching_since_ns = int(datetime.fromisoformat(since).timestamp()) * 1_000_000_000
        self.logged_old = set()  # paths skipped as older than watching_since, logged once

    def api_key(self):
        key_file = self.config_dir / "api-key"
        return key_file.read_text().strip() if key_file.exists() else None

    # discovery
    def candidates(self):
        """(path, lstat) of each file up to two folders deep in each watched path. The walk starts
        from the descriptor open_dir_safely returns and never follows a symlink, so what is listed
        is what that check accepted (#4)."""
        for root in self.roots():
            root_fd = None
            try:
                root_fd = open_dir_safely(root)
                # Read access to the root itself, checked here so fwalk can't raise mid-scan.
                os.close(os.open(".", os.O_RDONLY | os.O_DIRECTORY, dir_fd=root_fd))
            except FileNotFoundError:
                if root_fd is not None:
                    os.close(root_fd)
                continue  # e.g. /var/crash before the first crash
            except OSError as e:
                if root_fd is not None:
                    os.close(root_fd)
                problem = (f"its path goes through a symlink another user could have planted ({e.filename})"
                           if isinstance(e, NotWatched) else str(e))
                if (root, problem) not in self.untrusted:
                    log.error("not watching %s: %s", root, problem)
                    self.untrusted.add((root, problem))
                continue
            try:
                for dirpath, dirs, files, dir_fd in os.fwalk(".", dir_fd=root_fd, onerror=lambda e: None):
                    if dirpath.count("/") >= 2:
                        dirs[:] = []
                    for name in files:
                        try:
                            st = os.stat(name, dir_fd=dir_fd, follow_symlinks=False)
                        except OSError:
                            continue
                        yield os.path.join(root, dirpath[2:], name) if dirpath != "." else os.path.join(root, name), st
            finally:
                os.close(root_fd)

    def scan(self):
        """Returns True while any candidate is still settling (so --once knows to keep going)."""
        settling, live = False, set()
        for path, st in self.candidates():
            # Not a symlink, not a hard link: see open_dump (#4).
            if not stat.S_ISREG(st.st_mode) or st.st_nlink != 1 or st.st_size == 0:
                continue
            # Older than the agent's first run (#17). ctime too: a core moved or copied in on purpose
            # keeps its old mtime (mv, cp -p, rsync -t) but gets a new ctime.
            if max(st.st_mtime_ns, st.st_ctime_ns) < self.watching_since_ns:
                if path not in self.logged_old:
                    log.info("ignoring %s: older than this agent's first run", path)
                    self.logged_old.add(path)
                continue
            ident = (path, st.st_size, st.st_mtime_ns)
            dump_id = hashlib.sha256(f"{path}|{st.st_ino}|{st.st_mtime_ns}".encode()).hexdigest()[:32]
            if ident in self.ignored or self.queue.known(dump_id):
                continue
            live.add(path)
            prev = self.stable.get(path)
            now = time.monotonic()
            if prev is None or prev[:2] != (st.st_size, st.st_mtime_ns):
                self.stable[path] = (st.st_size, st.st_mtime_ns, now)
                settling = True
                continue
            if now - prev[2] < self.cfg["stableSeconds"] or open_by_other_process(path):
                settling = True
                continue
            self.accept(path, st, dump_id, ident)
            self.stable.pop(path, None)
        for gone in set(self.stable) - live:
            del self.stable[gone]
        return settling

    def accept(self, path, st, dump_id, ident):
        comp = compression_of(path)
        try:
            with open_dump(path, self.roots()) as f:
                dump_type = detect_dump_type(read_header(f, comp))
        except (OSError, subprocess.SubprocessError, lzma.LZMAError) as e:
            log.debug("cannot read %s: %s", path, e)
            self.ignored.add(ident)
            return
        if dump_type is None:
            self.ignored.add(ident)
            return
        now = self.clock()
        row = dict(id=dump_id, file_path=path, size_bytes=st.st_size, dump_type=dump_type, compression=comp,
                   state=DETECTED, detected_at_utc=iso(now))
        outcome = evaluate_policy(row, self.cfg["policy"], now.astimezone())
        if outcome == AUTO:
            row["state"] = QUEUED
        elif outcome == ASK:
            row["state"] = PENDING
            row["ask_deadline_utc"] = iso(now + timedelta(seconds=self.cfg["policy"]["askTimeoutSeconds"]))
        elif outcome == MANUAL:
            row["state"] = PENDING
        else:
            row["state"] = SKIPPED
        self.queue.add(**row)
        log.info("detected %s dump %s (%d bytes) -> %s [id %s]", dump_type, path, st.st_size, row["state"], dump_id[:12])
        if row["state"] == PENDING:
            log.warning("dump %s awaits approval: run `dumptriage-agent approve %s` or `skip %s`", path, dump_id[:12], dump_id[:12])

    # approvals
    def resolve_asks(self):
        now = self.clock()
        for r in self.queue.in_state(PENDING):
            if r["ask_deadline_utc"] and datetime.fromisoformat(r["ask_deadline_utc"]) <= now:
                action = self.cfg["policy"]["askTimeoutDefaultAction"].lower()
                new = QUEUED if action == "submit" else SKIPPED
                log.info("approval for %s timed out -> %s", r["file_path"], new)
                self.queue.update(r["id"], state=new, ask_deadline_utc=None)

    # upload
    def submit_due(self):
        now = self.clock()
        batching = self.cfg["policy"]["batching"]
        last = self.queue.meta("last_flush_utc")
        if batching["mode"] == "ScheduledTimeOfDay" and last is None:
            self.queue.set_meta("last_flush_utc", iso(now))  # first run: wait for the next slot
            return
        last = datetime.fromisoformat(last) if last else None
        if not should_submit_now(batching, now.astimezone(), last.astimezone() if last else None):
            return
        if self.auth_paused(now):
            return  # the key was refused; don't send dumps to be refused again (#11)
        self.used_key_stamp = self.key_stamp()  # before reading the key, so a key stored in between isn't blamed
        key = self.api_key()
        due = [r for r in self.queue.in_state(QUEUED) if not r["next_attempt_utc"] or datetime.fromisoformat(r["next_attempt_utc"]) <= now]
        if due and not key:
            log.error("no API key at %s; run `dumptriage-agent set-api-key`", self.config_dir / "api-key")
            return
        refused = False
        for r in due:
            if self.submit_one(r, key) == AUTH:
                refused = True
                break  # the rest would be refused too
        if batching["mode"] == "ScheduledTimeOfDay" and not refused:
            # The slot is used, even if nothing was due. Not when the key was refused, or (returns
            # above) during an auth pause or without a key: then it is taken once that clears.
            self.queue.set_meta("last_flush_utc", iso(now))

    def key_stamp(self):
        try:
            return str((self.config_dir / "api-key").stat().st_mtime_ns)
        except OSError:
            return ""

    def auth_paused(self, now):
        """After a 401/403, uploads wait AUTH_BACKOFF_SECONDS, doubling with each refusal in a row up to
        6 h, since every attempt sends the whole dump. A new key (the file changed) ends the pause."""
        until = self.queue.meta("auth_backoff_until_utc")
        if not until or datetime.fromisoformat(until) <= now:
            return False
        if self.queue.meta("auth_refused_key_stamp") != self.key_stamp():
            return False
        return True

    def roots(self):
        return [root_path(r) for r in self.cfg["watchedPaths"]]

    def submit_one(self, r, key):
        if r["kind"] == "trace":
            return self.submit_trace(r, key)
        self.queue.update(r["id"], state=SUBMITTING)
        spool = None
        try:
            with open_dump(r["file_path"], self.roots()) as f:
                self.central_copy(r, f)
                f.seek(0)
                if r["compression"]:
                    spool = self.state_dir / "spool" / (r["id"] + ".core")
                    spool.parent.mkdir(parents=True, exist_ok=True)
                    decompress_to(f, r["compression"], spool)
                    path, name = str(spool), Path(r["file_path"]).stem  # the original name minus the suffix
                else:
                    # The uploader opens this again: /proc/self/fd/N is the file already opened
                    # (safely), whatever the path points at by now.
                    path, name = f"/proc/self/fd/{f.fileno()}", os.path.basename(r["file_path"])
                outcome, status, msg = self.uploader(self.cfg["apiBaseUrl"], key, path, self.hostname, name)
        except NotWatched as e:
            outcome, status, msg = REJECTED, None, str(e)
        except (OSError, subprocess.SubprocessError, lzma.LZMAError) as e:
            outcome, status, msg = TRANSIENT, None, str(e)
        finally:
            if spool:
                spool.unlink(missing_ok=True)
        return self.record_outcome(r, outcome, status, msg)

    def record_outcome(self, r, outcome, status, msg):
        """What an upload's outcome does to the row: submitted, failed, retried later, or the key paused."""
        if outcome == SUCCESS:
            log.info("submitted %s", r["file_path"])
            self.queue.set_meta("auth_refusals", "0")
            self.queue.update(r["id"], state=SUBMITTED, submitted_at_utc=iso(self.clock()), last_error=None)
        elif outcome in (REJECTED, QUOTA):
            log.warning("dump %s rejected (%s): %s", r["file_path"], status, msg)
            self.queue.update(r["id"], state=FAILED, last_error=f"{status}: {msg}")
        elif outcome == AUTH:
            # Not the dump's fault and no attempt used: the key is wrong, revoked or rotated.
            stamp = self.used_key_stamp  # the key this upload used, not one stored meanwhile
            streak = int(self.queue.meta("auth_refusals") or 0) + 1 if self.queue.meta("auth_refused_key_stamp") == stamp else 1
            pause = min(AUTH_BACKOFF_SECONDS * 2 ** (streak - 1), 6 * 3600)
            log.error("upload of %s refused (%s): the API key is invalid or revoked; store a new one with "
                      "`dumptriage-agent set-api-key`; uploads paused for %d minutes or until then",
                      r["file_path"], status, pause // 60)
            self.queue.update(r["id"], state=QUEUED, last_error=f"{status}: authentication failed")
            self.queue.set_meta("auth_refusals", str(streak))
            self.queue.set_meta("auth_refused_key_stamp", stamp)
            self.queue.set_meta("auth_backoff_until_utc", iso(self.clock() + timedelta(seconds=pause)))
        else:
            n = r["retry_count"] + 1
            if n >= self.cfg["maxRetries"]:
                log.error("dump %s failed after %d attempts: %s", r["file_path"], n, msg)
                self.queue.update(r["id"], state=FAILED, retry_count=n, last_error=msg)
            else:
                delay = min(30 * 2 ** n, 3600)
                log.warning("upload of %s failed (%s: %s); retry in %ds", r["file_path"], status, msg, delay)
                self.queue.update(r["id"], state=QUEUED, retry_count=n, last_error=msg,
                                  next_attempt_utc=iso(self.clock() + timedelta(seconds=delay)))
        return outcome

    def central_copy(self, r, f):
        cc = self.cfg["centralCopy"]
        if not cc["enabled"] or r["central_copied_at_utc"] or not cc["destinationPath"]:
            return
        try:
            dest = Path(cc["destinationPath"])
            if cc["organizeByHostname"]:
                dest /= socket.gethostname()
            dest.mkdir(parents=True, exist_ok=True)
            # Never over another dump's copy (kdump names every dump "vmcore", #10): the plain
            # name if it is free, else name.<id8>, which only this dump uses (as on Windows).
            name = Path(r["file_path"]).name
            stem, ext = os.path.splitext(name)
            plain, ours = dest / name, dest / f"{stem}.{r['id'][:8]}{ext}"
            st = os.fstat(f.fileno())
            for done in (plain, ours):
                try:
                    d = done.stat()
                except OSError:
                    continue
                # Within 2 s: SMB and some NFS servers keep coarser timestamps than the local disk.
                if d.st_size == st.st_size and abs(d.st_mtime_ns - st.st_mtime_ns) < 2_000_000_000:
                    break  # copied by an attempt that died before recording it
            else:
                # Written under a temporary name, created 0600 and given the dump's own mode (as copy2
                # did: a core is someone's memory), then linked in without replacing anything, so a
                # failed or killed copy never leaves a partial file under a real name.
                tmp = dest / f".{stem}.{r['id'][:8]}{ext}.partial"
                try:
                    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
                    with os.fdopen(fd, "wb") as out:
                        os.fchmod(out.fileno(), stat.S_IMODE(st.st_mode))
                        shutil.copyfileobj(f, out, 1 << 20)
                    os.utime(tmp, ns=(st.st_atime_ns, st.st_mtime_ns))
                    try:
                        os.link(tmp, plain)
                    except FileExistsError:
                        os.replace(tmp, ours)
                    except OSError:  # no hard links on this filesystem (an SMB share): check, then move
                        os.replace(tmp, ours if plain.exists() else plain)
                finally:
                    tmp.unlink(missing_ok=True)
            self.queue.update(r["id"], central_copied_at_utc=iso(self.clock()), central_copy_error=None)
        except OSError as e:  # best effort: submission still proceeds
            log.warning("central copy of %s failed: %s", r["file_path"], e)
            self.queue.update(r["id"], central_copy_error=str(e))

    def tick(self, trace=False):
        settling = self.scan()
        self.resolve_asks()
        if trace:
            try:
                self.trace_tick()
            except Exception:  # a tracing fault must not stop dumps being submitted
                log.exception("tracing failed this tick; dumps are unaffected")
        self.submit_due()
        return settling

    # --- performance tracing -----------------------------------------------------------------------------------

    def trace_dir(self):
        return self.state_dir / "trace"

    def host_token(self):
        """A random per-install token for manifest.json's hostToken: stable, and not derived from the hostname (a hash of
        a guessable hostname is reversible by dictionary)."""
        token = self.queue.meta("host_token")
        if token is None:
            token = uuid.uuid4().hex[:16]
            self.queue.set_meta("host_token", token)
        return token

    def _write_trace_status(self, **fields):
        ts = self.trace_state
        status = {"enabled": self.cfg["tracing"]["enabled"], "backend": ts.backend, "backendNote": ts.backend_note,
                  "recorder": "running" if ts.recorder and ts.recorder.alive() else "stopped",
                  "error": ts.error, "overheadPercent": None if ts.overhead is None else round(ts.overhead, 3),
                  "updatedUtc": iso(self.clock()), **fields}
        previous = {}
        try:
            previous = json.loads((self.trace_dir() / "status.json").read_text())
        except (OSError, ValueError):
            pass
        for key in ("lastCaptureUtc", "lastManual"):
            status.setdefault(key, previous.get(key))
        try:
            self.trace_dir().mkdir(parents=True, exist_ok=True)
            tmp = self.trace_dir() / "status.json.tmp"
            tmp.write_text(json.dumps(status, indent=1))
            os.replace(tmp, self.trace_dir() / "status.json")
        except OSError as e:
            log.debug("cannot write trace status: %s", e)

    def _trace_history(self):
        try:
            return [float(x) for x in json.loads(self.queue.meta("trace_captures") or "[]")]
        except (ValueError, TypeError):
            return []

    def _trace_setup(self):
        """Once per run: check the config, build the readers and runners. A bad config disables tracing (fail closed)."""
        ts, tr = self.trace_state, self.cfg["tracing"]
        ts.validated = True
        problems = validate_tracing(tr)
        if problems:
            ts.disabled, ts.error = True, "; ".join(problems)
            for problem in problems:
                log.error("tracing is off: %s", problem)
            return
        ts.sampler = ProcSampler(self.proc_root, want_processes=any(
            t["type"] == "process" or (t["type"] == "cpuPercent" and t.get("process")) for t in tr["triggers"]))
        for trig in tr["triggers"]:
            if trig["type"] == "hangEvent":
                if not ts.warned_hang:
                    log.warning("trigger %s (hangEvent) has no source on Linux; it never fires", trig["name"])
                    ts.warned_hang = True
            elif trig["type"] != "schedule":
                ts.runners.append((trig, TriggerRunner(trig)))
        ts.backend = tr["backend"]
        if ts.backend == "perfetto":
            reason = self._perfetto_unavailable()
            if reason:
                log.warning("the perfetto backend is not available (%s); using perf", reason)
                ts.backend, ts.backend_note = "perf", reason
        if ts.backend == "perf":
            if self.perf_bin is None:
                self.perf_bin = shutil.which("perf")
            if not self.perf_bin:
                ts.disabled, ts.error = True, "perf is not installed (run linux/configure-tracing.sh)"
                log.error("tracing is off: %s", ts.error)

    def _perfetto_unavailable(self):
        """Why the perfetto backend cannot run here (None if it can): tracebox, the flight-recorder config and tracefs."""
        ts = self.trace_state
        here = Path(__file__).resolve().parent
        if self.tracebox_bin is None:
            candidates = [os.environ.get("DUMPTRIAGE_TRACEBOX"), str(here / "tracebox"), "/usr/local/lib/dumptriage/tracebox",
                          str(Path.home() / ".local/lib/dumptriage/tracebox"), shutil.which("tracebox")]
            self.tracebox_bin = next((c for c in candidates if c and os.access(c, os.X_OK) and Path(c).is_file()), None)
        if not self.tracebox_bin or not (Path(self.tracebox_bin).is_file() and os.access(self.tracebox_bin, os.X_OK)):
            return "tracebox is not installed (installer/install.sh --with-perfetto)"
        templates = [here / "perfetto" / "flight-recorder.pbtx", Path("/usr/local/lib/dumptriage/perfetto/flight-recorder.pbtx")]
        template = next((t for t in templates if t.is_file()), None)
        if template is None:
            return "the flight-recorder config perfetto/flight-recorder.pbtx is missing"
        ts.template = template.read_text()
        if not any(os.access(os.path.join(d, "available_events"), os.R_OK) for d in self.tracefs_dirs):
            return "tracefs is not readable here (not mounted, or not running as root)"
        return None

    def trace_tick(self):
        tr = self.cfg["tracing"]
        ts = self.trace_state
        if not tr["enabled"]:
            return
        if not ts.validated:
            self._trace_setup()
        if ts.disabled:
            return
        now = self.clock()
        t = now.timestamp()
        limits = tr["limits"]
        self.trace_housekeeping()
        if ts.backend_note and tr["backend"] == "perfetto" and t >= ts.backend_check_at:
            # Fell back to perf: look again now and then (tracefs mounted late, tracebox installed since, a config restored).
            ts.backend_check_at = t + BACKEND_RECHECK_SECONDS
            if self._perfetto_unavailable() is None:
                log.info("the perfetto backend is available now; switching from perf")
                if ts.recorder is not None:
                    ts.recorder.stop()
                    ts.recorder = None
                ts.backend, ts.backend_note = "perfetto", None
        if ts.recorder is None:
            if ts.backend == "perfetto":
                names = [t["name"] for t in tr["triggers"] if t["type"] != "hangEvent"] + ["manual"]
                ts.recorder = PerfettoRecorder(self.tracebox_bin, ts.template, self.trace_dir(), self._ncpu(), limits, names, self.proc_root,
                                               float(self.queue.meta("perfetto_bytes_per_sec") or 0) or None)
            else:
                ts.recorder = PerfRecorder(self.perf_bin, self.trace_dir(), self._ncpu(), limits["maxDiskMB"], self.proc_root)
        if ts.recorder.proc is not None and not ts.recorder.alive() and ts.recorder.ended_cleanly():
            log.info("the perfetto session ran out its timeout with no trigger; starting a new one")
            ts.recorder.discard_output()
            ts.recorder.proc = None
        if ts.recorder.proc is not None and not ts.recorder.alive():
            # It started and then died (killed, out of memory, a perf error): wait before starting it again, so a recorder that
            # dies every few seconds is not restarted, with the start grace each time, on every tick.
            ts.error = f"the {ts.recorder.kind} recorder exited ({ts.recorder.proc.returncode}): {ts.recorder.tail()}"
            ts.recorder.proc = None
            ts.recorder_retry_at = t + RECORDER_RETRY_SECONDS
            log.error("%s; trying again in %d s", ts.error, RECORDER_RETRY_SECONDS)
        if not ts.recorder.alive():
            if t < ts.recorder_retry_at:
                self._write_trace_status()
                return
            try:
                ts.recorder.start()
                ts.error = None
                log.info("trace recorder started: %s", " ".join(ts.recorder.command))
            except (OSError, subprocess.SubprocessError) as e:
                ts.error = f"the {ts.recorder.kind} recorder could not start: {e}"
                ts.recorder_retry_at = t + RECORDER_RETRY_SECONDS
                log.error("%s; trying again in %d s", ts.error, RECORDER_RETRY_SECONDS)
                self._write_trace_status()
                return
        metrics = ts.sampler.read(t)
        ts.recorder.ncpu = ts.sampler.ncpu
        overhead = ts.recorder.overhead_percent(t)
        if overhead is not None:
            ts.overhead = overhead
            if overhead > limits["maxOverheadPercent"]:
                ts.error = f"the {ts.recorder.kind} recorder used {overhead:.2f}% of the machine's CPU, over the {limits['maxOverheadPercent']}% limit"
                log.error("%s; stopping it for %d minutes", ts.error, OVERHEAD_BACKOFF_SECONDS // 60)
                ts.recorder.stop()
                ts.recorder_retry_at = t + OVERHEAD_BACKOFF_SECONDS
                self._write_trace_status()
                return
        met = []
        for trig, runner in ts.runners:
            value = trigger_value(trig, metrics)
            if runner.feed(t, value):
                met.append((trig, value))
        for trig in tr["triggers"]:
            if trig["type"] == "schedule":
                last = self.queue.meta(f"trace_last_{trig['name']}")
                last = float(last) if last else t  # the first baseline is one interval after the first run
                if not self.queue.meta(f"trace_last_{trig['name']}"):
                    self.queue.set_meta(f"trace_last_{trig['name']}", str(t))
                elif t - last >= trig["intervalMin"] * 60:
                    self.queue.set_meta(f"trace_last_{trig['name']}", str(t))
                    met.append((trig, None))
        request = self.trace_dir() / "capture.request"
        if request.exists():
            request.unlink(missing_ok=True)
            met.append(({"name": "manual", "type": "manual"}, None))
        for trig, observed in met:
            outcome = capture_outcome(t, limits, self._trace_history())
            if trig["type"] == "manual":
                self._write_trace_status(lastManual=f"{iso(now)} {outcome}")
            if outcome != "fire":
                log.info("trigger %s met, no capture: %s", trig["name"], outcome)
                continue
            self.capture_trace(trig, observed, now)
        self._write_trace_status()

    def _ncpu(self):
        return os.cpu_count() or 1

    def _tool_version(self):
        """`perf version 6.8.12` or `Perfetto v58.2-add693d8b (add693d...)`: the version word."""
        ts = self.trace_state
        if ts.tool_version is None:
            binary, index = (self.tracebox_bin, 1) if ts.backend == "perfetto" else (self.perf_bin, -1)
            try:
                out = subprocess.run([binary, "--version"], capture_output=True, text=True, timeout=10).stdout.splitlines()
                words = (out[0].split() if out else []) or ["", "unknown"]
                ts.tool_version = words[index] if len(words) > 1 else "unknown"
            except (OSError, subprocess.SubprocessError):
                ts.tool_version = "unknown"
        return ts.tool_version

    def capture_trace(self, trigger, observed, now):
        """Dump the ring buffer, fold it on the host, write manifest.json and queue the result. The raw perf.data is
        deleted as soon as it is folded: only text is kept, and only text leaves."""
        tr, ts = self.cfg["tracing"], self.trace_state
        limits = tr["limits"]
        t = now.timestamp()
        if not ts.recorder.alive():  # a perfetto capture ends its session: a second trigger in the same tick has nothing to stop
            log.warning("trigger %s met while no recorder session is running; no capture", trigger["name"])
            return None
        history = self._trace_history()
        # Recorded first: a capture that fails or comes up empty still counts, so a broken recorder cannot be asked in a loop.
        self.queue.set_meta("trace_captures", json.dumps([a for a in history if a > t - 2 * SECONDS_PER_DAY] + [t]))
        recorder = ts.recorder
        try:
            snapshot = recorder.snapshot(trigger["name"])
        except OSError as e:
            ts.error = f"capture for trigger {trigger['name']} failed: {e}"
            log.error(ts.error)
            return None
        folded = None
        try:
            if snapshot.stat().st_size > limits["maxDiskMB"] * 1024 * 1024:
                ts.error = f"the {recorder.kind} snapshot is over the {limits['maxDiskMB']} MB disk limit and was discarded"
                log.error(ts.error)
                snapshot.unlink(missing_ok=True)
                return None
            if recorder.kind == "perf":
                folded = self.fold_snapshot(snapshot, limits["maxDurationSec"])
        except (OSError, subprocess.SubprocessError) as e:
            ts.error = f"capture for trigger {trigger['name']} failed: {e}"
            log.error(ts.error)
            snapshot.unlink(missing_ok=True)
            return None
        finally:
            if recorder.kind == "perf":  # folded: the raw perf.data is not kept. A perfetto trace is the thing to send, adopted below.
                snapshot.unlink(missing_ok=True)
        if recorder.kind == "perf" and folded.samples == 0:
            log.warning("capture for trigger %s held no samples; nothing queued", trigger["name"])
            return None
        trace_id = hashlib.sha256(f"trace|{trigger['name']}|{t}|{uuid.uuid4().hex}".encode()).hexdigest()[:32]
        self.trace_dir().mkdir(parents=True, exist_ok=True)
        path = self.trace_dir() / f"{trace_id}-{trigger['name']}{recorder.suffix}"
        if recorder.kind == "perf":
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(fd, "w") as f:
                f.write(folded.text())
        else:
            # The binary trace as tracebox wrote it, adopted under its own name. Never read on the host.
            os.chmod(snapshot, 0o600)
            os.replace(snapshot, path)
            self.queue.set_meta("perfetto_bytes_per_sec", str(int(recorder.rate or 0)))
        manifest = self.trace_manifest(trigger, observed, now, folded)
        row = dict(id=trace_id, file_path=str(path), size_bytes=path.stat().st_size, dump_type=recorder.trace_type, compression=None,
                   state=DETECTED, detected_at_utc=iso(now), kind="trace", manifest=json.dumps(manifest))
        policy = dict(self.cfg["policy"], mode=tr["submission"])
        outcome = evaluate_policy(row, policy, now.astimezone())
        if outcome == AUTO:
            row["state"] = QUEUED
        elif outcome == ASK:
            row["state"] = PENDING
            row["ask_deadline_utc"] = iso(now + timedelta(seconds=self.cfg["policy"]["askTimeoutSeconds"]))
        elif outcome == MANUAL:
            row["state"] = PENDING
        else:
            row["state"] = SKIPPED
        self.queue.add(**row)
        self._write_trace_status(lastCaptureUtc=iso(now))
        detail = f"{folded.samples} samples, {folded.span:.0f} s" if folded is not None else f"{row['size_bytes']} bytes, {recorder.lookback:.0f} s"
        log.info("captured trace %s for trigger %s (%s) -> %s [id %s]", path.name, trigger["name"], detail, row["state"], trace_id[:12])
        if row["state"] == PENDING:
            log.warning("trace %s awaits approval: run `dumptriage-agent approve %s` or `skip %s`", path.name, trace_id[:12], trace_id[:12])
        return trace_id

    def fold_snapshot(self, snapshot, window_sec):
        """`perf script --header` over the snapshot, folded as it streams. perf runs as the agent's user, as root."""
        # stderr goes to a file, not a pipe: nothing reads a pipe while stdout is being folded, and a chatty perf would fill it and block.
        with tempfile.TemporaryFile() as errfile:
            proc = subprocess.Popen([self.perf_bin, "script", "-f", "-i", str(snapshot), "--header"], stdout=subprocess.PIPE,
                                    stderr=errfile, text=True, errors="replace")
            timer = threading.Timer(SCRIPT_TIMEOUT_SECONDS, proc.kill)
            timer.start()
            try:
                folded = fold_perf_script(proc.stdout, window_sec)
                proc.wait()
            finally:
                timer.cancel()
                if proc.poll() is None:
                    proc.kill()
                    proc.wait()
            errfile.seek(0)
            err = errfile.read(65536).decode("utf-8", "replace")
        if proc.returncode != 0 and folded.samples == 0:
            raise OSError(f"perf script exited with {proc.returncode}: {' '.join(err.split())[-300:]}")
        return folded

    def trace_manifest(self, trigger, observed, now, folded):
        """manifest.json (schemas/trace-manifest.schema.json). The window is the folded samples' own span ending when
        the trigger fired: the header's `sample duration` is 0 in a --switch-output dump, so it is not used."""
        ts = self.trace_state
        span = folded.span if folded is not None else ts.recorder.lookback
        trig = {"name": trigger["name"], "type": trigger["type"]}
        if trigger.get("threshold") is not None:
            trig["threshold"] = trigger["threshold"]
        if isinstance(observed, (int, float)):
            trig["observed"] = round(float(observed), 3)
        if trigger.get("process"):
            trig["process"] = trigger["process"]
        return {
            "schemaVersion": 1,
            "traceType": ts.recorder.trace_type,
            "trigger": trig,
            "window": {"startUtc": iso(now - timedelta(seconds=span)), "stopUtc": iso(now), "lookbackSec": int(span)},
            "tool": {"name": ts.recorder.kind, "version": self._tool_version(), "command": redact_home(" ".join(ts.recorder.command))},
            "os": {"family": "linux", "build": platform.release()},
            "hostToken": self.host_token(),
            "agentVersion": AGENT_VERSION,
        }

    def submit_trace(self, r, key):
        path = Path(r["file_path"])
        if not path.exists():
            return self.record_outcome(r, REJECTED, None, "the folded stacks file is gone")
        self.queue.update(r["id"], state=SUBMITTING)
        binary = r["dump_type"] == PERFETTO_TRACE_TYPE
        outcome, status, msg = self.trace_uploader(self.cfg["apiBaseUrl"], key, str(path), r["manifest"], self.hostname, path.name,
                                                   content_type="application/octet-stream" if binary else "text/plain")
        if outcome == AUTH and status == 403 and ("traces_disabled" in (msg or "") or "not enabled" in (msg or "")):
            outcome = TRANSIENT  # the service has traces off: not the key's fault, try again later
        result = self.record_outcome(r, outcome, status, msg)
        if outcome == SUCCESS:
            path.unlink(missing_ok=True)
        return result

    def trace_housekeeping(self):
        """Delete what is no longer needed and keep the trace directory inside maxDiskMB: folded files of submitted or
        skipped traces, leftover perf snapshots, then the oldest still-queued folded files (their rows fail)."""
        d = self.trace_dir()
        if not d.exists():
            return
        rows = {Path(r["file_path"]).name: r for r in self.queue.db.execute("SELECT * FROM dumps WHERE kind = 'trace'")}
        now = time.time()
        for f in d.iterdir():
            if f.name.startswith("perf.data.") and now - f.stat().st_mtime > 300:
                f.unlink(missing_ok=True)  # a dump nobody folded
            elif f.suffix in (".folded", PERFETTO_SUFFIX):
                row = rows.get(f.name)
                if row is None or row["state"] in (SUBMITTED, SKIPPED):
                    f.unlink(missing_ok=True)
        budget = self.cfg["tracing"]["limits"]["maxDiskMB"] * 1024 * 1024
        files = sorted((f for f in d.iterdir() if f.is_file()), key=lambda f: f.stat().st_mtime)
        total = sum(f.stat().st_size for f in files)
        for f in files:
            if total <= budget:
                break
            if f.suffix in (".folded", PERFETTO_SUFFIX) or f.name.startswith("perf.data."):
                total -= f.stat().st_size
                f.unlink(missing_ok=True)
                if f.name in rows and rows[f.name]["state"] not in (SUBMITTED, SKIPPED, FAILED):
                    log.warning("dropped %s: the trace directory was over the %d MB limit", f.name, budget // (1024 * 1024))
                    self.queue.update(rows[f.name]["id"], state=FAILED, last_error="dropped: over the maxDiskMB limit")

    def stop_tracing(self):
        if self.trace_state.recorder is not None:
            self.trace_state.recorder.stop()

    def run(self, once=False, stop=lambda: False):
        if once and self.cfg["tracing"]["enabled"]:
            log.warning("tracing needs the daemon (a recorder that stays running); `run --once` does not trace")
        try:
            while not stop():
                settling = self.tick(trace=not once)
                if once and not settling:
                    return
                time.sleep(self.cfg["pollSeconds"] if not once else min(self.cfg["pollSeconds"], 2))
        finally:
            self.stop_tracing()


# --- CLI ---------------------------------------------------------------------

def main(argv=None):
    ap = argparse.ArgumentParser(prog="dumptriage-agent", description=__doc__.splitlines()[0])
    cdir, sdir = default_dirs()
    ap.add_argument("--config-dir", default=str(cdir))
    ap.add_argument("--state-dir", default=str(sdir))
    ap.add_argument("-v", "--verbose", action="store_true")
    sub = ap.add_subparsers(dest="cmd", required=True)
    run = sub.add_parser("run", help="watch and upload (daemon)")
    run.add_argument("--once", action="store_true", help="exit when nothing is settling (for cron)")
    sub.add_parser("status", help="queue counts")
    sub.add_parser("list", help="queue contents")
    for name in ("approve", "skip", "retry"):
        sub.add_parser(name, help=f"{name} a dump by id prefix (or 'all' for approve/skip of pending)").add_argument("id")
    sub.add_parser("set-api-key", help="read the API key from stdin and store it 0600")
    sub.add_parser("trace-capture", help="ask the running daemon for a manual trace now (limits still apply)")
    args = ap.parse_args(argv)

    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO,
                        format="%(levelname)s %(message)s" if not sys.stderr.isatty() else "%(asctime)s %(levelname)s %(message)s")
    config_dir, state_dir = Path(args.config_dir), Path(args.state_dir)

    if args.cmd == "set-api-key":
        key = sys.stdin.readline().strip()
        if not key:
            ap.error("no key on stdin")
        config_dir.mkdir(parents=True, exist_ok=True)
        target = config_dir / "api-key"
        fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w") as f:
            f.write(key + "\n")
        os.chmod(target, 0o600)
        print(f"API key stored in {target}")
        return 0

    cfg = load_config(config_dir / "config.json")
    if args.cmd == "trace-capture":
        if not cfg["tracing"]["enabled"]:
            print("tracing is off in the config", file=sys.stderr)
            return 1
        (state_dir / "trace").mkdir(parents=True, exist_ok=True)
        (state_dir / "trace" / "capture.request").write_text("")
        print("requested; the daemon takes it on its next tick (see `status`)")
        return 0
    agent = Agent(cfg, config_dir, state_dir)
    q = agent.queue

    if args.cmd == "run":
        stopping = []
        for sig in (signal.SIGTERM, signal.SIGINT):
            signal.signal(sig, lambda *_: stopping.append(1))
        log.info("watching %s (policy %s)", ", ".join(cfg["watchedPaths"]), cfg["policy"]["mode"])
        agent.run(once=args.once, stop=lambda: bool(stopping))
        return 0
    if args.cmd in ("status", "list"):
        rows = q.db.execute("SELECT * FROM dumps ORDER BY detected_at_utc").fetchall()
        if args.cmd == "status":
            counts = {}
            for r in rows:
                counts[r["state"]] = counts.get(r["state"], 0) + 1
            print(json.dumps(counts))
            try:
                print("tracing: " + json.dumps(json.loads((state_dir / "trace" / "status.json").read_text())))
            except (OSError, ValueError):
                pass
        for r in rows if args.cmd == "list" else []:
            print(f"{r['id'][:12]}  {r['state']:<15} {r['dump_type']:<13} {r['size_bytes']:>12}  {r['file_path']}"
                  + (f"  ({r['last_error']})" if r["last_error"] else ""))
        return 0
    if args.id == "all" and args.cmd != "retry":
        targets = q.in_state(PENDING)
    else:
        r = q.get(args.id)
        targets = [r] if r else []
    if not targets:
        print("no matching dump", file=sys.stderr)
        return 1
    for r in targets:
        if args.cmd == "retry":
            q.update(r["id"], state=QUEUED, retry_count=0, next_attempt_utc=None, last_error=None)
        elif r["state"] == PENDING:
            q.update(r["id"], state=QUEUED if args.cmd == "approve" else SKIPPED, ask_deadline_utc=None)
        else:
            print(f"{r['id'][:12]} is {r['state']}, not pending", file=sys.stderr)
            return 1
        print(f"{r['id'][:12]} {dict(approve='approved', skip='skipped', retry='requeued')[args.cmd]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
