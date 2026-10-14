import json
import lzma
import os
import shutil
import struct
import sys
import tempfile
import threading
import unittest
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import dumptriage_agent as da  # noqa: E402


def elf(e_type=4, phdrs=(), notes=b""):
    """Minimal 64-bit little-endian ELF. phdrs: (p_type, offset, vaddr, filesz)."""
    h = bytearray(b"\x7fELF\x02\x01\x01" + b"\x00" * 9)
    h += struct.pack("<HHIQQQIHHHHHH", e_type, 62, 1, 0, 64, 0, 0, 64, 56, len(phdrs), 0, 0, 0)
    for p_type, off, vaddr, filesz in phdrs:
        h += struct.pack("<IIQQQQQQ", p_type, 0, off, vaddr, vaddr, filesz, filesz, 0)
    return bytes(h) + notes


USER_CORE = elf()
KERNEL_CORE = elf(phdrs=[(1, 0, 0xFFFF_8880_0000_0000, 4096)])
VMCOREINFO_CORE = elf(phdrs=[(4, 64 + 56, 0, 20)], notes=b"VMCOREINFO\x00OSRELEASE=6.8")


class SniffTests(unittest.TestCase):
    def test_types(self):
        self.assertEqual(da.detect_dump_type(USER_CORE), da.USER_CORE)
        self.assertEqual(da.detect_dump_type(KERNEL_CORE), da.KERNEL_DUMP)
        self.assertEqual(da.detect_dump_type(VMCOREINFO_CORE), da.KERNEL_DUMP)
        for sig in (b"KDUMP   x", b"DISKDUMPx", b"makedumpfile"):
            self.assertEqual(da.detect_dump_type(sig + b"\0" * 64), da.KERNEL_DUMP)

    def test_not_dumps(self):
        self.assertIsNone(da.detect_dump_type(elf(e_type=2)))  # executable
        self.assertIsNone(da.detect_dump_type(b"MDMP" + b"\0" * 64))  # Windows dump: not ours
        self.assertIsNone(da.detect_dump_type(b"hello"))
        self.assertIsNone(da.detect_dump_type(b""))

    def test_vsyscall_page_is_not_kernel(self):
        core = elf(phdrs=[(1, 0, 0xFFFF_FFFF_FF60_0000, 4096)])
        self.assertEqual(da.detect_dump_type(core), da.USER_CORE)


class PolicyTests(unittest.TestCase):
    NOW = datetime(2026, 10, 7, 23, 30)
    DUMP = dict(size_bytes=1000, dump_type=da.USER_CORE, file_path="/var/crash/dumptriage/core.x.1.2")

    def policy(self, **kw):
        return {**da.DEFAULT_CONFIG["policy"], **kw}

    def test_modes(self):
        for mode, want in (("AlwaysSubmit", da.AUTO), ("AskFirst", da.ASK), ("NotifyOnly", da.MANUAL)):
            self.assertEqual(da.evaluate_policy(self.DUMP, self.policy(mode=mode), self.NOW), want)

    def test_rules_first_match_wins_and_default(self):
        rules = [{"maxSizeBytes": 10, "action": "Skip"}, {"dumpType": "LinuxUserCore", "action": "Submit"}]
        p = self.policy(mode="RuleBased", rules=rules)
        self.assertEqual(da.evaluate_policy(self.DUMP, p, self.NOW), da.AUTO)
        self.assertEqual(da.evaluate_policy({**self.DUMP, "size_bytes": 5}, p, self.NOW), da.SKIP)
        self.assertEqual(da.evaluate_policy({**self.DUMP, "dump_type": da.KERNEL_DUMP}, p, self.NOW), da.ASK)

    def test_glob_and_hours(self):
        rule = {"pathGlob": "/var/crash/*", "businessHoursStart": "22:00", "businessHoursEnd": "06:00", "action": "Submit"}
        p = self.policy(mode="RuleBased", rules=[rule], defaultRuleAction="Skip")
        self.assertEqual(da.evaluate_policy(self.DUMP, p, self.NOW), da.AUTO)  # overnight window
        self.assertEqual(da.evaluate_policy(self.DUMP, p, self.NOW.replace(hour=12)), da.SKIP)

    def test_batching(self):
        b = {"mode": "ScheduledTimeOfDay", "scheduledTimesLocal": ["02:00"]}
        self.assertFalse(da.should_submit_now(b, datetime(2026, 10, 7, 1, 0), datetime(2026, 10, 6, 3, 0)))
        self.assertTrue(da.should_submit_now(b, datetime(2026, 10, 7, 3, 0), datetime(2026, 10, 6, 3, 0)))
        self.assertFalse(da.should_submit_now(b, datetime(2026, 10, 7, 3, 0), datetime(2026, 10, 7, 2, 30)))
        self.assertTrue(da.should_submit_now({"mode": "Immediate"}, self.NOW, None))


class FakeUploader:
    def __init__(self, *outcomes):
        self.outcomes, self.calls = list(outcomes), []

    def __call__(self, url, key, path, hostname, name):
        self.calls.append((url, key, name, hostname, Path(path).read_bytes()))
        return self.outcomes.pop(0) if self.outcomes else (da.SUCCESS, 201, None)


class AgentTestBase(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.watch = self.tmp / "dumps"
        self.watch.mkdir()
        self.cfgdir, self.state = self.tmp / "cfg", self.tmp / "state"
        self.cfgdir.mkdir()
        (self.cfgdir / "api-key").write_text("dts_test\n")
        self.now = datetime(2026, 10, 7, 12, 0, tzinfo=timezone.utc)

    def agent(self, uploader=None, **over):
        cfg = da.merge(da.DEFAULT_CONFIG, {"watchedPaths": [str(self.watch)], "stableSeconds": 0, "sendHostname": False,
                                           "policy": {"mode": "AlwaysSubmit"}, **over})
        self.uploader = uploader or FakeUploader()
        return da.Agent(cfg, self.cfgdir, self.state, uploader=self.uploader, clock=lambda: self.now)

    def settle(self, agent):
        agent.tick()  # first sight
        agent.tick()  # stable -> accepted and processed

    def rows(self, agent):
        return {Path(r["file_path"]).name: r["state"] for r in agent.queue.db.execute("SELECT * FROM dumps")}


class AgentTests(AgentTestBase):

    def test_detects_and_uploads_core_ignores_other_files(self):
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        (self.watch / "notes.txt").write_text("hi")
        (self.watch / "empty").write_bytes(b"")
        a = self.agent()
        self.settle(a)
        self.assertEqual(self.rows(a), {"core.a.1.2": da.SUBMITTED})
        self.assertEqual(self.uploader.calls[0][1:3], ("dts_test", "core.a.1.2"))
        self.settle(a)  # not re-submitted
        self.assertEqual(len(self.uploader.calls), 1)

    def test_walks_one_subdirectory_for_kdump_layout(self):
        d = self.watch / "202610071200"
        d.mkdir()
        (d / "dump.202610071200").write_bytes(b"makedumpfile" + b"\0" * 100)
        (d / "dmesg.202610071200").write_text("log")
        a = self.agent()
        self.settle(a)
        self.assertEqual(self.rows(a), {"dump.202610071200": da.SUBMITTED})

    def test_waits_while_file_is_growing(self):
        p = self.watch / "core.g.1.2"
        p.write_bytes(USER_CORE)
        a = self.agent(stableSeconds=0)
        self.assertTrue(a.tick())  # first sight counts as settling
        with open(p, "ab") as f:
            f.write(b"more")
        self.assertTrue(a.tick())  # size changed: still settling, nothing uploaded
        self.assertEqual(self.uploader.calls, [])
        self.assertFalse(a.tick())

    def test_ask_first_pends_then_approve_or_timeout_skips(self):
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        a = self.agent(policy={"mode": "AskFirst", "askTimeoutSeconds": 60})
        self.settle(a)
        self.assertEqual(self.rows(a), {"core.a.1.2": da.PENDING})
        self.assertEqual(self.uploader.calls, [])
        self.now += timedelta(seconds=61)
        a.tick()
        self.assertEqual(self.rows(a), {"core.a.1.2": da.SKIPPED})  # fails closed
        self.assertEqual(self.uploader.calls, [])

    def test_manual_approve_via_cli(self):
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        a = self.agent(policy={"mode": "NotifyOnly"})
        self.settle(a)
        self.now += timedelta(days=30)
        a.tick()
        self.assertEqual(self.rows(a), {"core.a.1.2": da.PENDING})  # no deadline in NotifyOnly
        (self.cfgdir / "config.json").write_text(json.dumps({"watchedPaths": [str(self.watch)]}))
        self.assertEqual(da.main(["--config-dir", str(self.cfgdir), "--state-dir", str(self.state), "approve", "all"]), 0)
        a.tick()
        self.assertEqual(self.rows(a), {"core.a.1.2": da.SUBMITTED})

    def test_transient_retries_with_backoff_then_succeeds(self):
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        a = self.agent(FakeUploader((da.TRANSIENT, 503, "down")))
        self.settle(a)
        self.assertEqual(self.rows(a), {"core.a.1.2": da.QUEUED})
        a.tick()
        self.assertEqual(len(self.uploader.calls), 1)  # backoff not elapsed
        self.now += timedelta(seconds=120)
        a.tick()
        self.assertEqual(self.rows(a), {"core.a.1.2": da.SUBMITTED})

    def test_rejected_and_max_retries_fail(self):
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        a = self.agent(FakeUploader((da.REJECTED, 400, "bad")))
        self.settle(a)
        self.assertEqual(self.rows(a), {"core.a.1.2": da.FAILED})
        (self.watch / "core.b.1.2").write_bytes(USER_CORE + b"b")
        b = self.agent(FakeUploader(*[(da.TRANSIENT, 500, "x")] * 5), maxRetries=2)
        for _ in range(4):
            b.tick()
            self.now += timedelta(hours=2)
        self.assertEqual(self.rows(b)["core.b.1.2"], da.FAILED)

    def test_rule_skip_never_uploads(self):
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        a = self.agent(policy={"mode": "RuleBased", "rules": [{"maxSizeBytes": 10, "action": "Skip"}, {"action": "Skip"}]})
        self.settle(a)
        self.assertEqual(self.rows(a), {"core.a.1.2": da.SKIPPED})
        self.assertEqual(self.uploader.calls, [])

    def test_central_copy_best_effort(self):
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        dest = self.tmp / "archive"
        a = self.agent(centralCopy={"enabled": True, "destinationPath": str(dest), "organizeByHostname": False})
        self.settle(a)
        self.assertEqual((dest / "core.a.1.2").read_bytes(), USER_CORE)
        self.assertEqual(self.rows(a), {"core.a.1.2": da.SUBMITTED})
        # unreachable destination: still submitted, error recorded
        (self.watch / "core.b.1.2").write_bytes(USER_CORE + b"b")
        blocker = self.tmp / "file"
        blocker.write_text("x")
        b = self.agent(centralCopy={"enabled": True, "destinationPath": str(blocker / "sub"), "organizeByHostname": False})
        self.settle(b)
        self.assertEqual(self.rows(b)["core.b.1.2"], da.SUBMITTED)
        self.assertTrue(b.queue.db.execute("SELECT central_copy_error FROM dumps WHERE file_path LIKE '%core.b%'").fetchone()[0])

    def test_xz_core_is_decompressed_for_upload(self):
        with lzma.open(self.watch / "core.c.1.2.xz", "wb") as f:
            f.write(USER_CORE)
        a = self.agent()
        self.settle(a)
        name, data = self.uploader.calls[0][2], self.uploader.calls[0][4]
        self.assertEqual((name, data), ("core.c.1.2", USER_CORE))
        self.assertEqual(list((self.state / "spool").iterdir()), [])  # spool cleaned up

    def test_submitting_rows_recovered_after_crash(self):
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        a = self.agent(policy={"mode": "AskFirst"})
        self.settle(a)
        row = a.queue.in_state(da.PENDING)[0]
        a.queue.update(row["id"], state=da.SUBMITTING)
        self.agent().queue.recover()
        self.assertEqual(self.agent().queue.in_state(da.QUEUED)[0]["id"], row["id"])

    def test_missing_api_key_keeps_dump_queued(self):
        (self.cfgdir / "api-key").unlink()
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        a = self.agent()
        self.settle(a)
        self.assertEqual(self.rows(a), {"core.a.1.2": da.QUEUED})
        self.assertEqual(self.uploader.calls, [])


class UploadTests(unittest.TestCase):
    def serve(self, status):
        seen = {}

        class H(BaseHTTPRequestHandler):
            def do_POST(self):
                n = int(self.headers["Content-Length"])
                seen.update(path=self.path, auth=self.headers["Authorization"], ctype=self.headers["Content-Type"],
                            body=self.rfile.read(n))
                self.send_response(status)
                self.end_headers()
                self.wfile.write(b"{}")

            def log_message(self, *a):
                pass

        srv = HTTPServer(("127.0.0.1", 0), H)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        self.addCleanup(srv.shutdown)
        return f"http://127.0.0.1:{srv.server_port}", seen

    def test_multipart_shape(self):
        url, seen = self.serve(201)
        p = Path(tempfile.mkdtemp()) / "core.x.1.2"
        p.write_bytes(USER_CORE)
        self.assertEqual(da.upload_dump(url, "dts_k", str(p), "host1"), (da.SUCCESS, 201, None))
        self.assertEqual(seen["path"], "/api/v1/dumps")
        self.assertEqual(seen["auth"], "Bearer dts_k")
        boundary = seen["ctype"].split("boundary=")[1].encode()
        self.assertIn(b'name="hostname"\r\n\r\nhost1', seen["body"])
        self.assertIn(b'name="file"; filename="core.x.1.2"', seen["body"])
        self.assertIn(USER_CORE, seen["body"])
        self.assertTrue(seen["body"].endswith(b"--" + boundary + b"--\r\n"))

    def test_status_mapping(self):
        p = Path(tempfile.mkdtemp()) / "c"
        p.write_bytes(USER_CORE)
        for status, want in ((400, da.REJECTED), (413, da.QUOTA), (429, da.QUOTA), (503, da.TRANSIENT), (500, da.TRANSIENT)):
            url, _ = self.serve(status)
            self.assertEqual(da.upload_dump(url, "k", str(p), None)[0], want, status)

    def test_connection_refused_is_transient(self):
        p = Path(tempfile.mkdtemp()) / "c"
        p.write_bytes(USER_CORE)
        self.assertEqual(da.upload_dump("http://127.0.0.1:1", "k", str(p), None)[0], da.TRANSIENT)




class ReviewFixTests(AgentTestBase):
    """Fixes from the 2026-10-14 agent review (dumptriage-tools #4, #9, #10, #11)."""

    def test_symlinks_and_hard_links_are_never_read(self):  # #4
        secret = self.tmp / "secret-core"
        secret.write_bytes(USER_CORE)
        (self.watch / "core.link").symlink_to(secret)
        os.link(secret, self.watch / "core.hard")
        a = self.agent()
        self.settle(a)
        self.assertEqual(self.rows(a), {})
        self.assertEqual(self.uploader.calls, [])

    def test_directory_swapped_for_symlink_after_scan_is_refused(self):  # #4
        sub = self.watch / "d"
        sub.mkdir()
        (sub / "core.a.1.2").write_bytes(USER_CORE)
        a = self.agent(policy={"mode": "AskFirst"})
        self.settle(a)
        self.assertEqual(self.rows(a), {"core.a.1.2": da.PENDING})
        elsewhere = self.tmp / "elsewhere"
        elsewhere.mkdir()
        (elsewhere / "core.a.1.2").write_bytes(USER_CORE + b"not this one")
        sub.rename(self.tmp / "d-moved")
        sub.symlink_to(elsewhere)
        with self.assertRaises(OSError):
            da.open_dump(str(sub / "core.a.1.2"), a.roots())
        a.queue.update(a.queue.in_state(da.PENDING)[0]["id"], state=da.QUEUED)
        a.submit_due()
        self.assertEqual(self.uploader.calls, [])
        self.assertEqual(self.rows(a), {"core.a.1.2": da.QUEUED})  # transient: retried, never read through the link

    def test_uploads_under_the_original_name_from_the_opened_file(self):  # #4
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        a = self.agent()
        self.settle(a)
        self.assertEqual(self.uploader.calls[0][2], "core.a.1.2")
        self.assertEqual(self.uploader.calls[0][4], USER_CORE)

    def test_central_copies_with_the_same_name_do_not_overwrite(self):  # #10
        dest = self.tmp / "archive"
        for n, body in ((1, USER_CORE), (2, USER_CORE + b"second")):
            d = self.watch / f"127.0.0.1-2026-10-0{n}"
            d.mkdir()
            (d / "vmcore").write_bytes(body)
        a = self.agent(centralCopy={"enabled": True, "destinationPath": str(dest), "organizeByHostname": False})
        self.settle(a)
        copies = sorted(p.read_bytes() for p in dest.iterdir())
        self.assertEqual(copies, sorted([USER_CORE, USER_CORE + b"second"]))

    def test_auth_failure_uses_no_retry_and_pauses_uploads(self):  # #11
        for n in (1, 2):
            (self.watch / f"core.a.{n}").write_bytes(USER_CORE + bytes([n]))
        a = self.agent(FakeUploader((da.AUTH, 401, "revoked")), maxRetries=1)
        self.settle(a)
        self.assertEqual(len(self.uploader.calls), 1)  # the second dump isn't tried with a refused key
        self.assertEqual(set(self.rows(a).values()), {da.QUEUED})
        self.assertEqual({r["retry_count"] for r in a.queue.in_state(da.QUEUED)}, {0})
        self.now += timedelta(minutes=5)
        a.tick()
        self.assertEqual(len(self.uploader.calls), 1)  # still paused
        self.now += timedelta(minutes=6)
        a.tick()
        self.assertEqual(set(self.rows(a).values()), {da.SUBMITTED})

    def test_upload_dump_maps_401_and_403_to_auth(self):  # #11
        for status in (401, 403):
            srv = HTTPServer(("127.0.0.1", 0), type("H", (BaseHTTPRequestHandler,), {
                "do_POST": lambda self, s=status: (self.rfile.read(int(self.headers["Content-Length"])), self.send_response(s), self.end_headers()),
                "log_message": lambda *a: None}))
            t = threading.Thread(target=srv.handle_request, daemon=True)
            t.start()
            p = self.tmp / "f"
            p.write_bytes(b"x")
            self.assertEqual(da.upload_dump(f"http://127.0.0.1:{srv.server_port}", "k", str(p), None)[0], da.AUTH)
            t.join()
            srv.server_close()

    @unittest.skipUnless(shutil.which("zstd"), "zstd not installed")
    def test_zst_core_is_decompressed_from_the_opened_file(self):  # #4: the decompressor reads stdin, not a path
        import subprocess
        subprocess.run(["zstd", "-q", "-o", str(self.watch / "core.z.1.2.zst")], input=USER_CORE, check=True)
        a = self.agent()
        self.settle(a)
        self.assertEqual(self.uploader.calls[0][2:5:2], ("core.z.1.2", USER_CORE))

    def test_scheduled_slot_with_nothing_queued_is_still_used(self):  # #9
        batching = {"mode": "ScheduledTimeOfDay", "scheduledTimesLocal": ["02:00"]}
        local = lambda h: datetime(2026, 10, 7, h, 0).astimezone().astimezone(timezone.utc)
        self.now = local(1)
        a = self.agent(policy={"mode": "AlwaysSubmit", "batching": batching})
        a.tick()  # first run: records now, waits for the next slot
        self.now = local(3)
        a.tick()  # 02:00 slot passes with nothing queued
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        self.now = local(9)
        self.settle(a)
        self.assertEqual(self.uploader.calls, [])  # waits for tomorrow's 02:00
        self.assertEqual(self.rows(a), {"core.a.1.2": da.QUEUED})
        self.now = local(9) + timedelta(hours=18)  # 03:00 the next day
        a.tick()
        self.assertEqual(self.rows(a), {"core.a.1.2": da.SUBMITTED})

    def test_central_copy_keeps_the_dump_mode_and_leaves_no_partial_file(self):  # review of #14
        dest = self.tmp / "archive"
        core = self.watch / "core.a.1.2"
        core.write_bytes(USER_CORE)
        core.chmod(0o600)
        a = self.agent(centralCopy={"enabled": True, "destinationPath": str(dest), "organizeByHostname": False})
        self.settle(a)
        self.assertEqual((dest / "core.a.1.2").stat().st_mode & 0o777, 0o600)

        def broken(src, dst, length=0):
            dst.write(b"partial")
            raise OSError("disk full")
        (self.watch / "core.b.1.2").write_bytes(USER_CORE + b"b")
        orig, da.shutil.copyfileobj = da.shutil.copyfileobj, broken
        try:
            self.settle(a)
        finally:
            da.shutil.copyfileobj = orig
        self.assertFalse((dest / "core.b.1.2").exists())

    def test_auth_pause_doubles_and_a_new_key_ends_it(self):  # review of #14
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        a = self.agent(FakeUploader(*[(da.AUTH, 401, "revoked")] * 2))
        self.settle(a)  # refusal 1: 10 min
        self.now += timedelta(minutes=11)
        a.tick()  # refusal 2: 20 min
        self.assertEqual(len(self.uploader.calls), 2)
        self.now += timedelta(minutes=15)
        a.tick()
        self.assertEqual(len(self.uploader.calls), 2)  # still paused (20 min)
        key = self.cfgdir / "api-key"
        key.write_text("dts_new\n")
        os.utime(key, ns=(1, 2))  # a different mtime, whatever the clock resolution
        a.tick()
        self.assertEqual(self.rows(a), {"core.a.1.2": da.SUBMITTED})

    def test_scheduled_slot_during_an_auth_pause_is_taken_after_it(self):  # review of #14
        batching = {"mode": "ScheduledTimeOfDay", "scheduledTimesLocal": ["02:00"]}
        local = lambda h, m=0: datetime(2026, 10, 7, h, m).astimezone().astimezone(timezone.utc)
        self.now = local(1)
        a = self.agent(FakeUploader((da.AUTH, 401, "revoked")), policy={"mode": "AlwaysSubmit", "batching": batching})
        a.tick()
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        self.settle(a)  # queued, waiting for 02:00
        self.now = local(2, 1)
        a.tick()  # refused: paused until 02:11
        self.now = local(2, 5)
        a.tick()
        self.now = local(2, 12)
        a.tick()  # the 02:00 slot was not used up by the refused attempt
        self.assertEqual(self.rows(a), {"core.a.1.2": da.SUBMITTED})

    def test_watched_root_through_a_planted_symlink_is_not_read(self):  # review of #14
        shared = self.tmp / "shared"
        shared.mkdir()
        shared.chmod(0o1777)
        (shared / "dumptriage").symlink_to(self.watch)  # as if another user planted it
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        orig = os.geteuid
        os.geteuid = lambda: orig() + 12345  # the link (and the rest) now belongs to "someone else"
        try:
            a = self.agent(watchedPaths=[str(shared / "dumptriage")])
            self.settle(a)
            with self.assertRaises(da.NotWatched):
                da.open_dump(str(shared / "dumptriage" / "core.a.1.2"), a.roots())
        finally:
            os.geteuid = orig
        self.assertEqual(self.rows(a), {})
        self.assertEqual(self.uploader.calls, [])

    def test_dump_no_longer_under_a_watched_path_fails_with_a_reason(self):  # review of #14
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        a = self.agent(policy={"mode": "AskFirst"})
        self.settle(a)
        a.queue.update(a.queue.in_state(da.PENDING)[0]["id"], state=da.QUEUED)
        a.cfg = da.merge(a.cfg, {"watchedPaths": [str(self.tmp / "other")]})
        a.submit_due()
        row = a.queue.db.execute("SELECT state, last_error FROM dumps").fetchone()
        self.assertEqual(row["state"], da.FAILED)
        self.assertIn("watchedPaths", row["last_error"])


    def test_central_copy_is_not_repeated_after_a_crash_before_it_was_recorded(self):  # review of #14
        dest = self.tmp / "archive"
        (self.watch / "core.a.1.2").write_bytes(USER_CORE)
        a = self.agent(policy={"mode": "AskFirst"},
                       centralCopy={"enabled": True, "destinationPath": str(dest), "organizeByHostname": False})
        self.settle(a)
        r = a.queue.in_state(da.PENDING)[0]
        with da.open_dump(r["file_path"], a.roots()) as f:
            a.central_copy(r, f)
        a.queue.update(r["id"], central_copied_at_utc=None)  # as if killed before the queue recorded it
        with da.open_dump(r["file_path"], a.roots()) as f:
            a.central_copy(a.queue.get(r["id"]), f)
        self.assertEqual(sorted(p.name for p in dest.iterdir()), ["core.a.1.2"])

    def test_trusted_root_rules(self):  # review of #14
        orig = os.geteuid
        def as_other(fn):
            os.geteuid = lambda: orig() + 12345
            try:
                return fn()
            finally:
                os.geteuid = orig
        safe = self.tmp / "safe"
        (safe / "app" / "dumps").mkdir(parents=True)
        safe.chmod(0o755)  # whatever the umask: only we can write here
        # Real folders owned by someone else (an app's service user) are fine.
        self.assertTrue(as_other(lambda: da.trusted_root(str(safe / "app" / "dumps"))))
        # A symlink only its trusted owner can replace is fine; one owned by someone else is not.
        (safe / "link").symlink_to(safe / "app")
        self.assertTrue(da.trusted_root(str(safe / "link" / "dumps")))
        self.assertFalse(as_other(lambda: da.trusted_root(str(safe / "link" / "dumps"))))
        # Our own symlink in a group/other-writable, non-sticky folder could be replaced: refused.
        loose = self.tmp / "loose"
        loose.mkdir()
        loose.chmod(0o777)
        (loose / "link").symlink_to(safe / "app")
        self.assertFalse(da.trusted_root(str(loose / "link" / "dumps")))
        # ... but in a sticky one only we can replace it.
        loose.chmod(0o1777)
        self.assertTrue(da.trusted_root(str(loose / "link" / "dumps")))


if __name__ == "__main__":
    unittest.main()
