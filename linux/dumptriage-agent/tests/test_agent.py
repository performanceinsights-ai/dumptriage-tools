import json
import lzma
import os
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
        self.assertFalse(da.should_submit_now(b, datetime(2026, 10, 7, 1, 0), None))
        self.assertTrue(da.should_submit_now(b, datetime(2026, 10, 7, 3, 0), None))
        self.assertFalse(da.should_submit_now(b, datetime(2026, 10, 7, 3, 0), datetime(2026, 10, 7, 2, 30)))
        self.assertTrue(da.should_submit_now({"mode": "Immediate"}, self.NOW, None))


class FakeUploader:
    def __init__(self, *outcomes):
        self.outcomes, self.calls = list(outcomes), []

    def __call__(self, url, key, path, hostname):
        self.calls.append((url, key, os.path.basename(path), hostname, Path(path).read_bytes()))
        return self.outcomes.pop(0) if self.outcomes else (da.SUCCESS, 201, None)


class AgentTests(unittest.TestCase):
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


if __name__ == "__main__":
    unittest.main()
