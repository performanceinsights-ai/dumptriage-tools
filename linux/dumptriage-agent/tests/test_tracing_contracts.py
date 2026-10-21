"""The shared tracing contracts (dumptriage-tools#26, website#1999): the config and manifest
schemas and tests/fixtures/tracing/triggers.json, which the Windows (xUnit) and macOS (bash)
suites load too. The reference evaluator below is the fixture's definition of correct; the
Linux agent's trigger loop (#27) is tested against the same file."""
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
FIXTURE = ROOT / "tests" / "fixtures" / "tracing" / "triggers.json"
CONFIG_SCHEMA = ROOT / "schemas" / "tracing-config.schema.json"
MANIFEST_SCHEMA = ROOT / "schemas" / "trace-manifest.schema.json"

try:
    import jsonschema
except ImportError:  # the agent is standard-library only; the schema checks need the package
    jsonschema = None

OUTCOMES = {"fire", "no_fire", "cooldown", "capped"}


def evaluate(case):
    """The semantics written in the fixture's `semantics` list."""
    trig, limits = case["trigger"], case["limits"]
    run_start = None
    for s in case["series"]:
        if s["value"] < trig["threshold"]:
            run_start = None
            continue
        if run_start is None:
            run_start = s["tSec"]
        if s["tSec"] - run_start < trig["sustainSec"]:
            continue
        t = s["tSec"]
        past = [h["atSec"] for h in case["history"]]
        if any(a > t - limits["cooldownMin"] * 60 for a in past):
            return {"outcome": "cooldown", "atSec": t}
        if sum(1 for a in past if a > t - 86400) >= limits["dailyCap"]:
            return {"outcome": "capped", "atSec": t}
        return {"outcome": "fire", "atSec": t}
    return {"outcome": "no_fire"}


def manifest():
    return {
        "schemaVersion": 1, "traceType": "perf_script_text",
        "trigger": {"name": "cpu-high", "type": "cpuPercent", "threshold": 90, "observed": 97.5},
        "window": {"startUtc": "2026-10-21T10:00:00Z", "stopUtc": "2026-10-21T10:00:30Z", "lookbackSec": 20},
        "tool": {"name": "perf", "version": "6.8.12", "command": "perf record -F 99 -a -g --overwrite"},
        "os": {"family": "linux", "build": "6.8.0-147-generic"},
        "hostToken": "3f2a9c", "agentVersion": "0.5.0",
    }


class FixtureTests(unittest.TestCase):
    def setUp(self):
        self.doc = json.loads(FIXTURE.read_text())

    def test_cases_are_well_formed_and_named_once(self):
        names = [c["name"] for c in self.doc["cases"]]
        self.assertEqual(len(names), len(set(names)))
        for c in self.doc["cases"]:
            self.assertIn(c["expect"]["outcome"], OUTCOMES, c["name"])
            self.assertEqual("atSec" in c["expect"], c["expect"]["outcome"] != "no_fire", c["name"])
            self.assertEqual([s["tSec"] for s in c["series"]], sorted(s["tSec"] for s in c["series"]), c["name"])
            self.assertGreaterEqual(c["limits"]["cooldownMin"], 0)

    def test_every_outcome_is_covered(self):
        self.assertEqual({c["expect"]["outcome"] for c in self.doc["cases"]}, OUTCOMES)

    def test_reference_evaluator_agrees_with_every_case(self):
        for c in self.doc["cases"]:
            with self.subTest(c["name"]):
                self.assertEqual(evaluate(c), c["expect"])

    @unittest.skipIf(jsonschema is None, "jsonschema not installed")
    def test_fixture_triggers_and_limits_satisfy_the_config_schema(self):
        schema = json.loads(CONFIG_SCHEMA.read_text())
        for c in self.doc["cases"]:
            with self.subTest(c["name"]):
                jsonschema.validate({"enabled": True, "backend": "perf", "triggers": [c["trigger"]], "limits": c["limits"]}, schema)


@unittest.skipIf(jsonschema is None, "jsonschema not installed")
class SchemaTests(unittest.TestCase):
    def setUp(self):
        self.config = json.loads(CONFIG_SCHEMA.read_text())
        self.manifest = json.loads(MANIFEST_SCHEMA.read_text())

    def test_schemas_are_valid_schemas(self):
        jsonschema.Draft202012Validator.check_schema(self.config)
        jsonschema.Draft202012Validator.check_schema(self.manifest)

    def test_a_manifest_validates_and_the_hostname_has_no_field(self):
        jsonschema.validate(manifest(), self.manifest)
        self.assertNotIn("hostname", self.manifest["properties"])
        bad = dict(manifest(), hostname="web-01")
        with self.assertRaises(jsonschema.ValidationError):
            jsonschema.validate(bad, self.manifest)

    def test_a_manifest_missing_its_command_is_rejected(self):
        m = manifest()
        del m["tool"]["command"]
        with self.assertRaises(jsonschema.ValidationError):
            jsonschema.validate(m, self.manifest)

    def test_the_default_config_is_off(self):
        jsonschema.validate({"enabled": False}, self.config)
        self.assertIs(self.config["properties"]["enabled"]["default"], False)
        self.assertEqual(self.config["properties"]["submission"]["default"], "AskFirst")

    def test_trigger_rules(self):
        ok = [{"name": "p", "type": "process", "process": "sqlservr", "threshold": 80, "sustainSec": 20},
              {"name": "h", "type": "hangEvent"},
              {"name": "s", "type": "schedule", "intervalMin": 1440}]
        bad = [{"name": "no-threshold", "type": "cpuPercent"},
               {"name": "no-process", "type": "process", "threshold": 80},
               {"name": "no-interval", "type": "schedule"},
               {"name": "psi-over-100", "type": "psiSomeAvg10", "threshold": 150},
               {"name": "bad type", "type": "gpu", "threshold": 1}]
        for t in ok:
            jsonschema.validate({"enabled": True, "triggers": [t]}, self.config)
        for t in bad:
            with self.subTest(t["name"]), self.assertRaises(jsonschema.ValidationError):
                jsonschema.validate({"enabled": True, "triggers": [t]}, self.config)


if __name__ == "__main__":
    unittest.main()
