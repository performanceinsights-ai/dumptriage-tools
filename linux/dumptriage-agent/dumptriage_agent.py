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
import shutil
import signal
import socket
import sqlite3
import stat
import struct
import subprocess
import sys
import time
import urllib.parse
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

log = logging.getLogger("dumptriage")

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


def trusted_root(root):
    """Whether a watched root's path can be trusted not to lead somewhere another user chose.

    /var/crash is world-writable, so another user could create /var/crash/dumptriage before
    configure-dumps.sh does. As a real folder that only holds files that user could write anyway,
    and open_dump reads nothing through a symlink or hard link below it. As a symlink it could
    point anywhere, so a symlink anywhere on the path is trusted only if it belongs to root or the
    agent's user and sits in a folder no one else can replace it in: one only they can write to, or
    a sticky one (like /tmp). An app's own dump folder owned by its service user is fine."""
    path = os.path.abspath(root)
    try:
        if not stat.S_ISDIR(os.stat(path).st_mode):
            return False
    except OSError:
        return False
    ok = (0, os.geteuid())
    cur = "/"
    for part in path.strip("/").split("/"):
        nxt = os.path.join(cur, part)
        try:
            link, parent = os.lstat(nxt), os.stat(cur)
        except OSError:
            return False
        if stat.S_ISLNK(link.st_mode):
            others_can_write = parent.st_uid not in ok or parent.st_mode & (stat.S_IWGRP | stat.S_IWOTH)
            if link.st_uid not in ok or (others_can_write and not parent.st_mode & stat.S_ISVTX):
                return False
            cur = os.path.realpath(nxt)
        else:
            cur = nxt
    return True


def open_dump(path, roots):
    """Open a dump for reading without following any symlink below its watched root (#4).

    Capture folders such as /var/crash/dumptriage are world-writable, and the system agent runs as
    root: a symlink planted there (to /proc/kcore, or another user's core) must not be read, uploaded
    or copied. Every component below the root is opened with O_NOFOLLOW, relative to its parent, so a
    directory swapped for a symlink after the scan is refused too, and the file must be a regular file
    with a single link. The root itself is the admin's configuration and may be a symlink."""
    path = os.path.abspath(path)
    root = max((r for r in roots if path.startswith(r.rstrip("/") + "/")), key=len, default=None)
    if root is None:
        raise NotWatched(errno.EPERM, "no longer under a watched path; add its folder back to watchedPaths and run `retry`", path)
    if not trusted_root(root):
        raise NotWatched(errno.EPERM, f"its watched path {root} goes through a symlink another user could have planted", path)
    parts = os.path.relpath(path, root).split(os.sep)
    dir_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY)
    try:
        for part in parts[:-1]:
            next_fd = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=dir_fd)
            os.close(dir_fd)
            dir_fd = next_fd
        fd = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=dir_fd)
    finally:
        os.close(dir_fd)
    st = os.fstat(fd)
    if not stat.S_ISREG(st.st_mode) or st.st_nlink != 1:
        os.close(fd)
        raise OSError(errno.EPERM, "not a regular file with a single link", path)
    return os.fdopen(fd, "rb")


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
    def __init__(self, path):
        Path(path).parent.mkdir(parents=True, exist_ok=True)
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

class Agent:
    def __init__(self, config, config_dir, state_dir, queue=None, uploader=upload_dump, clock=utcnow):
        self.cfg, self.config_dir, self.state_dir = config, Path(config_dir), Path(state_dir)
        self.queue = queue or Queue(self.state_dir / "queue.db")
        self.uploader, self.clock = uploader, clock
        self.stable = {}  # path -> (size, mtime_ns, first_seen_unchanged_monotonic)
        self.ignored = set()  # (path, size, mtime_ns) already sniffed as not-a-dump
        self.untrusted = set()  # watched roots refused by trusted_root, logged once
        self.hostname = socket.gethostname() if config["sendHostname"] else None
        self.queue.recover()

    def api_key(self):
        key_file = self.config_dir / "api-key"
        return key_file.read_text().strip() if key_file.exists() else None

    # discovery
    def candidates(self):
        for root in self.cfg["watchedPaths"]:
            root = os.path.expanduser(root)
            if os.path.exists(root) and not trusted_root(root):
                if root not in self.untrusted:
                    log.error("not watching %s: its path goes through a symlink another user could have planted", root)
                    self.untrusted.add(root)
                continue
            base_depth = root.rstrip("/").count("/")
            for dirpath, dirs, files in os.walk(root, onerror=lambda e: None):
                if dirpath.count("/") - base_depth >= 2:
                    dirs[:] = []
                for name in files:
                    yield os.path.join(dirpath, name)

    def scan(self):
        """Returns True while any candidate is still settling (so --once knows to keep going)."""
        settling, live = False, set()
        for path in self.candidates():
            try:
                st = os.lstat(path)
            except OSError:
                continue
            # Not a symlink, not a hard link: see open_dump (#4).
            if not stat.S_ISREG(st.st_mode) or st.st_nlink != 1 or st.st_size == 0:
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
        return [os.path.abspath(os.path.expanduser(r)) for r in self.cfg["watchedPaths"]]

    def submit_one(self, r, key):
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

    def tick(self):
        settling = self.scan()
        self.resolve_asks()
        self.submit_due()
        return settling

    def run(self, once=False, stop=lambda: False):
        while not stop():
            settling = self.tick()
            if once and not settling:
                return
            time.sleep(self.cfg["pollSeconds"] if not once else min(self.cfg["pollSeconds"], 2))


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
