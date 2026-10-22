"""Performance tracing in the Linux agent (website#1999, dumptriage-tools#27): trigger rules against the shared fixture,
/proc readers, folding pinned to real `perf script` output on three distros, and the capture path against a fake perf."""
import json
import os
import shutil
import stat
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import dumptriage_agent as da  # noqa: E402

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
FIXTURES = HERE / "fixtures"
TRIGGERS = json.loads((ROOT / "tests" / "fixtures" / "tracing" / "triggers.json").read_text())
CONFIG_SCHEMA = json.loads((ROOT / "schemas" / "tracing-config.schema.json").read_text())
MANIFEST_SCHEMA = json.loads((ROOT / "schemas" / "trace-manifest.schema.json").read_text())
DISTROS = {
    "ubuntu2204": ("perf-script-ubuntu2204-perf5.15.txt", "5.15.216", "cycles"),
    "rocky9": ("perf-script-rocky9-perf5.14.txt", "5.14.0-687.58.1.el9_8.x86_64", "cycles:P"),
    "debian12": ("perf-script-debian12-perf6.1.txt", "6.1.190", "cycles"),
}

try:
    import jsonschema
except ImportError:  # the agent is standard-library only; the schema checks need the package
    jsonschema = None


class SharedFixtureTests(unittest.TestCase):
    """tests/fixtures/tracing/triggers.json: the same cases the Windows and macOS suites load."""

    def test_every_case_gives_the_expected_outcome(self):
        for case in TRIGGERS["cases"]:
            with self.subTest(case["name"]):
                got = da.evaluate_series(
                    case["trigger"], case["limits"], [h["atSec"] for h in case["history"]],
                    [(s["tSec"], s["value"]) for s in case["series"]])
                self.assertEqual(got, case["expect"])

    def test_a_missing_reading_restarts_the_clock(self):
        r = da.TriggerRunner({"threshold": 90, "sustainSec": 10})
        self.assertFalse(r.feed(0, 95))
        self.assertFalse(r.feed(5, None))
        self.assertFalse(r.feed(10, 95))
        self.assertTrue(r.feed(20, 95))
        self.assertFalse(r.feed(21, 95))  # met once: it must be sustained again


class ConfigTests(unittest.TestCase):
    def tracing(self, **over):
        t = json.loads(json.dumps(da.DEFAULT_CONFIG["tracing"]))
        t["triggers"] = [{"name": "cpu-high", "type": "cpuPercent", "threshold": 90, "sustainSec": 30}]
        t.update(over)
        return t

    def test_the_default_is_off_and_held_for_approval(self):
        t = da.DEFAULT_CONFIG["tracing"]
        self.assertIs(t["enabled"], False)
        self.assertEqual(t["submission"], "AskFirst")
        self.assertEqual(t["limits"], {"maxDurationSec": 60, "maxDiskMB": 512, "maxOverheadPercent": 2, "cooldownMin": 60, "dailyCap": 4})
        self.assertEqual(da.validate_tracing(self.tracing()), [])

    def test_bad_configs_are_named(self):
        cases = [
            (self.tracing(backend="wpr"), "backend 'wpr' is not available"),
            (self.tracing(submission="Whenever"), "submission 'Whenever'"),
            (self.tracing(triggers=[]), "no triggers"),
            (self.tracing(triggers=[{"name": "x", "type": "cpuPercent"}]), "needs a numeric threshold"),
            (self.tracing(triggers=[{"name": "x", "type": "process", "threshold": 5}]), "needs a process name"),
            (self.tracing(triggers=[{"name": "x", "type": "schedule"}]), "intervalMin"),
            (self.tracing(triggers=[{"name": "x", "type": "psiSomeAvg10", "threshold": 120}]), "at most 100"),
            (self.tracing(triggers=[{"name": "a b", "type": "hangEvent"}]), "name must be"),
            (self.tracing(triggers=[{"name": "x", "type": "gpu", "threshold": 1}]), "type 'gpu'"),
            (self.tracing(triggers=[{"name": "x", "type": "hangEvent"}, {"name": "x", "type": "hangEvent"}]), "used twice"),
            (self.tracing(triggers=[{"name": "x", "type": "cpuPercent", "threshold": 1, "sustainSec": -1}]), "sustainSec"),
            (self.tracing(limits={"maxDurationSec": 0, "maxDiskMB": 1, "maxOverheadPercent": 0, "cooldownMin": -1, "dailyCap": "x"}), "maxDurationSec"),
        ]
        for tracing, fragment in cases:
            with self.subTest(fragment):
                self.assertIn(fragment, "; ".join(da.validate_tracing(tracing)))

    @unittest.skipIf(jsonschema is None, "jsonschema not installed")
    def test_the_agent_and_the_published_schema_agree(self):
        good = [
            {"enabled": True, "backend": "perf", "triggers": [{"name": "p", "type": "process", "process": "sqlservr", "threshold": 80, "sustainSec": 20}]},
            {"enabled": True, "backend": "perf", "triggers": [{"name": "h", "type": "hangEvent"}]},
            {"enabled": True, "backend": "perf", "triggers": [{"name": "s", "type": "schedule", "intervalMin": 1440}]},
        ]
        bad = [
            {"enabled": True, "triggers": [{"name": "x", "type": "cpuPercent"}]},
            {"enabled": True, "triggers": [{"name": "x", "type": "process", "threshold": 80}]},
            {"enabled": True, "triggers": [{"name": "x", "type": "schedule"}]},
            {"enabled": True, "triggers": [{"name": "x", "type": "psiSomeAvg10", "threshold": 150}]},
            {"enabled": True, "triggers": [{"name": "bad name", "type": "cpuPercent", "threshold": 1}]},
        ]
        for tracing in good:
            jsonschema.validate(tracing, CONFIG_SCHEMA)
            merged = da.merge(da.DEFAULT_CONFIG["tracing"], tracing)
            self.assertEqual(da.validate_tracing(merged), [], tracing)
        for tracing in bad:
            with self.assertRaises(jsonschema.ValidationError):
                jsonschema.validate(tracing, CONFIG_SCHEMA)
            self.assertNotEqual(da.validate_tracing(da.merge(da.DEFAULT_CONFIG["tracing"], tracing)), [], tracing)


class FakeProc:
    """A /proc the sampler reads: write the next interval's counters with tick()."""

    def __init__(self, root, ncpu=4):
        self.root, self.ncpu = Path(root), ncpu
        self.total = self.busy = self.ios = self.io_ms = 0
        self.procs = {}
        (self.root / "pressure").mkdir(parents=True, exist_ok=True)
        self.tick(0, 0)

    def tick(self, busy_pct, seconds, load=0.5, psi=0.0, disk_ms=0.0, ios=0):
        add = seconds * 100 * self.ncpu
        self.total += add
        self.busy += int(add * busy_pct / 100)
        self.ios += ios
        self.io_ms += int(ios * disk_ms)
        idle = self.total - self.busy
        lines = [f"cpu {self.busy} 0 0 {idle} 0 0 0 0 0 0"] + [f"cpu{i} 0 0 0 0 0 0 0 0 0 0" for i in range(self.ncpu)]
        (self.root / "stat").write_text("\n".join(lines) + "\n")
        (self.root / "loadavg").write_text(f"{load * self.ncpu:.2f} 0.5 0.5 1/100 1234\n")
        for res in ("cpu", "io", "memory"):
            (self.root / "pressure" / res).write_text(f"some avg10={psi if res == 'cpu' else 0.0:.2f} avg60=0.00 avg300=0.00 total=0\n")
        (self.root / "diskstats").write_text(
            f"   8       0 sda {self.ios} 0 0 {self.io_ms} 0 0 0 0 0 0 0\n   8       1 sda1 99 0 0 99 0 0 0 0 0 0 0\n   7       0 loop0 5 0 0 5 0 0 0 0 0 0 0\n")
        for pid, (comm, ticks) in self.procs.items():
            d = self.root / str(pid)
            d.mkdir(exist_ok=True)
            (d / "stat").write_text(f"{pid} ({comm}) S 1 1 1 0 -1 0 0 0 0 0 {ticks} 0 0 0 20 0 1 0 100 0 0\n")

    def run(self, pid, comm, ticks):
        self.procs[pid] = (comm, ticks)
        d = self.root / str(pid)
        d.mkdir(exist_ok=True)
        (d / "stat").write_text(f"{pid} ({comm}) S 1 1 1 0 -1 0 0 0 0 0 {ticks} 0 0 0 20 0 1 0 100 0 0\n")


class ProcSamplerTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)

    def test_cpu_load_psi_and_disk_latency(self):
        fp = FakeProc(self.tmp)
        s = da.ProcSampler(self.tmp)
        first = s.read(0)
        self.assertIsNone(first["cpuPercent"])  # a delta needs two reads
        self.assertEqual((s.ncpu, first["loadPerCpu"], first["psiSomeAvg10"]), (4, 0.5, 0.0))
        fp.tick(75, 5, load=2.0, psi=31.5, disk_ms=40.0, ios=50)
        m = s.read(5)
        self.assertAlmostEqual(m["cpuPercent"], 75.0, places=1)
        self.assertEqual((m["loadPerCpu"], m["psiSomeAvg10"]), (2.0, 31.5))
        self.assertAlmostEqual(m["diskLatencyMs"], 40.0)  # sda counts; its partition and loop0 do not
        fp.tick(10, 5)
        self.assertEqual(s.read(10)["diskLatencyMs"], 0.0)  # no I/O in the interval

    def test_per_process_percent_of_one_cpu_and_the_15_character_comm(self):
        fp = FakeProc(self.tmp)
        fp.run(10, "sqlservr", 0)
        fp.run(11, "sqlservr", 0)
        fp.run(12, "a-very-long-pro", 0)
        s = da.ProcSampler(self.tmp, want_processes=True)
        s.read(0)
        fp.tick(50, 5)
        fp.run(10, "sqlservr", 250)  # 2.5 s of CPU in 5 s: 50%
        fp.run(11, "sqlservr", 250)
        fp.run(13, "newcomer", 400)  # not in the previous read: no baseline, no value
        m = s.read(5)
        self.assertAlmostEqual(m["procs"]["sqlservr"], 100.0)
        self.assertNotIn("newcomer", m["procs"])
        trigger = {"name": "x", "type": "process", "process": "a-very-long-program", "threshold": 1}
        self.assertEqual(da.trigger_value(trigger, {"procs": {"a-very-long-pro": 7.0}}), 7.0)
        self.assertEqual(da.trigger_value({"type": "cpuPercent", "process": "sqlservr"}, m), m["procs"]["sqlservr"])
        self.assertEqual(da.trigger_value({"type": "cpuPercent"}, {"cpuPercent": 12.5}), 12.5)

    def test_missing_files_give_none_not_a_crash(self):
        s = da.ProcSampler(self.tmp / "nowhere", want_processes=True)
        m = s.read(0)
        self.assertEqual((m["loadPerCpu"], m["psiSomeAvg10"], m["cpuPercent"], m["diskLatencyMs"], m["procs"]), (None, None, None, None, {}))


class FoldTests(unittest.TestCase):
    def test_real_output_of_three_distros_folds(self):
        for name, (fixture, version, event) in DISTROS.items():
            with self.subTest(name):
                text = (FIXTURES / fixture).read_text()
                self.assertIn(f"# perf version : {version}", text)
                folded = da.fold_perf_script(text.splitlines(True))
                self.assertGreater(folded.samples, 100)
                self.assertEqual((folded.other_events, folded.lines_unread), (0, 0), "every line of real output is read")
                self.assertAlmostEqual(folded.span, 4.66, delta=0.1)
                out = folded.text()
                self.assertEqual(sum(int(line.rsplit(" ", 1)[1]) for line in out.splitlines()), folded.samples)
                self.assertIn("_[k]", out)  # kernel frames carry the marker
                self.assertIn("[unknown]", out)
                self.assertNotIn("/home/", out)
                self.assertTrue(all(";" in line or line.count(" ") >= 1 for line in out.splitlines()))
                # in a --switch-output dump the header's sample duration and first/last sample times are all 0, on every
                # perf version tried, so the span comes from the samples
                self.assertRegex(text, r"# sample duration :\s+0\.000 ms")
                self.assertRegex(text, r"# time of first sample : 0\.000000")

    def test_the_event_name_differs_by_perf_version_and_does_not_matter(self):
        events = {}
        for name, (fixture, _, event) in DISTROS.items():
            first = [l for l in (FIXTURES / fixture).read_text().splitlines() if l and l[0] not in "#\t "][0]
            events[name] = first.split()[-1].rstrip(":")
            self.assertEqual(events[name], event)
        self.assertEqual(sorted(set(events.values())), ["cycles", "cycles:P"])

    def sample(self, comm, frames, t, event="cycles:P"):
        lines = [f"{comm} 42 [001] {t:.6f}:          1 {event}: "]
        lines += [f"\t    {0x7f00 + i:x} {f}" for i, f in enumerate(frames)]
        return lines + [""]

    def test_kernel_modules_are_kernel_frames_too(self):
        lines = self.sample("a", ["f+0x1 ([ext4])", "g+0x1 (/lib/modules/6.8.0/kernel/fs/jbd2.ko.zst)", "h+0x1 ([vdso])", "i+0x1 ([unknown])",
                                  "j+0x1 (/usr/lib/libext4.so)"], 1.0)
        self.assertEqual(list(da.fold_perf_script(lines).stacks), ["a;j;i;h;g_[k];f_[k]"])

    def test_frames_comm_and_kernel_marker(self):
        lines = (
            self.sample("Bun Pool 2", ["clear_page_rep+0x7 ([kernel.kallsyms])", "Foo::bar(int, char**)+0x72 (/home/alice/app)", "_start+0x1 (/home/alice/app)"], 1.0)
            + self.sample("a;b", ["x+0x1 (/bin/x)"], 2.0))
        folded = da.fold_perf_script(lines)
        self.assertEqual(dict(folded.stacks), {
            "Bun Pool 2;_start;Foo::bar(int, char**);clear_page_rep_[k]": 1,
            "a,b;x": 1,
        })

    def test_home_directories_are_redacted_in_names(self):
        lines = self.sample("job", ["load+0x1 (/bin/x)", "/home/alice/lib.so+0x2 (/bin/x)"], 1.0)
        self.assertEqual(list(da.fold_perf_script(lines).stacks), ["job;/home/<user>/lib.so;load"])
        self.assertEqual(da.redact_home("/Users/bob/x /home/carol/y"), "/Users/<user>/x /home/<user>/y")

    def test_only_the_first_event_counts_and_the_window_keeps_the_latest_samples(self):
        lines = self.sample("a", ["f+0x1 (/bin/a)"], 1.0) + self.sample("a", ["g+0x1 (/bin/a)"], 9.0) + self.sample("a", ["h+0x1 (/bin/a)"], 10.0, "context-switches")
        full = da.fold_perf_script(lines)
        self.assertEqual((full.samples, full.other_events, full.first, full.last), (2, 1, 1.0, 9.0))
        windowed = da.fold_perf_script(lines, window_sec=5)
        self.assertEqual((windowed.samples, list(windowed.stacks), windowed.first), (1, ["a;g"], 9.0))

    def test_garbage_is_counted_and_an_empty_input_is_empty(self):
        folded = da.fold_perf_script(["not perf output\n", "\tzzz not a frame\n"])
        self.assertEqual((folded.samples, folded.lines_unread, folded.span), (0, 1, 0.0))
        bad = self.sample("a", ["f+0x1 (/bin/a)"], 1.0)
        bad.insert(2, "\tzzzz no module here")
        self.assertEqual(da.fold_perf_script(bad).lines_unread, 1)

    def test_text_is_cut_at_the_byte_limit_most_frequent_first(self):
        stacks = da.Counter({"a;hot": 90, "a;warm": 9, "a;cold": 1})
        folded = da.Folded(stacks, 0, 1, 100, 0, 0)
        self.assertEqual(folded.text().splitlines(), ["a;hot 90", "a;warm 9", "a;cold 1"])
        self.assertEqual(folded.text(max_bytes=20), "a;hot 90\na;warm 9\n")


class UploadTraceTests(unittest.TestCase):
    """upload_trace against a real local HTTP server: the request the backend's /api/v1/traces expects."""

    def serve(self, status):
        from http.server import BaseHTTPRequestHandler, HTTPServer
        import threading

        seen = {}

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                seen.update(path=self.path, auth=self.headers["Authorization"], type=self.headers["Content-Type"],
                            body=self.rfile.read(int(self.headers["Content-Length"])))
                self.send_response(status)
                self.end_headers()
                self.wfile.write(b"server said no" if status != 201 else b"{}")

            def log_message(self, *a):
                pass

        server = HTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.shutdown)
        self.addCleanup(server.server_close)
        return f"http://127.0.0.1:{server.server_port}", seen

    def test_the_request_has_the_manifest_the_hostname_and_the_file(self):
        base, seen = self.serve(201)
        f = Path(tempfile.mkdtemp()) / "t.folded"
        self.addCleanup(shutil.rmtree, f.parent)
        f.write_text("app;main;hot 5\n")
        self.assertEqual(da.upload_trace(base + "/", "dts_key", str(f), '{"schemaVersion": 1}', "web-01", "t.folded"), (da.SUCCESS, 201, None))
        self.assertEqual((seen["path"], seen["auth"]), ("/api/v1/traces", "Bearer dts_key"))
        self.assertTrue(seen["type"].startswith("multipart/form-data; boundary="))
        body = seen["body"].decode()
        self.assertIn('name="manifest"\r\n\r\n{"schemaVersion": 1}', body)
        self.assertIn('name="hostname"\r\n\r\nweb-01', body)
        self.assertIn('name="file"; filename="t.folded"\r\nContent-Type: text/plain\r\n\r\napp;main;hot 5\n', body)
        base, seen = self.serve(201)
        da.upload_trace(base, "k", str(f), "{}", None)
        self.assertNotIn('name="hostname"', seen["body"].decode())

    def test_statuses_map_to_outcomes(self):
        f = Path(tempfile.mkdtemp()) / "t.folded"
        self.addCleanup(shutil.rmtree, f.parent)
        f.write_text("a;b 1\n")
        for status, outcome in ((400, da.REJECTED), (422, da.REJECTED), (413, da.QUOTA), (429, da.QUOTA), (401, da.AUTH), (403, da.AUTH), (500, da.TRANSIENT)):
            base, _ = self.serve(status)
            with self.subTest(status):
                got = da.upload_trace(base, "k", str(f), "{}", None)
                self.assertEqual((got[0], got[1]), (outcome, status))
                self.assertEqual(got[2], "server said no")
        self.assertEqual(da.upload_trace("http://127.0.0.1:1", "k", str(f), "{}", None)[0], da.TRANSIENT)


FAKE_PERF = HERE / "fake_perf.py"


class AgentTracingTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.state, self.config = self.tmp / "state", self.tmp / "config"
        self.config.mkdir()
        self.fp = FakeProc(self.tmp / "proc")
        self.now = datetime(2026, 10, 21, 12, 0, 0, tzinfo=timezone.utc)
        self.uploads = []
        self.env = {"FAKE_PERF_SCRIPT": str(FIXTURES / DISTROS["ubuntu2204"][0])}
        for k, v in self.env.items():
            os.environ[k] = v
            self.addCleanup(os.environ.pop, k, None)
        saved = da.RECORDER_START_GRACE_SECONDS
        da.RECORDER_START_GRACE_SECONDS = 0.3
        self.addCleanup(setattr, da, "RECORDER_START_GRACE_SECONDS", saved)
        self.agents = []

    def agent(self, tracing=None, **kw):
        tr = {"enabled": True, "triggers": [{"name": "cpu-high", "type": "cpuPercent", "threshold": 90, "sustainSec": 10}]}
        tr.update(tracing or {})
        cfg = da.merge(da.DEFAULT_CONFIG, {"sendHostname": False, "watchedPaths": [str(self.tmp / "watch")], "tracing": tr})
        (self.tmp / "watch").mkdir(exist_ok=True)

        def uploader(base, key, path, manifest, hostname, name):
            self.uploads.append({"path": path, "manifest": manifest, "name": name, "body": Path(path).read_text(), "key": key})
            return kw.pop("outcome", (da.SUCCESS, 201, None))

        a = da.Agent(cfg, self.config, self.state, clock=lambda: self.now, trace_uploader=uploader,
                     perf_bin=str(FAKE_PERF), proc_root=str(self.tmp / "proc"))
        self.agents.append(a)
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

    def test_a_sustained_cpu_trigger_captures_folds_and_holds_the_trace_for_approval(self):
        a = self.agent()
        self.advance(a, 5)  # starts the recorder; the first read has no delta
        self.assertTrue(a.trace_state.recorder.alive())
        self.hot(a)
        (row,) = self.rows(a)
        self.assertEqual((row["state"], row["kind"], row["dump_type"]), (da.PENDING, "trace", "folded"))
        self.assertTrue(row["ask_deadline_utc"])
        path = Path(row["file_path"])
        self.assertTrue(path.name.endswith("-cpu-high.folded"))
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)
        self.assertEqual([p.name for p in (self.state / "trace").glob("perf.data.[0-9]*")], [], "the raw snapshot is deleted once folded")
        manifest = json.loads(row["manifest"])
        self.assertEqual(manifest["traceType"], "folded")
        self.assertEqual(manifest["trigger"], {"name": "cpu-high", "type": "cpuPercent", "threshold": 90, "observed": 95.0})
        self.assertEqual(manifest["window"]["stopUtc"], row["detected_at_utc"])
        self.assertEqual(manifest["window"]["lookbackSec"], 4)
        self.assertEqual(manifest["tool"]["name"], "perf")
        self.assertEqual(manifest["tool"]["version"], "6.8.12")
        self.assertRegex(manifest["tool"]["command"], r"record -F 99 -a -g --overwrite --switch-output=signal -m \d+ -o ")  # -m follows the CPU count
        self.assertNotIn("/home/rob", manifest["tool"]["command"])
        self.assertEqual(manifest["os"]["family"], "linux")
        self.assertRegex(manifest["hostToken"], r"^[0-9a-f]{16}$")
        self.assertEqual(manifest["agentVersion"], da.AGENT_VERSION)
        self.assertNotIn("hostname", json.dumps(manifest))
        self.assertEqual(self.uploads, [], "held, not sent")
        if jsonschema is not None:
            jsonschema.validate(manifest, MANIFEST_SCHEMA)

    def test_approving_uploads_the_folded_text_and_deletes_it(self):
        a = self.agent()
        self.advance(a, 5)
        self.hot(a)
        (row,) = self.rows(a)
        a.queue.update(row["id"], state=da.QUEUED, ask_deadline_utc=None)
        os.environ["_"] = ""
        (self.config / "api-key").write_text("dts_key\n")
        a.submit_due()
        (up,) = self.uploads
        self.assertEqual((up["key"], up["name"].endswith(".folded")), ("dts_key", True))
        self.assertIn(";", up["body"])
        self.assertEqual(json.loads(up["manifest"])["trigger"]["name"], "cpu-high")
        self.assertEqual(self.rows(a)[0]["state"], da.SUBMITTED)
        self.assertFalse(Path(row["file_path"]).exists())

    def test_always_submit_queues_straight_away_and_not_enabled_is_retried_not_a_key_problem(self):
        a = self.agent({"submission": "AlwaysSubmit"}, outcome=(da.AUTH, 403, "traces_disabled: performance traces are not enabled on this service yet"))
        (self.config / "api-key").write_text("dts_key\n")
        self.advance(a, 5)
        self.hot(a)
        self.assertEqual(self.rows(a)[0]["state"], da.QUEUED)
        a.submit_due()
        row = self.rows(a)[0]
        self.assertEqual((row["state"], row["retry_count"]), (da.QUEUED, 1))
        self.assertIsNone(a.queue.meta("auth_backoff_until_utc"))

    def test_notify_only_waits_for_a_person_and_skip_deletes_the_file(self):
        a = self.agent({"submission": "NotifyOnly"})
        self.advance(a, 5)
        self.hot(a)
        (row,) = self.rows(a)
        self.assertEqual((row["state"], row["ask_deadline_utc"]), (da.PENDING, None))
        a.queue.update(row["id"], state=da.SKIPPED)
        a.trace_housekeeping()
        self.assertFalse(Path(row["file_path"]).exists())

    def test_the_cooldown_and_the_daily_cap_stop_further_captures(self):
        a = self.agent({"limits": {"cooldownMin": 1, "dailyCap": 2}})
        self.advance(a, 5)
        self.hot(a)
        self.assertEqual(len(self.rows(a)), 1)
        self.hot(a, 6)  # met again at once, inside the cooldown
        self.assertEqual(len(self.rows(a)), 1)
        self.now += timedelta(minutes=2)
        self.hot(a)
        self.assertEqual(len(self.rows(a)), 2)
        self.now += timedelta(minutes=2)
        self.hot(a, 6)
        self.assertEqual(len(self.rows(a)), 2, "dailyCap 2 reached")
        self.now += timedelta(days=1, minutes=1)
        self.hot(a, 6)
        self.assertEqual(len(self.rows(a)), 3, "24 hours later the cap no longer counts the first")

    def test_no_capture_below_the_threshold_or_before_it_is_sustained(self):
        a = self.agent()
        self.advance(a, 5)
        for busy in (95, 40, 95, 95, 40):
            self.advance(a, busy)
        self.assertEqual(self.rows(a), [])

    def test_a_process_trigger_and_a_disk_trigger(self):
        a = self.agent({"triggers": [
            {"name": "sql", "type": "process", "process": "sqlservr", "threshold": 150, "sustainSec": 5},
            {"name": "slow-disk", "type": "diskLatencyMs", "threshold": 100, "sustainSec": 5, "intervalMin": 1}]})
        self.fp.run(10, "sqlservr", 0)
        self.advance(a, 5)
        ticks = 0
        for _ in range(3):
            ticks += 1000  # 10 CPU-seconds in 5 s: 200% of one CPU
            self.fp.run(10, "sqlservr", ticks)
            self.advance(a, 60)
        triggers = [json.loads(r["manifest"])["trigger"] for r in self.rows(a)]
        self.assertEqual([t["name"] for t in triggers], ["sql"])
        self.assertEqual(triggers[0]["process"], "sqlservr")

    def test_load_and_pressure_triggers(self):
        a = self.agent({"triggers": [{"name": "load", "type": "loadPerCpu", "threshold": 2, "sustainSec": 5}]})
        self.advance(a, 5, load=3.0)
        self.advance(a, 5, load=3.0)
        self.advance(a, 5, load=3.0)
        self.assertEqual(json.loads(self.rows(a)[0]["manifest"])["trigger"]["observed"], 3.0)
        b = self.agent({"triggers": [{"name": "psi", "type": "psiSomeAvg10", "threshold": 20, "sustainSec": 0}], "limits": {"cooldownMin": 0}})
        self.advance(b, 5, psi=35.0)
        self.assertTrue(any(json.loads(r["manifest"])["trigger"]["name"] == "psi" for r in self.rows(b)))

    def test_a_schedule_trigger_takes_a_baseline_every_interval(self):
        a = self.agent({"triggers": [{"name": "nightly", "type": "schedule", "intervalMin": 10}], "limits": {"cooldownMin": 0}})
        self.advance(a, 5)
        self.assertEqual(self.rows(a), [])
        self.now += timedelta(minutes=11)
        self.advance(a, 5)
        self.assertEqual(len(self.rows(a)), 1)
        trigger = json.loads(self.rows(a)[0]["manifest"])["trigger"]
        self.assertEqual((trigger["name"], trigger["type"]), ("nightly", "schedule"))
        self.advance(a, 5)
        self.assertEqual(len(self.rows(a)), 1)  # not again until another interval has passed

    def test_a_manual_request_is_a_capture_with_the_limits_still_applied(self):
        a = self.agent({"triggers": [{"name": "cpu-high", "type": "cpuPercent", "threshold": 99, "sustainSec": 600}]})
        self.advance(a, 5)
        (self.state / "trace" / "capture.request").write_text("")
        self.advance(a, 5)
        (row,) = self.rows(a)
        self.assertEqual(json.loads(row["manifest"])["trigger"], {"name": "manual", "type": "manual"})
        self.assertFalse((self.state / "trace" / "capture.request").exists())
        (self.state / "trace" / "capture.request").write_text("")
        self.advance(a, 5)
        self.assertEqual(len(self.rows(a)), 1)
        self.assertIn("cooldown", json.loads((self.state / "trace" / "status.json").read_text())["lastManual"])

    def test_off_by_default_does_nothing(self):
        a = self.agent({"enabled": False})
        self.advance(a, 95)
        self.assertIsNone(a.trace_state.recorder)
        self.assertFalse((self.state / "trace").exists())

    def test_a_bad_config_disables_tracing_and_says_why(self):
        a = self.agent({"triggers": [{"name": "x", "type": "cpuPercent"}]})
        with self.assertLogs("dumptriage", "ERROR") as logs:
            self.advance(a, 95)
        self.assertIn("needs a numeric threshold", "\n".join(logs.output))
        self.assertIsNone(a.trace_state.recorder)

    def test_without_perf_it_says_so(self):
        a = self.agent()
        a.perf_bin = None
        orig = da.shutil.which
        da.shutil.which = lambda name: None
        self.addCleanup(setattr, da.shutil, "which", orig)
        with self.assertLogs("dumptriage", "ERROR") as logs:
            self.advance(a, 95)
        self.assertIn("perf is not installed", "\n".join(logs.output))

    def test_a_recorder_that_cannot_start_is_reported_and_retried_later(self):
        os.environ["FAKE_PERF_FAIL"] = "1"
        self.addCleanup(os.environ.pop, "FAKE_PERF_FAIL", None)
        a = self.agent()
        with self.assertLogs("dumptriage", "ERROR") as logs:
            self.advance(a, 95)
        self.assertIn("perf_event_paranoid is 4", "\n".join(logs.output))
        status = json.loads((self.state / "trace" / "status.json").read_text())
        self.assertIn("could not start", status["error"])
        self.assertEqual(status["recorder"], "stopped")
        del os.environ["FAKE_PERF_FAIL"]
        self.advance(a, 95, seconds=10)  # still waiting
        self.assertFalse(a.trace_state.recorder.alive())
        self.advance(a, 95, seconds=da.RECORDER_RETRY_SECONDS)
        self.assertTrue(a.trace_state.recorder.alive())
        self.assertIsNone(json.loads((self.state / "trace" / "status.json").read_text())["error"])

    def test_a_recorder_that_died_is_started_again_after_a_wait_not_on_every_tick(self):
        a = self.agent()
        self.advance(a, 5)
        a.trace_state.recorder.proc.kill()
        a.trace_state.recorder.proc.wait()
        with self.assertLogs("dumptriage", "ERROR") as logs:
            self.advance(a, 5)
        self.assertIn("the perf recorder exited", "\n".join(logs.output))
        self.assertFalse(a.trace_state.recorder.alive())
        self.advance(a, 5)
        self.assertFalse(a.trace_state.recorder.alive(), "still waiting")
        self.advance(a, 5, seconds=da.RECORDER_RETRY_SECONDS)
        self.assertTrue(a.trace_state.recorder.alive())

    def test_the_recorder_is_stopped_when_it_costs_more_than_the_limit(self):
        a = self.agent({"limits": {"maxOverheadPercent": 1}})
        self.advance(a, 5)
        rec = a.trace_state.recorder
        pid = rec.proc.pid
        self.fp.run(pid, "perf", 0)
        self.advance(a, 5)  # the first reading of its CPU
        self.fp.run(pid, "perf", 100 * 4 * 70 // 10)  # 28 CPU-seconds over the next 70 s on 4 CPUs: 10%
        with self.assertLogs("dumptriage", "ERROR") as logs:
            self.advance(a, 5, seconds=70)
        self.assertIn("over the 1% limit", "\n".join(logs.output))
        self.assertFalse(rec.alive())
        self.assertAlmostEqual(a.trace_state.overhead, 10.0, delta=0.5)
        self.advance(a, 5, seconds=60)
        self.assertFalse(rec.alive(), "backed off, not restarted at once")
        self.advance(a, 5, seconds=da.OVERHEAD_BACKOFF_SECONDS)
        self.assertTrue(a.trace_state.recorder.alive())

    def test_a_recorder_within_the_limit_keeps_running(self):
        a = self.agent()
        self.advance(a, 5)
        pid = a.trace_state.recorder.proc.pid
        self.fp.run(pid, "perf", 0)
        self.advance(a, 5)
        self.fp.run(pid, "perf", 100)  # 1 CPU-second in 70 s on 4 CPUs: 0.36%
        self.advance(a, 5, seconds=70)
        self.assertTrue(a.trace_state.recorder.alive())
        self.assertAlmostEqual(a.trace_state.overhead, 0.357, delta=0.05)

    def test_the_ring_buffer_is_sized_from_the_disk_budget(self):
        rec = da.PerfRecorder("perf", self.tmp, 8, 512)
        self.assertEqual(rec.mmap_pages(), 4096)  # 128 MiB over 8 CPUs: 16 MiB, 4096 pages each
        self.assertEqual(da.PerfRecorder("perf", self.tmp, 8, 1).mmap_pages(), 64)  # never under 64 pages
        self.assertEqual(da.PerfRecorder("perf", self.tmp, 1, 100000).mmap_pages(), 16384)  # nor over 64 MiB a CPU

    def test_an_empty_or_failed_script_queues_nothing_but_still_counts_against_the_cap(self):
        os.environ["FAKE_PERF_SCRIPT_EXIT"] = "1"
        self.addCleanup(os.environ.pop, "FAKE_PERF_SCRIPT_EXIT", None)
        empty = self.tmp / "empty.txt"
        empty.write_text("# header only\n")
        os.environ["FAKE_PERF_SCRIPT"] = str(empty)
        a = self.agent()
        self.advance(a, 5)
        with self.assertLogs("dumptriage", "ERROR"):
            self.hot(a)
        self.assertEqual(self.rows(a), [])
        self.assertEqual(len(a._trace_history()), 1)
        self.assertEqual(list((self.state / "trace").glob("perf.data.[0-9]*")), [])
        del os.environ["FAKE_PERF_SCRIPT_EXIT"]
        b = self.agent({"limits": {"cooldownMin": 0}})
        with self.assertLogs("dumptriage", "WARNING") as logs:
            self.now += timedelta(minutes=1)
            self.hot(b, 5)
        self.assertIn("held no samples", "\n".join(logs.output))

    def test_a_perf_script_that_writes_a_lot_to_stderr_does_not_block_the_fold(self):
        os.environ["FAKE_PERF_STDERR_KB"] = "300"
        self.addCleanup(os.environ.pop, "FAKE_PERF_STDERR_KB", None)
        a = self.agent()
        self.advance(a, 5)
        self.hot(a)
        self.assertEqual(len(self.rows(a)), 1)

    def test_a_snapshot_over_the_disk_budget_is_discarded(self):
        a = self.agent({"limits": {"maxDiskMB": 1}})
        self.advance(a, 5)
        orig = a.trace_state.recorder.snapshot

        def big():
            p = orig()
            with open(p, "ab") as f:
                f.truncate(2 * 1024 * 1024)
            return p

        a.trace_state.recorder.snapshot = big
        with self.assertLogs("dumptriage", "ERROR") as logs:
            self.hot(a)
        self.assertIn("over the 1 MB disk limit", "\n".join(logs.output))
        self.assertEqual(self.rows(a), [])
        self.assertEqual(list((self.state / "trace").glob("perf.data.[0-9]*")), [])

    def test_the_trace_directory_is_kept_inside_its_budget(self):
        a = self.agent({"limits": {"maxDiskMB": 1}})
        d = self.state / "trace"
        d.mkdir(parents=True)
        a.queue.add(id="old", file_path=str(d / "old-x.folded"), size_bytes=1, dump_type="folded", state=da.QUEUED,
                    detected_at_utc=da.iso(self.now), kind="trace", manifest="{}")
        (d / "old-x.folded").write_bytes(b"x" * 900_000)
        os.utime(d / "old-x.folded", (1, 1))
        a.queue.add(id="new", file_path=str(d / "new-x.folded"), size_bytes=1, dump_type="folded", state=da.QUEUED,
                    detected_at_utc=da.iso(self.now), kind="trace", manifest="{}")
        (d / "new-x.folded").write_bytes(b"y" * 900_000)
        (d / "orphan.folded").write_bytes(b"z")  # no row names it
        (d / "perf.data.20261021120000").write_bytes(b"s")
        os.utime(d / "perf.data.20261021120000", (1, 1))  # a snapshot nobody folded
        with self.assertLogs("dumptriage", "WARNING"):
            a.trace_housekeeping()
        self.assertEqual(sorted(p.name for p in d.iterdir() if p.is_file()), ["new-x.folded"])
        self.assertEqual(a.queue.get("old")["state"], da.FAILED)
        self.assertIn("maxDiskMB", a.queue.get("old")["last_error"])

    def test_a_queued_trace_whose_file_is_gone_is_rejected(self):
        a = self.agent()
        (self.config / "api-key").write_text("dts_key\n")
        a.queue.add(id="gone", file_path=str(self.state / "trace" / "gone.folded"), size_bytes=1, dump_type="folded",
                    state=da.QUEUED, detected_at_utc=da.iso(self.now), kind="trace", manifest="{}")
        a.submit_due()
        self.assertEqual(a.queue.get("gone")["state"], da.FAILED)
        self.assertIn("gone", a.queue.get("gone")["last_error"])

    def test_dumps_still_flow_beside_traces_and_the_queue_gets_its_new_columns(self):
        import sqlite3

        db = self.tmp / "old.db"
        con = sqlite3.connect(db)
        con.executescript("CREATE TABLE dumps (id TEXT PRIMARY KEY, file_path TEXT NOT NULL, size_bytes INTEGER NOT NULL, sha256 TEXT, "
                          "dump_type TEXT NOT NULL, compression TEXT, state TEXT NOT NULL, detected_at_utc TEXT NOT NULL, ask_deadline_utc TEXT, "
                          "next_attempt_utc TEXT, submitted_at_utc TEXT, retry_count INTEGER NOT NULL DEFAULT 0, last_error TEXT, "
                          "central_copied_at_utc TEXT, central_copy_error TEXT); CREATE TABLE meta (k TEXT PRIMARY KEY, v TEXT);"
                          "INSERT INTO dumps (id, file_path, size_bytes, dump_type, state, detected_at_utc) VALUES ('d1','/x/core',5,'LinuxUserCore','Queued','2026-01-01T00:00:00+00:00');")
        con.commit()
        con.close()
        q = da.Queue(db)
        row = q.get("d1")
        self.assertEqual((row["kind"], row["manifest"]), ("dump", None))
        q2 = da.Queue(db)  # opening it again is harmless
        self.assertEqual(q2.get("d1")["kind"], "dump")

    def test_once_mode_warns_that_it_does_not_trace(self):
        a = self.agent()
        with self.assertLogs("dumptriage", "WARNING") as logs:
            a.run(once=True)
        self.assertIn("run --once` does not trace", "\n".join(logs.output))
        self.assertIsNone(a.trace_state.recorder)

    def test_a_tracing_fault_does_not_stop_dumps_being_submitted(self):
        a = self.agent()
        calls = {"trace": 0, "submit": 0}

        def boom():
            calls["trace"] += 1
            raise RuntimeError("proc vanished")

        a.trace_tick = boom
        a.submit_due = lambda: calls.__setitem__("submit", calls["submit"] + 1)
        da.time.sleep, orig = (lambda s: None), da.time.sleep
        self.addCleanup(setattr, da.time, "sleep", orig)
        with self.assertLogs("dumptriage", "ERROR"):
            a.run(stop=lambda: calls["trace"] >= 2)
        self.assertEqual(calls, {"trace": 2, "submit": 2})

    def test_the_host_token_is_random_stable_and_not_a_hostname_hash(self):
        a = self.agent()
        token = a.host_token()
        self.assertEqual(a.host_token(), token)
        import hashlib
        import socket
        self.assertNotEqual(token, hashlib.sha256(socket.gethostname().encode()).hexdigest()[:16])
        self.assertEqual(self.agent().host_token(), token)  # kept in the queue's meta across restarts

    def test_a_hang_event_trigger_has_no_linux_source_and_says_so(self):
        a = self.agent({"triggers": [{"name": "hang", "type": "hangEvent"}, {"name": "cpu", "type": "cpuPercent", "threshold": 99}]})
        with self.assertLogs("dumptriage", "WARNING") as logs:
            self.advance(a, 5)
        self.assertIn("hangEvent) has no source on Linux", "\n".join(logs.output))


if __name__ == "__main__":
    unittest.main()
