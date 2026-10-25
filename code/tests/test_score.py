import copy
import tempfile
import unittest
from pathlib import Path

from tests._common import DATASET, needs_dataset
from score import Row, align, assign_splits, lcs_ratio, prf, read_rows, score


def step(no, phase, op, state, system, res, ev=""):
    return {"step_no": no, "phase": phase, "system_id": system, "resource_id": res, "operation": op, "state": state, "evidence_id": ev}


GOLD = {
    "a": Row("INCOMPLETE", [
        step(1, "FINDING", "REPORT_REMAINING_RECORD", "PRESENT", "search_service", "search_1", "evidence_01"),
        step(2, "REMEDIATION", "DELETE_RECORD", "ABSENT", "search_service", "search_1"),
        step(3, "VERIFICATION", "VERIFY_RECORD_ABSENT", "ABSENT", "search_service", "search_1"),
    ]),
    "b": Row("NOT_APPLICABLE", []),
}


class ScoreTests(unittest.TestCase):
    def test_perfect(self):
        m = score(GOLD, copy.deepcopy(GOLD), ["a", "b"])
        self.assertEqual(m.status_correct, 2)
        self.assertEqual(m.step_strict, (1.0, 1.0, 1.0))
        self.assertEqual(m.exact_cases, 2)
        self.assertEqual(m.mean_lcs_full, 1.0)
        self.assertTrue(all(v == 1.0 for v in m.field_acc.values()))

    def test_wrong_status_and_missing_row(self):
        pred = {"a": Row("COMPLETE", [])}
        m = score(GOLD, pred, ["a", "b"])
        self.assertEqual(m.status_correct, 0)
        self.assertEqual(m.missing_cases, 1)
        self.assertEqual(m.confusion[("INCOMPLETE", "COMPLETE")], 1)
        self.assertEqual(m.confusion[("NOT_APPLICABLE", "(missing)")], 1)

    def test_order_only_error_keeps_set_metrics_but_not_sequence(self):
        pred = copy.deepcopy(GOLD)
        pred["a"].steps = list(reversed(pred["a"].steps))
        m = score(GOLD, pred, ["a", "b"])
        self.assertEqual(m.step_strict[2], 1.0)
        self.assertEqual(m.exact_cases, 1)
        self.assertLess(m.mean_lcs_full, 1.0)
        self.assertTrue([c for c in m.cases if c.case_id == "a"][0].order_only)

    def test_evidence_only_error_hits_strict_not_relaxed(self):
        pred = copy.deepcopy(GOLD)
        pred["a"].steps[0]["evidence_id"] = "evidence_02"
        m = score(GOLD, pred, ["a"])
        self.assertLess(m.step_strict[2], 1.0)
        self.assertEqual(m.step_relaxed[2], 1.0)
        self.assertEqual(m.discovery[2], 1.0)
        self.assertAlmostEqual(m.field_acc["evidence_id"], 2 / 3)

    def test_alignment_survives_a_missing_step(self):
        """Dropping the first step must not zero the field accuracy of the others (index shift)."""
        pred = copy.deepcopy(GOLD)
        pred["a"].steps = pred["a"].steps[1:]
        m = score(GOLD, pred, ["a"])
        self.assertAlmostEqual(m.field_acc["resource_id"], 2 / 3)
        self.assertAlmostEqual(m.step_strict[1], 2 / 3)   # recall

    def test_align_prefers_exact_match(self):
        g = [step(1, "VERIFICATION", "VERIFY_RECORD_ABSENT", "ABSENT", "a", "x")]
        p = [step(1, "VERIFICATION", "VERIFY_RECORD_PRESENT", "PRESENT", "a", "x"), dict(g[0])]
        self.assertEqual(align(g, p)[0], p[1])

    def test_lcs_and_prf_edges(self):
        self.assertEqual(lcs_ratio([], []), 1.0)
        self.assertEqual(lcs_ratio(["a"], []), 0.0)
        self.assertAlmostEqual(lcs_ratio(list("abc"), list("acb")), 2 * 2 / 6)
        self.assertEqual(prf(0, 0, 0), (1.0, 1.0, 0.0 + 1.0))

    def test_bad_json_row_does_not_crash(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "p.csv"
            p.write_text('case_id,deletion_status,steps_json\na,INCOMPLETE,"not json"\nb,NOT_APPLICABLE,[]\n')
            rows = read_rows(p)
        m = score(GOLD, rows, ["a", "b"])
        self.assertEqual(m.bad_rows, 1)
        self.assertEqual(m.status_correct, 2)


@needs_dataset
class SplitTests(unittest.TestCase):
    def test_split_is_deterministic_disjoint_and_nontrivial(self):
        gold = read_rows(DATASET / "sample_output.csv")
        s1, s2 = assign_splits(gold), assign_splits(dict(reversed(list(gold.items()))))
        self.assertEqual(s1, s2)                                   # independent of row order
        self.assertEqual(set(s1), set(gold))
        held = [c for c, v in s1.items() if v == "heldout"]
        self.assertGreaterEqual(len(held), 8)
        statuses = {gold[c].status for c in held}
        self.assertEqual(statuses, {"COMPLETE", "INCOMPLETE"})

    def test_gold_scores_perfectly_against_itself(self):
        gold = read_rows(DATASET / "sample_output.csv")
        m = score(gold, gold, sorted(gold))
        self.assertEqual((m.status_correct, m.exact_cases, m.step_strict[2]), (32, 32, 1.0))


if __name__ == "__main__":
    unittest.main()
