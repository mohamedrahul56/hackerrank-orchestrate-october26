"""The edge-case sweep (evaluation/edge_sweep.py) must find no defect, and its committed report must describe the same scenarios."""
import re
import unittest

from tests._common import ROOT
import edge_sweep as sweep

AREAS = ("empty or missing fields", "bad timestamps", "duplicate / unknown case_id", "e-mail spelling", "old e-mails", "several requests per user", "events after audit_at",
         "failed-but-claimed-success", "resources with no identifier", "shared reported_at", "retained records with extra identifiers", "expired legal holds",
         "corrupt or missing evidence", "corrupt PNG", "prompt injection in request_text", "prompt injection in evidence")


class EdgeSweep(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.rows = sweep.run_all()

    def test_no_scenario_is_a_defect(self):
        bad = [(r.sid, r.scenario, r.observed) for r in self.rows if r.verdict == "DEFECT"]
        self.assertEqual(bad, [])

    def test_every_requested_area_is_covered(self):
        have = {r.area for r in self.rows}
        for a in AREAS:
            self.assertIn(a, have)

    def test_every_scenario_states_its_expectation_and_what_it_saw(self):
        for r in self.rows:
            self.assertTrue(r.expected.strip() and r.observed.strip(), r.sid)
            self.assertIn(r.verdict, ("OK", "DECISION"), r.sid)
            self.assertNotIn("EXCEPTION", r.observed.split(" ")[0], r.sid)

    def test_scenario_ids_are_unique(self):
        ids = [r.sid for r in self.rows]
        self.assertEqual(len(ids), len(set(ids)))

    def test_the_committed_report_lists_the_same_scenarios(self):
        text = (ROOT / "code" / "evaluation" / "edge_case_sweep.md").read_text(encoding="utf-8")
        table_ids = re.findall(r"^\| ([A-Z]\d{2}) \| ", text.split("## Defects found")[0], flags=re.M)
        self.assertEqual(table_ids, [r.sid for r in self.rows], "regenerate with: python3 code/evaluation/edge_sweep.py --before-json <before.json>")
        self.assertIn(f"Totals: {len(self.rows)} scenarios", text)


if __name__ == "__main__":
    unittest.main()
