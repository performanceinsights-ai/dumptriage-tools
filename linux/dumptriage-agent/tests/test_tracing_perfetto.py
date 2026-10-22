"""The perfetto backend of the Linux agent (dumptriage-tools#28): config rendering, the trigger call, the session life cycle,
limits and the fall back to perf, against a fake tracebox (tests/fake_tracebox.py)."""
import json
import os
import shutil
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import dumptriage_agent as da  # noqa: E402

try:
    from test_tracing import DISTROS, FIXTURES, MANIFEST_SCHEMA, FakeProc, jsonschema
except ImportError:  # run as tests.test_tracing_perfetto
    from tests.test_tracing import DISTROS, FIXTURES, MANIFEST_SCHEMA, FakeProc, jsonschema

HERE = Path(__file__).resolve().parent
FAKE_TRACEBOX = HERE / "fake_tracebox.py"
TEMPLATE = (HERE.parent / "perfetto" / "flight-recorder.pbtx").read_text()
LIMITS = {"maxDurationSec": 60, "maxDiskMB": 512, "maxOverheadPercent": 2, "cooldownMin": 60, "dailyCap": 4}


class ConfigTests(unittest.TestCase):
    def test_the_shipped_template_has_every_marker_and_the_data_sources_the_ticket_names(self):
        for marker in ("{{BUFFER_KB}}", "{{TRIGGER_TIMEOUT_MS}}", "{{TRIGGERS}}"):
            self.assertEqual(TEMPLATE.count(marker), 1, marker)
        for needle in ("RING_BUFFER", "linux.ftrace", "sched/sched_switch", "sched/sched_waking", "power/cpu_frequency", "irq/softirq_entry",
                       "block/block_rq_issue", "linux.process_stats", "linux.perf", "callstack_sampling", "trigger_mode: STOP_TRACING"):
            self.assertIn(needle, TEMPLATE)

    def test_rendering_gives_one_trigger_per_name_all_stopping_the_session(self):
        text = da.render_perfetto_config(TEMPLATE, 32768, ["cpu-high", "sql busy", "manual", "cpu-high"], stop_delay_ms=1500, timeout_ms=86400000)
        self.assertNotIn("{{", text)
        self.assertIn("buffers { size_kb: 32768 fill_policy: RING_BUFFER }", text)
        self.assertIn("trigger_timeout_ms: 86400000", text)
        self.assertEqual([l.strip() for l in text.splitlines() if l.strip().startswith("triggers {")], [
            'triggers { name: "pi_cpu-high" stop_delay_ms: 1500 }',
            'triggers { name: "pi_sql_busy" stop_delay_ms: 1500 }',  # a name perfetto's trigger CLI could not carry is made safe
            'triggers { name: "pi_manual" stop_delay_ms: 1500 }',
        ])
        self.assertEqual(da.trigger_name_for("sql busy"), "pi_sql_busy")

    def test_the_buffer_is_sized_from_the_measured_rate_within_the_caps(self):
        def kb(rate, **limits):
            rec = da.PerfettoRecorder("tracebox", TEMPLATE, "/tmp", 8, dict(LIMITS, **limits), ["x"], rate_bytes_per_sec=rate)
            return rec.buffer_kb()

        self.assertEqual(kb(None), 32768)  # the first session guesses
        self.assertEqual(kb(500_000), int(500_000 * 60 * 1.25 / 1024))  # 0.5 MB/s for 60 s, a quarter spare
        self.assertEqual(kb(10_000_000), 49152 - 2048)  # never above what the service takes, less the process buffer the file also holds
        self.assertEqual(kb(1000), 8192)  # nor below 8 MiB
        self.assertEqual(kb(None, maxDiskMB=20), 10240 - 2048)  # half of a 20 MB disk budget, less the process buffer
        self.assertEqual(kb(10_000_000, maxDiskMB=8), 2048)  # never under the process buffer's own size

    def test_perfetto_is_an_accepted_backend(self):
        t = json.loads(json.dumps(da.DEFAULT_CONFIG["tracing"]))
        t["backend"] = "perfetto"
        t["triggers"] = [{"name": "x", "type": "cpuPercent", "threshold": 1}]
        self.assertEqual(da.validate_tracing(t), [])
        t["backend"] = "wpr"
        self.assertIn("perf and perfetto are", "; ".join(da.validate_tracing(t)))

    def test_descendant_cpu_adds_the_children_tracebox_started(self):
        tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, tmp)
        fp = FakeProc(tmp)
        for pid, comm, ppid, ticks in ((100, "tracebox", 1, 10), (101, "traced", 100, 20), (102, "traced_perf", 100, 30), (103, "x", 102, 5), (200, "other", 1, 999)):
            (tmp / str(pid)).mkdir(exist_ok=True)
            (tmp / str(pid) / "stat").write_text(f"{pid} ({comm}) S {ppid} 1 1 0 -1 0 0 0 0 0 {ticks} 0 0 0 20 0 1 0 100 0 0\n")
        self.assertEqual(da.descendant_cpu_ticks(tmp, 100), 10 + 20 + 30 + 5)
        self.assertIsNone(da.descendant_cpu_ticks(tmp, 555))
        self.assertIsNone(da.descendant_cpu_ticks(tmp / "nowhere", 100))
        del fp

    def test_the_file_limit_is_raised_for_the_children(self):
        import resource

        before = resource.getrlimit(resource.RLIMIT_NOFILE)
        self.addCleanup(resource.setrlimit, resource.RLIMIT_NOFILE, before)
        da._raise_file_limit()
        soft, hard = resource.getrlimit(resource.RLIMIT_NOFILE)
        self.assertGreaterEqual(soft, min(65536, hard if hard != resource.RLIM_INFINITY else 65536))


class PerfettoAgentTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.state, self.config = self.tmp / "state", self.tmp / "config"
        self.config.mkdir()
        self.fp = FakeProc(self.tmp / "proc")
        self.tracefs = self.tmp / "tracefs"
        self.tracefs.mkdir()
        (self.tracefs / "available_events").write_text("sched:sched_switch\n")
        self.now = datetime(2026, 10, 21, 12, 0, 0, tzinfo=timezone.utc)
        self.uploads = []
        for k, v in {"FAKE_PERF_SCRIPT": str(FIXTURES / DISTROS["ubuntu2204"][0])}.items():
            os.environ[k] = v
            self.addCleanup(os.environ.pop, k, None)
        saved = da.RECORDER_START_GRACE_SECONDS
        da.RECORDER_START_GRACE_SECONDS = 0.3
        self.addCleanup(setattr, da, "RECORDER_START_GRACE_SECONDS", saved)

    def agent(self, tracing=None, tracebox=str(FAKE_TRACEBOX), template=True, tracefs=True, **kw):
        tr = {"enabled": True, "backend": "perfetto", "triggers": [{"name": "cpu-high", "type": "cpuPercent", "threshold": 90, "sustainSec": 10}]}
        tr.update(tracing or {})
        cfg = da.merge(da.DEFAULT_CONFIG, {"sendHostname": False, "watchedPaths": [str(self.tmp / "watch")], "tracing": tr})
        (self.tmp / "watch").mkdir(exist_ok=True)

        def uploader(base, key, path, manifest, hostname, name, content_type="text/plain"):
            self.uploads.append({"manifest": manifest, "name": name, "content_type": content_type, "bytes": Path(path).read_bytes()})
            return da.SUCCESS, 201, None

        a = da.Agent(cfg, self.config, self.state, clock=lambda: self.now, trace_uploader=uploader, perf_bin=str(HERE / "fake_perf.py"),
                     proc_root=str(self.tmp / "proc"), tracebox_bin=tracebox,
                     tracefs_dirs=[str(self.tracefs) if tracefs else str(self.tmp / "no-tracefs")], **kw)
        if not template:
            self.skipTest("template-less agent is built by test_a_missing_config_falls_back")
        self.addCleanup(a.stop_tracing)
        return a

    def advance(self, agent, busy, seconds=5, **metrics):
        self.now += timedelta(seconds=seconds)
        self.fp.tick(busy, seconds, **metrics)
        agent.trace_tick()

    def hot(self, agent, ticks=4):
        for _ in range(ticks):
            self.advance(agent, 95)

    def rows(self, agent):
        return [dict(r) for r in agent.queue.db.execute("SELECT * FROM dumps WHERE kind = 'trace'")]

    def test_a_trigger_stops_the_session_and_the_binary_trace_is_queued_held_for_approval(self):
        a = self.agent()
        self.advance(a, 5)
        self.assertEqual((a.trace_state.backend, a.trace_state.recorder.kind), ("perfetto", "perfetto"))
        self.assertTrue(a.trace_state.recorder.alive())
        config = Path(str(a.trace_state.recorder.out_path) + ".config").read_text()
        self.assertIn('triggers { name: "pi_cpu-high" stop_delay_ms: 2000 }', config)
        self.assertIn('triggers { name: "pi_manual"', config)
        self.hot(a)
        (row,) = self.rows(a)
        self.assertEqual((row["state"], row["dump_type"], row["kind"]), (da.PENDING, "perfetto", "trace"))
        path = Path(row["file_path"])
        self.assertTrue(path.name.endswith("-cpu-high.perfetto-trace"))
        self.assertEqual(oct(path.stat().st_mode & 0o777), "0o600")
        self.assertTrue(path.read_bytes().startswith(b"\x0a\x0bPERFETTO-TRACE"))
        self.assertFalse(a.trace_state.recorder.out_path.exists(), "the recording was adopted under its own name")
        manifest = json.loads(row["manifest"])
        self.assertEqual(manifest["traceType"], "perfetto")
        self.assertEqual(manifest["tool"]["name"], "perfetto")
        self.assertEqual(manifest["tool"]["version"], "v58.2-add693d8b")
        self.assertIn("--txt -c ", manifest["tool"]["command"])
        self.assertEqual(manifest["trigger"]["name"], "cpu-high")
        self.assertEqual(manifest["os"]["family"], "linux")
        self.assertGreaterEqual(manifest["window"]["lookbackSec"], 0)
        if jsonschema is not None:
            jsonschema.validate(manifest, MANIFEST_SCHEMA)
        self.assertEqual(self.uploads, [], "held, not sent")
        self.assertEqual(json.loads((self.state / "trace" / "status.json").read_text())["backend"], "perfetto")

    def test_a_new_session_starts_straight_after_a_capture_and_the_buffer_follows_the_measured_rate(self):
        os.environ["FAKE_TRACEBOX_BYTES"] = "8000000"
        self.addCleanup(os.environ.pop, "FAKE_TRACEBOX_BYTES", None)
        a = self.agent()
        self.advance(a, 5)
        first = a.trace_state.recorder.proc.pid
        self.hot(a)
        self.assertEqual(len(self.rows(a)), 1)
        self.advance(a, 5)  # the next tick: no penalty wait, a new session
        self.assertTrue(a.trace_state.recorder.alive())
        self.assertNotEqual(a.trace_state.recorder.proc.pid, first)
        rate = float(a.queue.meta("perfetto_bytes_per_sec"))
        self.assertGreater(rate, 0)
        self.assertIn(f"size_kb: {a.trace_state.recorder.buffer_kb()}", Path(str(a.trace_state.recorder.out_path) + ".config").read_text())

    def test_approving_uploads_the_binary_as_an_octet_stream(self):
        a = self.agent()
        (self.config / "api-key").write_text("dts_key\n")
        self.advance(a, 5)
        self.hot(a)
        (row,) = self.rows(a)
        a.queue.update(row["id"], state=da.QUEUED, ask_deadline_utc=None)
        a.submit_due()
        (up,) = self.uploads
        self.assertEqual((up["content_type"], up["name"].endswith(".perfetto-trace")), ("application/octet-stream", True))
        self.assertTrue(up["bytes"].startswith(b"\x0a\x0bPERFETTO-TRACE"))
        self.assertEqual(json.loads(up["manifest"])["traceType"], "perfetto")
        self.assertFalse(Path(row["file_path"]).exists())
        self.assertEqual(self.rows(a)[0]["state"], da.SUBMITTED)

    def test_without_tracebox_a_template_or_tracefs_it_falls_back_to_perf_and_says_why(self):
        for kwargs, fragment in (
            ({"tracebox": str(self.tmp / "missing")}, "tracebox is not installed"),
            ({"tracefs": False}, "tracefs is not readable"),
        ):
            with self.subTest(fragment):
                a = self.agent(**kwargs)
                with self.assertLogs("dumptriage", "WARNING") as logs:
                    self.advance(a, 5)
                self.assertIn(fragment, "\n".join(logs.output))
                self.assertIn("using perf", "\n".join(logs.output))
                self.assertEqual((a.trace_state.backend, a.trace_state.recorder.kind), ("perf", "perf"))
                a.stop_tracing()

    def test_a_missing_config_falls_back_to_perf(self):
        a = self.agent()
        orig = da.Path.is_file
        da.Path.is_file = lambda p: False if p.name == "flight-recorder.pbtx" else orig(p)
        self.addCleanup(setattr, da.Path, "is_file", orig)
        with self.assertLogs("dumptriage", "WARNING") as logs:
            self.advance(a, 5)
        self.assertIn("perfetto/flight-recorder.pbtx is missing", "\n".join(logs.output))
        self.assertEqual(a.trace_state.backend, "perf")

    def test_a_fall_back_to_perf_uploads_folded_stacks(self):
        a = self.agent(tracefs=False)
        self.advance(a, 5)
        self.hot(a)
        (row,) = self.rows(a)
        self.assertEqual((row["dump_type"], json.loads(row["manifest"])["traceType"], json.loads(row["manifest"])["tool"]["name"]), ("folded", "folded", "perf"))

    def test_with_no_perf_and_no_perfetto_tracing_is_off(self):
        a = self.agent(tracefs=False)
        a.perf_bin = None
        orig = da.shutil.which
        da.shutil.which = lambda name: None
        self.addCleanup(setattr, da.shutil, "which", orig)
        with self.assertLogs("dumptriage", "ERROR") as logs:
            self.advance(a, 5)
        self.assertIn("perf is not installed", "\n".join(logs.output))

    def test_a_tracebox_that_cannot_start_is_reported_and_retried_later(self):
        os.environ["FAKE_TRACEBOX_FAIL"] = "1"
        self.addCleanup(os.environ.pop, "FAKE_TRACEBOX_FAIL", None)
        a = self.agent()
        with self.assertLogs("dumptriage", "ERROR") as logs:
            self.advance(a, 95)
        self.assertIn("the perfetto recorder could not start", "\n".join(logs.output))
        self.assertIn("Failed to start traced", "\n".join(logs.output))
        self.assertIsNone(a.trace_state.recorder.sock_dir, "the socket directory is not left behind")
        del os.environ["FAKE_TRACEBOX_FAIL"]
        self.advance(a, 95, seconds=10)
        self.assertFalse(a.trace_state.recorder.alive())
        self.advance(a, 95, seconds=da.RECORDER_RETRY_SECONDS)
        self.assertTrue(a.trace_state.recorder.alive())

    def test_a_session_that_ran_out_its_timeout_is_not_a_fault_and_its_ring_buffer_is_thrown_away(self):
        os.environ["FAKE_TRACEBOX_TIMEOUT"] = "0.5"
        self.addCleanup(os.environ.pop, "FAKE_TRACEBOX_TIMEOUT", None)
        a = self.agent({"triggers": [{"name": "never", "type": "cpuPercent", "threshold": 101, "sustainSec": 0}]})
        self.advance(a, 5)
        import time

        time.sleep(1.0)
        with self.assertLogs("dumptriage", "INFO") as logs:
            self.advance(a, 5)
        self.assertIn("ran out its timeout", "\n".join(logs.output))
        self.assertTrue(a.trace_state.recorder.alive(), "a new session was started at once")
        self.assertIsNone(a.trace_state.error)
        self.assertEqual(self.rows(a), [])
        self.assertEqual(list((self.state / "trace").glob("*.perfetto-trace")), [])

    def test_a_trace_over_the_disk_budget_is_discarded(self):
        os.environ["FAKE_TRACEBOX_BYTES"] = str(2 * 1024 * 1024)
        self.addCleanup(os.environ.pop, "FAKE_TRACEBOX_BYTES", None)
        a = self.agent({"limits": {"maxDiskMB": 1}})
        self.advance(a, 5)
        with self.assertLogs("dumptriage", "ERROR") as logs:
            self.hot(a)
        self.assertIn("perfetto snapshot is over the 1 MB disk limit", "\n".join(logs.output))
        self.assertEqual(self.rows(a), [])
        self.assertFalse(a.trace_state.recorder.out_path.exists())

    def test_a_trigger_the_session_cannot_take_is_a_failed_capture_not_a_crash(self):
        a = self.agent()
        self.advance(a, 5)
        rec = a.trace_state.recorder
        rec._cleanup()  # the sockets are gone: trigger_perfetto cannot connect
        rec.sock_dir = Path(tempfile.mkdtemp(prefix="dt-"))
        self.addCleanup(shutil.rmtree, rec.sock_dir, True)
        with self.assertLogs("dumptriage", "ERROR") as logs:
            self.hot(a)
        self.assertIn("trigger_perfetto exited with 1", "\n".join(logs.output))
        self.assertEqual(self.rows(a), [])

    def test_the_recorders_cost_includes_its_children_and_is_limited(self):
        a = self.agent({"limits": {"maxOverheadPercent": 1}})
        self.advance(a, 5)
        pid = a.trace_state.recorder.proc.pid

        def procs(tracebox_ticks, child_ticks):
            for p, comm, ppid, ticks in ((pid, "tracebox", 1, tracebox_ticks), (pid + 1000, "traced_perf", pid, child_ticks)):
                (self.tmp / "proc" / str(p)).mkdir(exist_ok=True)
                (self.tmp / "proc" / str(p) / "stat").write_text(f"{p} ({comm}) S {ppid} 1 1 0 -1 0 0 0 0 0 {ticks} 0 0 0 20 0 1 0 100 0 0\n")

        procs(0, 0)
        self.advance(a, 5)
        procs(50, 1750)  # 18 CPU-seconds over 70 s on 4 CPUs: 6.4%
        with self.assertLogs("dumptriage", "ERROR") as logs:
            self.advance(a, 5, seconds=70)
        self.assertIn("the perfetto recorder used", "\n".join(logs.output))
        self.assertFalse(a.trace_state.recorder.alive())
        self.assertAlmostEqual(a.trace_state.overhead, 6.43, delta=0.2)

    def test_stopping_removes_the_recording_and_the_sockets(self):
        a = self.agent()
        self.advance(a, 5)
        rec = a.trace_state.recorder
        sockets = rec.sock_dir
        self.assertTrue(sockets.is_dir())
        a.stop_tracing()
        self.assertFalse(rec.alive())
        self.assertFalse(sockets.exists())
        self.assertFalse(rec.out_path.exists())

    def test_the_manual_trigger_works_on_perfetto_too(self):
        a = self.agent({"triggers": [{"name": "cpu-high", "type": "cpuPercent", "threshold": 99, "sustainSec": 600}]})
        self.advance(a, 5)
        (self.state / "trace" / "capture.request").write_text("")
        self.advance(a, 5)
        (row,) = self.rows(a)
        self.assertEqual(json.loads(row["manifest"])["trigger"], {"name": "manual", "type": "manual"})

    def test_a_ring_that_filled_does_not_give_a_rate_or_a_session_long_lookback(self):
        """A full ring drops its oldest data, so size over the session's age is not the byte rate (and not the history held)."""
        os.environ["FAKE_TRACEBOX_BYTES"] = str(int(2.2 * 1024 * 1024))
        self.addCleanup(os.environ.pop, "FAKE_TRACEBOX_BYTES", None)
        a = self.agent({"limits": {"maxDiskMB": 8, "maxDurationSec": 40}})
        self.advance(a, 5)
        self.assertEqual(a.trace_state.recorder.buffer_kb(), 2048)
        import time

        time.sleep(1.0)
        self.hot(a)
        (row,) = self.rows(a)
        self.assertEqual(a.queue.meta("perfetto_bytes_per_sec"), "0", "no rate was learned from a full ring")
        lookback = json.loads(row["manifest"])["window"]["lookbackSec"]
        self.assertLessEqual(lookback, 40)  # at most maxDurationSec's worth, never the session's age

    def test_a_ring_with_room_gives_the_rate_and_the_lookback_is_the_age(self):
        os.environ["FAKE_TRACEBOX_BYTES"] = "100000"
        self.addCleanup(os.environ.pop, "FAKE_TRACEBOX_BYTES", None)
        a = self.agent()
        self.advance(a, 5)
        import time

        time.sleep(1.2)
        self.hot(a)
        self.assertGreater(float(a.queue.meta("perfetto_bytes_per_sec")), 0)
        self.assertGreater(a.trace_state.recorder.lookback, 1.0)

    def test_the_process_scan_for_the_overhead_runs_once_a_window_not_every_tick(self):
        a = self.agent()
        calls = []
        real = da.descendant_cpu_ticks
        da.descendant_cpu_ticks = lambda *args: (calls.append(1), real(*args))[1]
        self.addCleanup(setattr, da, "descendant_cpu_ticks", real)
        self.advance(a, 5)
        pid = a.trace_state.recorder.proc.pid
        (self.tmp / "proc" / str(pid)).mkdir(exist_ok=True)
        (self.tmp / "proc" / str(pid) / "stat").write_text(f"{pid} (tracebox) S 1 1 1 0 -1 0 0 0 0 0 5 0 0 0 20 0 1 0 100 0 0\n")
        del calls[:]
        for _ in range(14):  # 70 s of 5 s ticks
            self.advance(a, 5)
        self.assertLessEqual(len(calls), 2)

    def test_a_tracebox_that_cannot_be_run_leaves_no_socket_directory_and_waits_before_trying_again(self):
        broken = self.tmp / "tracebox"
        broken.write_text("not a program\n")
        broken.chmod(0o755)
        a = self.agent(tracebox=str(broken))
        made = []
        real = tempfile.mkdtemp
        tempfile.mkdtemp = lambda *args, **kw: (made.append(real(*args, **kw)), made[-1])[1]
        self.addCleanup(setattr, tempfile, "mkdtemp", real)
        with self.assertLogs("dumptriage", "ERROR") as logs:
            self.advance(a, 5)
        self.assertIn("tracebox could not be run", "\n".join(logs.output))
        self.assertTrue(made and not any(os.path.exists(d) for d in made), "the socket directory was removed")
        self.advance(a, 5)
        self.assertEqual(len(made), 1, "waiting, not starting again on every tick")

    def test_two_triggers_in_one_tick_capture_once_and_the_second_says_why_not(self):
        a = self.agent({"triggers": [{"name": "one", "type": "cpuPercent", "threshold": 1, "sustainSec": 0},
                                     {"name": "two", "type": "cpuPercent", "threshold": 1, "sustainSec": 0}],
                        "limits": {"cooldownMin": 0, "dailyCap": 10}})
        self.advance(a, 50)
        with self.assertLogs("dumptriage", "WARNING") as logs:
            self.advance(a, 50)
        self.assertEqual(len(self.rows(a)), 1)
        self.assertIn("while no recorder session is running", "\n".join(logs.output))
        self.assertEqual(len(a._trace_history()), 1, "the capture that did not happen is not counted")

    def test_the_fall_back_to_perf_is_looked_at_again_and_the_status_says_why(self):
        a = self.agent(tracefs=False)
        self.advance(a, 5)
        self.assertEqual(a.trace_state.backend, "perf")
        status = json.loads((self.state / "trace" / "status.json").read_text())
        self.assertEqual((status["backend"], "tracefs" in status["backendNote"]), ("perf", True))
        self.assertTrue(a.trace_state.recorder.alive())
        (self.tmp / "no-tracefs").mkdir()
        (self.tmp / "no-tracefs" / "available_events").write_text("x\n")  # tracefs is there now
        self.advance(a, 5, seconds=30)
        self.assertEqual(a.trace_state.backend, "perf", "not looked at again yet")
        with self.assertLogs("dumptriage", "INFO") as logs:
            self.advance(a, 5, seconds=da.BACKEND_RECHECK_SECONDS)
        self.assertIn("perfetto backend is available now", "\n".join(logs.output))
        self.assertEqual((a.trace_state.backend, a.trace_state.recorder.kind), ("perfetto", "perfetto"))
        self.assertIsNone(json.loads((self.state / "trace" / "status.json").read_text())["backendNote"])

    def test_stopping_ends_the_whole_process_group_not_only_tracebox(self):
        os.environ["FAKE_TRACEBOX_CHILD"] = "1"
        self.addCleanup(os.environ.pop, "FAKE_TRACEBOX_CHILD", None)
        a = self.agent()
        self.advance(a, 5)
        child_file = Path(str(a.trace_state.recorder.out_path) + ".child")
        child = int(child_file.read_text())
        os.kill(child, 0)  # alive
        a.stop_tracing()
        import time

        for _ in range(50):
            try:
                os.kill(child, 0)
                os.waitpid(child, os.WNOHANG)
            except (ProcessLookupError, ChildProcessError):
                break
            time.sleep(0.1)
        with self.assertRaises(ProcessLookupError):
            os.kill(child, 0)

    def test_the_name_in_the_config_is_the_name_the_agent_fires(self):
        for name in ("cpu-high", "sql busy", "a/b", "ünï", "x" * 64, "manual"):
            text = da.render_perfetto_config(TEMPLATE, 8192, [name])
            self.assertIn(f'name: "{da.trigger_name_for(name)}"', text)


if __name__ == "__main__":
    unittest.main()
