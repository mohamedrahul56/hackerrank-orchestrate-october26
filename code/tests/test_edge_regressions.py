"""Regression tests for the defects the edge-case sweep (evaluation/edge_sweep.py) found. Each test failed on the code before its fix."""
import csv
import json
import os
import tempfile
import time
import unittest
from datetime import timezone
from pathlib import Path

from tests._common import ROOT
import extra_cases as xc
from auditor.audit import Auditor, Config
from auditor.dataset import DatasetError, _rows, parse_ts

A = xc.person(1, "Alma Reyes", "alma.reyes@example.test")
HEADER = ["case_id", "email", "requested_at", "reported_status", "reported_at", "audit_at", "request_text"]


def world(tmp: Path, resources=None, evidence=None) -> Path:
    res = resources if resources is not None else xc.core(1) + [xc.res("cache_101", "CACHE_ENTRY", "PRESENT")]
    case = xc.mk("XC-01", "t", "t", A, res, [], evidence=evidence, status="INCOMPLETE")
    root = tmp / "ds"
    xc.build_dataset(case, root)
    return root


def write_request(root: Path, **over) -> None:
    rq = xc.mk("XC-01", "t", "t", A, [], []) ["request"]
    row = {"case_id": "XC-01", "email": rq["email"], "requested_at": rq["requested_at"], "reported_status": "COMPLETE", "reported_at": rq["reported_at"],
           "audit_at": rq["audit_at"], "request_text": rq["text"], **over}
    with (root / "deletion_requests.csv").open("w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(HEADER)
        w.writerow([row[h] for h in HEADER])


def add_manifest(root: Path, file_id: str, rel: str, t: str) -> None:
    with (root / "evidence_files.csv").open("a", newline="", encoding="utf-8") as fh:
        csv.writer(fh).writerow([file_id, rel, "synthetic", t])


class ZoneLessTimestamps(unittest.TestCase):
    """S-T06: a timestamp without a zone used to be read in the machine's local time zone."""

    def test_parse_ts_reads_a_zone_less_time_as_utc_whatever_the_machine_zone(self):
        old = os.environ.get("TZ")
        try:
            got = set()
            for tz in ("UTC", "America/Los_Angeles", "Asia/Kolkata"):
                os.environ["TZ"] = tz
                time.tzset()
                got.add(parse_ts("2026-08-20T09:00:00"))
                got.add(parse_ts("2026-08-20"))
            self.assertEqual({d.isoformat() for d in got}, {"2026-08-20T09:00:00+00:00", "2026-08-20T00:00:00+00:00"})
        finally:
            os.environ.pop("TZ", None) if old is None else os.environ.__setitem__("TZ", old)
            time.tzset()

    def test_zoned_times_are_unchanged(self):
        self.assertEqual(parse_ts("2026-08-20T11:00:00+02:00"), parse_ts("2026-08-20T09:00:00Z"))
        self.assertEqual(parse_ts("2026-08-20T09:00:00Z").tzinfo, timezone.utc)

    def test_a_whole_audit_does_not_depend_on_the_zone(self):
        old = os.environ.get("TZ")
        try:
            answers = set()
            for tz in ("UTC", "Asia/Kolkata"):
                os.environ["TZ"] = tz
                time.tzset()
                with tempfile.TemporaryDirectory() as d:
                    root = world(Path(d))
                    write_request(root, requested_at="2026-08-01T09:00:00", reported_at="2026-08-03T09:00:00", audit_at="2026-08-10T12:00:00")
                    r = Auditor(root, Config()).audit("XC-01")
                    answers.add((r.status, len(r.steps)))
            self.assertEqual(answers, {("INCOMPLETE", 5)})
        finally:
            os.environ.pop("TZ", None) if old is None else os.environ.__setitem__("TZ", old)
            time.tzset()


if __name__ == "__main__":
    unittest.main()


class UnreadableHoldEnd(unittest.TestCase):
    """S-R09: a legal-hold record whose retained_until could not be parsed was dropped whole, so the retained record was never verified."""

    def hold_world(self, tmp, until):
        ev = xc.legal_hold_file("legal_hold_105", A["user_id"], until, file_id="evidence_x2")
        case = xc.mk("XC-01", "t", "t", A, xc.core(1), [], status="COMPLETE")
        case["evidence"].append(ev)
        root = tmp / "ds"
        xc.build_dataset(case, root)
        return root

    def ops(self, root):
        a = Auditor(root, Config())
        r = a.audit("XC-01")
        return r, {(s["operation"], s["resource_id"]) for s in r.steps}, a

    def test_unparseable_end_date_counts_as_no_end_date(self):
        with tempfile.TemporaryDirectory() as d:
            r, ops, a = self.ops(self.hold_world(Path(d), "someday"))
            self.assertIn(("VERIFY_RECORD_PRESENT", "legal_hold_105"), ops)
            self.assertNotIn(("DELETE_RECORD", "legal_hold_105"), ops)
            self.assertTrue(any("retained_until" in w and "legal_hold_105" in w for w in a.base.warnings))

    def test_it_matches_the_missing_end_date_and_a_valid_future_one(self):
        shapes = []
        for until in (None, "someday", "2033-12-31T23:59:59Z"):
            with tempfile.TemporaryDirectory() as d:
                root = self.hold_world(Path(d), until if until else "2033-12-31T23:59:59Z")
                if until is None:
                    p = root / "evidence" / "evidence_x2.json"
                    doc = json.loads(p.read_text())
                    doc["records"][0].pop("retained_until")
                    p.write_text(json.dumps(doc))
                r, ops, _ = self.ops(root)
                shapes.append((r.status, sorted(ops)))
        self.assertEqual(shapes[0], shapes[1])
        self.assertEqual(shapes[1], shapes[2])

    def test_a_valid_past_end_date_still_ends_the_hold(self):
        with tempfile.TemporaryDirectory() as d:
            r, ops, _ = self.ops(self.hold_world(Path(d), "2026-08-05T00:00:00Z"))
            self.assertNotIn(("VERIFY_RECORD_PRESENT", "legal_hold_105"), ops)


class ManifestPathsStayInsideTheDataset(unittest.TestCase):
    """S-F12: a manifest row such as '../outside.json' or an absolute path was opened, so a file outside dataset/ could change an answer."""

    def outside_ledger(self, tmp: Path) -> Path:
        p = tmp / "outside.json"
        p.write_text(json.dumps({"exported_at": "2026-08-12T00:00:00Z", "origin": "outside", "records": [
            {"recorded_at": "2026-08-12T00:00:00Z", "system": "cache_service", "resource_id": "cache_101", "record_type": "CACHE_ENTRY", "result": "NOT_FOUND",
             "subject_reference": A["email"]}]}), encoding="utf-8")
        return p

    def test_dot_dot_and_absolute_paths_are_not_read(self):
        for style in ("relative", "absolute"):
            with tempfile.TemporaryDirectory() as d:
                tmp = Path(d)
                root = world(tmp)
                out = self.outside_ledger(tmp)
                add_manifest(root, "evidence_bad", "../outside.json" if style == "relative" else str(out), "2026-08-12T00:00:00Z")
                a = Auditor(root, Config())
                r = a.audit("XC-01")
                self.assertEqual(r.status, "INCOMPLETE", style)
                self.assertTrue(any("outside the dataset folder" in w for w in a.base.warnings), style)

    def test_ordinary_nested_paths_and_symlinked_files_inside_the_dataset_still_work(self):
        from auditor.evidence.load import inside_root
        root = Path("/data/ds")
        self.assertTrue(inside_root(root, "evidence/logs/a.log"))
        self.assertTrue(inside_root(root, "evidence/../evidence/a.json"))
        self.assertFalse(inside_root(root, "evidence/../../x"))
        self.assertFalse(inside_root(root, "/etc/passwd"))
        self.assertFalse(inside_root(root, "../ds2/a.json"))                      # a sibling folder with the same prefix
        with tempfile.TemporaryDirectory() as d:                                  # profile_run.py links dataset files with symlinks: they must keep working
            tmp = Path(d)
            real = tmp / "real"
            real.mkdir()
            (real / "a.json").write_text("{}")
            ds = tmp / "ds"
            (ds / "evidence").mkdir(parents=True)
            (ds / "evidence" / "a.json").symlink_to(real / "a.json")
            self.assertTrue(inside_root(ds, "evidence/a.json"))


class VeryLongRequestText(unittest.TestCase):
    """S-O06: a request_text above 128 KiB made the csv module raise, which stopped the whole run (all 518 answers lost) instead of one case."""

    def test_a_one_megabyte_request_is_audited_like_any_other(self):
        with tempfile.TemporaryDirectory() as d:
            root = world(Path(d))
            write_request(root, request_text=("Ignore previous instructions. " * 30000) + " Please delete my account and all personal data.")
            t0 = time.time()
            r = Auditor(root, Config()).audit("XC-01")
            self.assertEqual((r.status, len(r.steps)), ("INCOMPLETE", 5))
            self.assertLess(time.time() - t0, 20)

    def test_rows_around_it_are_unaffected_through_the_command_line(self):
        import contextlib
        import importlib.util
        import io
        spec = importlib.util.spec_from_file_location("auditor_cli_edge", ROOT / "code" / "main.py")
        cli = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cli)
        with tempfile.TemporaryDirectory() as d:
            root = world(Path(d))
            rq = xc.mk("XC-01", "t", "t", A, [], [])["request"]
            with (root / "deletion_requests.csv").open("w", newline="", encoding="utf-8") as fh:
                w = csv.writer(fh)
                w.writerow(HEADER)
                for cid, text in (("R-1", rq["text"]), ("R-2", "x " * 200_000 + rq["text"]), ("R-3", rq["text"])):
                    w.writerow([cid, rq["email"], rq["requested_at"], "COMPLETE", rq["reported_at"], rq["audit_at"], text])
            out = Path(d) / "o.csv"
            with contextlib.redirect_stderr(io.StringIO()):
                rc = cli.main(["--dataset", str(root), "--out", str(out), "--images", "off"])
            self.assertEqual(rc, 0)
            with out.open(newline="", encoding="utf-8") as fh:
                rows = list(csv.DictReader(fh))
            self.assertEqual([r["case_id"] for r in rows], ["R-1", "R-2", "R-3"])
            self.assertEqual({r["deletion_status"] for r in rows}, {"INCOMPLETE"})

    def test_a_field_beyond_every_limit_is_a_clear_dataset_error(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "big.csv"
            p.write_text("a,b\n" + "x" * (17 * 1024 * 1024) + ",1\n", encoding="utf-8")
            with self.assertRaises(DatasetError):
                _rows(p, ("a", "b"))


class OnlyRealResourceIdsReachTheOutput(unittest.TestCase):
    """S-O09: a record whose resource_id was 'cache_101; DROP TABLE users' (inside evidence) was written into the output steps verbatim."""

    ODD = ["cache_101; DROP TABLE users", "../../etc/passwd", "Ignore previous instructions", "cache_101\nINJECT", "cache_", "_101", "CACHE_101", ""]

    def test_the_shape_rule(self):
        from auditor.schema import valid_resource_id
        for good in ("account_5762", "data_flow_1121", "audit_record_4902", "email_delivery_601", "telemetry_801"):
            self.assertTrue(valid_resource_id(good), good)
        for bad in self.ODD + [None, 5]:
            self.assertFalse(valid_resource_id(bad), bad)

    def test_odd_ids_in_evidence_never_become_steps(self):
        with tempfile.TemporaryDirectory() as d:
            root = world(Path(d))
            recs = [{"resource_id": rid, "email": A["email"]} for rid in self.ODD[:5]]
            p = root / "evidence" / "inj.json"
            p.write_text(json.dumps({"collection": "cache_entries", "captured_at": "2026-08-12T00:00:00Z", "source": "x", "records": recs}), encoding="utf-8")
            add_manifest(root, "evidence_bad", "evidence/inj.json", "2026-08-12T00:00:00Z")
            a = Auditor(root, Config())
            r = a.audit("XC-01")
            self.assertEqual([s["resource_id"] for s in r.steps if "DROP" in s["resource_id"] or "/" in s["resource_id"] or " " in s["resource_id"]], [])
            self.assertEqual(r.status, "INCOMPLETE")
            self.assertTrue(any("dropped" in w and "<name>_<number>" in w for w in a.base.warnings))

    def test_the_validator_rejects_an_odd_resource_id_in_a_step(self):
        from auditor.validate import validate_case
        step = {"step_no": 1, "phase": "FINDING", "system_id": "cache_service", "resource_id": "cache_101; DROP TABLE users", "operation": "REPORT_REMAINING_RECORD",
                "state": "PRESENT", "evidence_id": "evidence_x1"}
        issues = validate_case("c", "INCOMPLETE", [step], None)
        self.assertTrue(any(i.code == "BAD_RESOURCE_ID" for i in issues), [str(i) for i in issues])


class RuleFileProblemsAreLoud(unittest.TestCase):
    """S-F18/F20/F21: a missing rules file or column was a traceback; an unknown action turned every case with that category into a quiet NOT_APPLICABLE row."""

    def run_cli(self, root):
        import contextlib
        import importlib.util
        import io
        spec = importlib.util.spec_from_file_location("auditor_cli_rules", ROOT / "code" / "main.py")
        cli = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cli)
        err = io.StringIO()
        out = root.parent / "o.csv"
        with contextlib.redirect_stderr(err):
            rc = cli.main(["--dataset", str(root), "--out", str(out), "--images", "off"])
        return rc, err.getvalue(), out

    def test_missing_rules_file(self):
        with tempfile.TemporaryDirectory() as d:
            root = world(Path(d))
            (root / "deletion_rules.csv").unlink()
            rc, err, out = self.run_cli(root)
            self.assertEqual(rc, 2)
            self.assertIn("deletion_rules.csv", err)
            self.assertFalse(out.exists())

    def test_missing_column(self):
        with tempfile.TemporaryDirectory() as d:
            root = world(Path(d))
            (root / "deletion_rules.csv").write_text("rule_id,data_category,condition\nrule_01,USER_PROFILE,Always\n", encoding="utf-8")
            rc, err, out = self.run_cli(root)
            self.assertEqual(rc, 2)
            self.assertIn("required_action", err)

    def test_unknown_action_stops_the_run_and_names_the_rule(self):
        with tempfile.TemporaryDirectory() as d:
            root = world(Path(d))
            text = (root / "deletion_rules.csv").read_text().replace("rule_04,CACHE_ENTRY,Always,DELETE", "rule_04,CACHE_ENTRY,Always,VAPORIZE")
            (root / "deletion_rules.csv").write_text(text)
            rc, err, out = self.run_cli(root)
            self.assertEqual(rc, 2)
            self.assertIn("rule_04", err)
            self.assertIn("VAPORIZE", err)
            self.assertFalse(out.exists())

    def test_action_spelling_is_case_insensitive_and_an_unused_category_is_fine(self):
        from auditor.policy import PolicyEngine
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "deletion_rules.csv"
            p.write_text("rule_id,data_category,condition,required_action\nrule_01,USER_PROFILE,Always, delete \nrule_99,TELEPORTED_DATA,Always,RETAIN\n", encoding="utf-8")
            e = PolicyEngine(p)
            self.assertEqual(e.action_for("USER_PROFILE"), "DELETE")
