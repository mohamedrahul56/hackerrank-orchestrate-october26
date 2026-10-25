"""Invariants that must hold for EVERY output row, checked independently of auditor/validate.py (own tables, own code), on
  * the pipeline's fresh answers for the 518 requests and the 32 samples,
  * the committed output.csv,
  * the 32 gold answers (they prove the invariants themselves are right),
  * the 23 synthetic cases.
A crafted bad row for each invariant proves the checker can fail."""
import csv
import json
import tempfile
import unittest
from pathlib import Path

from tests._common import DATASET, ROOT, needs_dataset
import extra_cases as xc

# AGENTS.md 6.3 / 6.4, typed out again on purpose (not imported from auditor.schema): phase order, operation order inside a phase, state of each operation.
PHASE_ORDER = ["FINDING", "REMEDIATION", "VERIFICATION"]
OPERATIONS = [  # (operation, phase, state) in the canonical order of problem_statement.md
    ("REPORT_REMAINING_RECORD", "FINDING", "PRESENT"), ("REPORT_EXPOSED_PII", "FINDING", "PRESENT"), ("REPORT_ACTIVE_DATA_FLOW", "FINDING", "ACTIVE"),
    ("REPORT_RESTORE_RISK", "FINDING", "ACTIVE"),
    ("DISABLE_DATA_FLOW", "REMEDIATION", "DISABLED"), ("BLOCK_RESTORE", "REMEDIATION", "BLOCKED"), ("DELETE_RECORD", "REMEDIATION", "ABSENT"),
    ("REDACT_RECORD", "REMEDIATION", "REDACTED"), ("ANONYMIZE_RECORD", "REMEDIATION", "ANONYMIZED"),
    ("VERIFY_RECORD_ABSENT", "VERIFICATION", "ABSENT"), ("VERIFY_RECORD_PRESENT", "VERIFICATION", "PRESENT"), ("VERIFY_RECORD_REDACTED", "VERIFICATION", "REDACTED"),
    ("VERIFY_RECORD_ANONYMIZED", "VERIFICATION", "ANONYMIZED"), ("VERIFY_DATA_FLOW_DISABLED", "VERIFICATION", "DISABLED"), ("VERIFY_RESTORE_BLOCKED", "VERIFICATION", "BLOCKED"),
]
OP_RANK = {op: i for i, (op, _, _) in enumerate(OPERATIONS)}
TRIPLES = {(ph, op, st) for op, ph, st in OPERATIONS}
FIELDS = ["step_no", "phase", "system_id", "resource_id", "operation", "state", "evidence_id"]
FIX_OF_FINDING = {"REPORT_REMAINING_RECORD": {"DELETE_RECORD"}, "REPORT_EXPOSED_PII": {"REDACT_RECORD", "ANONYMIZE_RECORD"},
                  "REPORT_ACTIVE_DATA_FLOW": {"DISABLE_DATA_FLOW"}, "REPORT_RESTORE_RISK": {"BLOCK_RESTORE"}}


def violations(status, steps, citable=None) -> list[str]:
    """Every broken invariant of one row. `citable(evidence_id)` returns the capture time of an id (or None); None disables the existence check."""
    out = []
    if status not in ("COMPLETE", "INCOMPLETE", "NOT_APPLICABLE"):
        return [f"status {status!r}"]
    if status == "NOT_APPLICABLE":
        return ["NOT_APPLICABLE row is not []"] if steps != [] else []
    if not steps:
        return [f"{status} row has no steps"]
    for i, s in enumerate(steps):
        if set(s) != set(FIELDS):          # key order is irrelevant (AGENTS.md 6.3)
            out.append(f"step {i + 1}: fields {list(s)}")
            return out
    if [s["step_no"] for s in steps] != list(range(1, len(steps) + 1)):
        out.append("step_no is not 1..n in array order")
    phases = [PHASE_ORDER.index(s["phase"]) for s in steps]
    if phases != sorted(phases):
        out.append("phases are not in order FINDING, REMEDIATION, VERIFICATION")
    keys = [(PHASE_ORDER.index(s["phase"]), OP_RANK[s["operation"]], s["system_id"], s["resource_id"]) for s in steps]
    if keys != sorted(keys):
        out.append("steps are not in canonical order (phase, operation, system_id, resource_id)")
    if len(set(keys)) != len(keys):
        out.append("duplicate step")
    for s in steps:
        if (s["phase"], s["operation"], s["state"]) not in TRIPLES:
            out.append(f"step {s['step_no']}: {s['phase']}/{s['operation']}/{s['state']} is not an allowed combination")
        if not s["system_id"] or not s["resource_id"]:
            out.append(f"step {s['step_no']}: empty system_id/resource_id")
    if status == "COMPLETE":
        if {s["phase"] for s in steps} != {"VERIFICATION"}:
            out.append("COMPLETE row contains a non-VERIFICATION step")
        for s in steps:
            if not s["evidence_id"]:
                out.append(f"COMPLETE step {s['step_no']} ({s['operation']} {s['resource_id']}) has no evidence_id")
    else:
        if {s["phase"] for s in steps} != set(PHASE_ORDER):
            out.append("INCOMPLETE row lacks one of the three phases")
        for s in steps:
            if s["phase"] == "FINDING" and not s["evidence_id"]:
                out.append(f"finding {s['step_no']} has no evidence_id")
            if s["phase"] != "FINDING" and s["evidence_id"]:
                out.append(f"future-work step {s['step_no']} carries evidence_id {s['evidence_id']!r}")
        for f in (s for s in steps if s["phase"] == "FINDING"):
            fixes = {s["operation"] for s in steps if s["phase"] == "REMEDIATION" and (s["system_id"], s["resource_id"]) == (f["system_id"], f["resource_id"])}
            if not fixes & FIX_OF_FINDING[f["operation"]]:
                out.append(f"finding {f['operation']} {f['resource_id']} has no matching remediation")
    if citable is not None:
        for s in steps:
            if s["evidence_id"] and citable(s["evidence_id"]) is None:
                out.append(f"step {s['step_no']} cites unknown id {s['evidence_id']!r}")
    return out


def check_rows(rows, audit_at=None, citable=None) -> dict[str, list[str]]:
    bad = {}
    for cid, status, steps in rows:
        v = violations(status, steps, citable)
        if audit_at and citable:
            for s in steps:
                t = citable(s["evidence_id"]) if s["evidence_id"] else None
                if t is not None and t > audit_at[cid]:
                    v.append(f"step {s['step_no']} cites {s['evidence_id']} captured after audit_at")
        if v:
            bad[cid] = v
    return bad


class TheCheckerCanFail(unittest.TestCase):
    """One crafted bad row per invariant: the invariant checker must report it (so a green run means something)."""

    def step(self, no, phase, op, rid="x_1", system="s", ev=""):
        return {"step_no": no, "phase": phase, "system_id": system, "resource_id": rid, "operation": op, "state": dict((o, st) for o, _, st in OPERATIONS)[op], "evidence_id": ev}

    def good(self):
        return [self.step(1, "FINDING", "REPORT_REMAINING_RECORD", ev="e1"), self.step(2, "REMEDIATION", "DELETE_RECORD"), self.step(3, "VERIFICATION", "VERIFY_RECORD_ABSENT")]

    def test_good_row_has_no_violation(self):
        self.assertEqual(violations("INCOMPLETE", self.good()), [])

    def test_each_invariant_is_detected(self):
        g = self.good()
        cases = {
            "phase order": ("INCOMPLETE", [g[1], g[0], g[2]]),
            "step_no": ("INCOMPLETE", [{**g[0]}, {**g[1], "step_no": 3}, {**g[2], "step_no": 2}]),
            "canonical order": ("INCOMPLETE", [g[0], self.step(2, "REMEDIATION", "DELETE_RECORD", rid="b"), self.step(3, "REMEDIATION", "DISABLE_DATA_FLOW", rid="a"), self.step(4, "VERIFICATION", "VERIFY_RECORD_ABSENT")]),
            "COMPLETE verification-only": ("COMPLETE", [{**g[0]}, {**g[1]}, {**g[2], "evidence_id": "e1"}]),
            "COMPLETE needs evidence": ("COMPLETE", [self.step(1, "VERIFICATION", "VERIFY_RECORD_ABSENT")]),
            "NOT_APPLICABLE is []": ("NOT_APPLICABLE", g),
            "future work has no evidence": ("INCOMPLETE", [g[0], {**g[1], "evidence_id": "e1"}, g[2]]),
            "finding needs evidence": ("INCOMPLETE", [{**g[0], "evidence_id": ""}, g[1], g[2]]),
            "finding needs remediation": ("INCOMPLETE", [g[0], self.step(2, "REMEDIATION", "REDACT_RECORD"), g[2]]),
            "unknown evidence id": ("INCOMPLETE", [{**g[0], "evidence_id": "e_nope"}, g[1], g[2]]),
        }
        for name, (status, steps) in cases.items():
            with self.subTest(name):
                self.assertTrue(violations(status, steps, citable=lambda i: None if i == "e_nope" else 0), name)


@needs_dataset
class EveryRowOfAFreshRun(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        from auditor.audit import Auditor
        from auditor.dataset import load_dataset
        cls.a = Auditor(DATASET)
        cls.ds = load_dataset(DATASET)
        cls.samples = load_dataset(DATASET, "sample_deletion_requests.csv")
        cls.audit_at = {r.case_id: r.audit_at for r in cls.ds.requests + cls.samples.requests}
        results = {c: cls.a.audit(c) for c in cls.ds.case_order + cls.samples.case_order}
        cls.rows = [(c, r.status, r.steps) for c, r in results.items()]

    def citable(self, i):
        return self.ds.citable_time(i)

    def test_all_550_rows_hold_every_invariant(self):
        self.assertEqual(len(self.rows), 550)
        self.assertEqual(check_rows(self.rows, self.audit_at, self.citable), {})

    def test_complete_rows_are_verification_only_and_always_cite(self):
        complete = [(c, s) for c, st, steps in self.rows if st == "COMPLETE" for s in steps]
        self.assertGreater(len(complete), 400)
        self.assertEqual([(c, s["operation"]) for c, s in complete if s["phase"] != "VERIFICATION" or not s["evidence_id"]], [])

    def test_not_applicable_rows_are_empty(self):
        na = [(c, steps) for c, st, steps in self.rows if st == "NOT_APPLICABLE"]
        self.assertGreaterEqual(len(na), 10)
        self.assertEqual([c for c, steps in na if steps != []], [])

    def test_row_counts_by_status_are_all_three_kinds(self):
        self.assertEqual({st for _, st, _ in self.rows}, {"COMPLETE", "INCOMPLETE", "NOT_APPLICABLE"})


@needs_dataset
class TheCommittedFileAndTheGoldAnswers(unittest.TestCase):
    def rows_of(self, path: Path):
        with path.open(newline="", encoding="utf-8") as fh:
            return [(r["case_id"], r["deletion_status"], json.loads(r["steps_json"])) for r in csv.DictReader(fh)]

    def test_committed_output_csv_holds_every_invariant(self):
        from auditor.dataset import load_dataset
        out = ROOT / "output.csv"
        if not out.exists():
            self.skipTest("output.csv not generated")
        ds = load_dataset(DATASET)
        rows = self.rows_of(out)
        self.assertEqual([r[0] for r in rows], ds.case_order)
        self.assertEqual(check_rows(rows, {r.case_id: r.audit_at for r in ds.requests}, ds.citable_time), {})

    def test_gold_answers_hold_the_same_invariants(self):
        """The invariants are not stricter than the organisers' own answers."""
        from auditor.dataset import load_dataset
        ds = load_dataset(DATASET, "sample_deletion_requests.csv")
        rows = self.rows_of(DATASET / "sample_output.csv")
        self.assertEqual(len(rows), 32)
        self.assertEqual(check_rows(rows, {r.case_id: r.audit_at for r in ds.requests}, ds.citable_time), {})


class TheSyntheticCases(unittest.TestCase):
    def test_all_synthetic_cases_hold_every_invariant(self):
        from auditor.dataset import load_dataset
        cases, rules = xc.parse()
        for c in cases:
            with tempfile.TemporaryDirectory() as d:
                xc.build_dataset(c, Path(d), rules)
                status, steps = xc.run_pipeline(c, rules)
                ds = load_dataset(Path(d))
                bad = check_rows([(c["id"], status, steps)], {c["id"]: ds.requests[0].audit_at}, ds.citable_time)
                self.assertEqual(bad, {}, c["id"])


if __name__ == "__main__":
    unittest.main()
