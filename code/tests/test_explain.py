"""`--explain CASE_ID`: read-only, faithful, and complete. Synthetic cases (always run) plus three real cases of different kinds (need dataset/)."""
import contextlib
import hashlib
import io
import importlib.util
import tempfile
import unittest
from pathlib import Path

from tests._common import DATASET, ROOT, needs_dataset
import extra_cases as xc
from auditor.audit import Auditor
from auditor.dataset import load_dataset
from auditor.explain import explain

spec = importlib.util.spec_from_file_location("auditor_cli", ROOT / "code" / "main.py")
cli = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cli)
CASES = {c["id"]: c for c in xc.definitions()}


def synthetic(case_id: str, root: Path) -> Auditor:
    xc.build_dataset(CASES[case_id], root)
    return Auditor(root)


def snapshot(folder: Path) -> dict[str, str]:
    return {str(p.relative_to(folder)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(folder.rglob("*")) if p.is_file()}


class Invariants(unittest.TestCase):
    def check(self, a: Auditor, case_id: str, text: str):
        res = a.audit(case_id)
        self.assertIn(f"deletion_status = {res.status}", text)
        for s in res.steps:                                                   # every emitted step is listed with a reason
            self.assertIn(f"{s['operation']:<26} {s['resource_id']}", text)
        self.assertNotIn("(no reason recorded)", text)
        again = a.audit(case_id)
        self.assertEqual((res.status, res.steps), (again.status, again.steps))   # explaining changed nothing


class SyntheticKinds(Invariants):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def test_kind_1_violation_with_a_rule_and_a_citation(self):
        """XC-11: a retained legal-hold record that also carries the e-mail address."""
        a = synthetic("XC-11", self.root)
        text = explain(a, "XC-11")
        self.check(a, "XC-11", text)
        self.assertIn("katya.volkova@example.test -> user_9011", text)                      # identity resolution
        self.assertIn("the CURRENT e-mail", text)
        self.assertIn("legal_hold_1101", text)
        self.assertIn("rule_13 LEGAL_HOLD_RECORD / A supplied legal hold is active / RETAIN", text)   # the rule applied
        self.assertIn("state EXPOSED -> VIOLATION (treatment REDACT)", text)                # why it is a violation
        self.assertIn("-> state used: EXPOSED (LEGAL_HOLD_RECORD), from evidence_x2", text)
        self.assertIn("REPORT_EXPOSED_PII", text)
        self.assertIn("cites evidence_x2", text)                                            # the finding cites the file that shows it
        self.assertIn("proposed fix for the finding above", text)                           # remediation: future work, no evidence id
        self.assertIn("deletion_status = INCOMPLETE", text)

    def test_kind_2_a_record_that_needs_no_step_and_a_hold_that_expired(self):
        a = synthetic("XC-12", self.root)
        text = explain(a, "XC-12")
        self.check(a, "XC-12", text)
        self.assertIn("no step: its retention / legal hold ended 2026-08-05T00:00:00Z", text)
        self.assertNotIn("legal_hold_1201 ", text.split("5. THE STEPS")[1].split("6. WHAT")[0])   # not among the steps
        self.assertIn("deletion_status = COMPLETE", text)
        # an already-compliant DELETE-type copy is omitted with its reason
        root2 = self.root / "second"
        a2 = synthetic("XC-02", root2)
        t2 = explain(a2, "XC-02")
        self.assertIn("a compliant CACHE_ENTRY copy adds no step", t2)
        self.check(a2, "XC-02", t2)

    def test_kind_3_request_that_does_not_ask_for_deletion(self):
        a = synthetic("XC-15", self.root)
        text = explain(a, "XC-15")
        self.assertIn("decision : NOT_APPLICABLE", text)
        self.assertIn("steps_json = []", text)
        self.assertIn("does not authorize deletion", text)
        self.assertNotIn("2. WHO THE PERSON IS", text)                                      # nothing else is examined
        self.assertIn("deletion_status = NOT_APPLICABLE", text)

    def test_later_evidence_is_shown_as_ignored_for_state(self):
        a = synthetic("XC-20", self.root)
        text = explain(a, "XC-20")
        self.check(a, "XC-20", text)
        self.assertIn("captured AFTER audit_at: ignored for state", text)
        self.assertIn("-> state used: PRESENT (CACHE_ENTRY), from evidence_x1", text)
        steps = text.split("5. THE STEPS")[1].split("6. WHAT")[0]
        self.assertNotIn("evidence_x9", steps)                                              # never cited

    def test_historical_email_is_reported_as_historical(self):
        a = synthetic("XC-21", self.root)
        text = explain(a, "XC-21")
        self.assertIn("a HISTORICAL e-mail", text)
        self.assertIn("vera.old@example.test -> user_9022", text)


class ReadOnly(unittest.TestCase):
    def test_cli_explain_writes_nothing_and_keeps_an_existing_output_file(self):
        with tempfile.TemporaryDirectory() as d:
            root, work = Path(d) / "ds", Path(d) / "work"
            work.mkdir()
            xc.build_dataset(CASES["XC-01"], root)
            out = work / "output.csv"
            out.write_text("case_id,deletion_status,steps_json\nsentinel,COMPLETE,[]\n", encoding="utf-8")
            (work / "output.usage.jsonl").write_text("sentinel\n", encoding="utf-8")
            before_work, before_ds = snapshot(work), snapshot(root)
            buf, err = io.StringIO(), io.StringIO()
            with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(err):
                code = cli.main(["--dataset", str(root), "--out", str(out), "--explain", "XC-01"])
            self.assertEqual(code, 0, err.getvalue())
            self.assertIn("EXPLAIN XC-01", buf.getvalue())
            self.assertEqual(snapshot(work), before_work)                                   # no output, notes or usage file touched or created
            self.assertEqual(snapshot(root), before_ds)                                     # the dataset is read-only too

    def test_unknown_case_is_a_clean_error(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / "ds"
            xc.build_dataset(CASES["XC-01"], root)
            err = io.StringIO()
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(err):
                code = cli.main(["--dataset", str(root), "--out", str(Path(d) / "o.csv"), "--explain", "nope"])
            self.assertEqual(code, 2)
            self.assertIn("no case 'nope'", err.getvalue())
            self.assertFalse((Path(d) / "o.csv").exists())

    def test_instrumentation_does_not_change_the_normal_run(self):
        """The trace is passed on every run; the committed output must still be what a fresh run writes (CliMatchesLibrary covers the CLI)."""
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / "ds"
            a = synthetic("XC-14", root)
            res = a.audit("XC-14")
            self.assertEqual(res.status, "INCOMPLETE")
            self.assertEqual([s["step_no"] for s in res.steps], list(range(1, len(res.steps) + 1)))


@needs_dataset
class RealCasesOfThreeKinds(Invariants):
    @classmethod
    def setUpClass(cls):
        cls.a = Auditor(DATASET)

    def test_kind_1_workflow_says_complete_but_evidence_shows_remaining_copies(self):
        text = explain(self.a, "case_36")
        self.check(self.a, "case_36", text)
        self.assertIn("reported_status     : COMPLETE", text)
        self.assertIn("the workflow REPORTED COMPLETE; that claim was not used as proof. The evidence at audit_at says INCOMPLETE.", text)
        self.assertIn("rule_09 USER_EXPORT / Always / DELETE; state PRESENT -> VIOLATION", text)
        self.assertIn("rule_03 SEARCH_INDEX_ENTRY", text)
        self.assertIn("rule_12 SECURITY_AUDIT_LOG", text)
        self.assertIn("evidence_03", text)

    def test_kind_2_account_found_through_the_activity_timeline_with_a_historical_email(self):
        text = explain(self.a, "case_48")
        self.check(self.a, "case_48", text)
        self.assertIn("a HISTORICAL e-mail", text)
        self.assertIn("account discovery   : phase -> account_483", text)
        self.assertIn("worker delete event", text)
        self.assertIn("no evidence file observes this resource", text)
        self.assertIn("treated as deleted (claim from the worker's delete event)", text)
        self.assertIn("`--no-timing-heuristics` switches it off", text)
        self.assertIn("REPORT_ACTIVE_DATA_FLOW", text)
        self.assertIn("DISABLE_FLOW", text)

    def test_kind_3_account_left_out_because_candidates_cannot_be_separated(self):
        text = explain(self.a, "case_58")
        self.check(self.a, "case_58", text)
        self.assertIn("account discovery   : ambiguous; candidates ['account_5715', 'account_9862']", text)
        self.assertIn("NO account step is emitted", text)
        self.assertIn("account: no account step (ambiguous), see section 3", text)
        self.assertIn("anonymized analytics: several candidates fit", text)
        self.assertIn("the workflow REPORTED NOT_STARTED", text)

    def test_provisional_row_is_labelled_as_such(self):
        text = explain(self.a, "case_34")
        self.check(self.a, "case_34", text)
        self.assertIn("PROVISIONAL", text)
        self.assertIn("evidence_04", text)

    def test_every_case_of_the_main_set_can_be_explained_and_agrees_with_its_row(self):
        ds = load_dataset(DATASET)
        for cid in ds.case_order:
            text = explain(self.a, cid)
            res = self.a.audit(cid)
            self.assertIn(f"deletion_status = {res.status}", text, cid)
            self.assertNotIn("(no reason recorded)", text, cid)
            self.assertEqual(sum(1 for line in text.splitlines() if line.startswith("  #")), len(res.steps), cid)

    def test_the_repository_run_files_are_untouched_by_explain(self):
        names = ["output.csv", "output.notes.md", "output.usage.jsonl"]
        present = [ROOT / n for n in names if (ROOT / n).exists()]
        before = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in present}
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(cli.main(["--explain", "case_48"]), 0)
        self.assertEqual({p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in present}, before)
        self.assertEqual(sorted(p.name for p in ROOT.glob("output*")), sorted(p.name for p in present))


if __name__ == "__main__":
    unittest.main()
